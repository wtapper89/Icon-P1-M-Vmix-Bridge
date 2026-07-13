$ErrorActionPreference = "Stop"

$AppDir = Join-Path $env:LOCALAPPDATA "VMixMidiSurfaceBridge"
$LegacyAppDir = Join-Path $env:LOCALAPPDATA "vMixXTouchBridge"
$TaskName = "vMix MIDI Surface Bridge"
$LegacyTaskName = "vMix X-Touch Bridge"
$LegacyTask = Get-ScheduledTask -TaskName $LegacyTaskName -ErrorAction SilentlyContinue
if ($LegacyTask -and $LegacyTask.State -eq "Running") {
    Stop-ScheduledTask -TaskName $LegacyTaskName
    Start-Sleep -Milliseconds 750
}
$ExistingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($ExistingTask -and $ExistingTask.State -eq "Running") {
    Stop-ScheduledTask -TaskName $TaskName
    Start-Sleep -Milliseconds 750
}
$Python = Get-Command py -ErrorAction SilentlyContinue
if (-not $Python) {
    $Python = Get-Command python -ErrorAction SilentlyContinue
}
if (-not $Python) {
    $Winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $Winget) {
        throw "Python 3 is required and Windows Package Manager was not found. Install Python 3, then run setup again."
    }
    Write-Host "Installing Python 3 for the current user..."
    & $Winget.Source install --id Python.Python.3.13 --exact --scope user --silent --accept-package-agreements --accept-source-agreements
    $PythonExe = Get-ChildItem (Join-Path $env:LOCALAPPDATA "Programs\Python\Python*\python.exe") -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    if ($PythonExe) {
        $Python = [pscustomobject]@{ Name = $PythonExe.Name; Source = $PythonExe.FullName }
    }
}
if (-not $Python) {
    throw "Python 3 could not be installed. Install Python 3, then run setup again."
}

New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
Copy-Item -Force (Join-Path $PSScriptRoot "app.py") $AppDir
Copy-Item -Force (Join-Path $PSScriptRoot "bridge_core.py") $AppDir
Copy-Item -Force (Join-Path $PSScriptRoot "requirements.txt") $AppDir
if (-not (Test-Path (Join-Path $AppDir "config.json"))) {
    $LegacyConfig = Join-Path $LegacyAppDir "config.json"
    if (Test-Path $LegacyConfig) {
        Copy-Item $LegacyConfig (Join-Path $AppDir "config.json")
    } else {
        Copy-Item (Join-Path $PSScriptRoot "config.example.json") (Join-Path $AppDir "config.json")
    }
}

$VenvPython = Join-Path $AppDir ".venv\Scripts\python.exe"
$VenvPythonw = Join-Path $AppDir ".venv\Scripts\pythonw.exe"
if (-not (Test-Path $VenvPython)) {
    if ($Python.Name -eq "py.exe") {
        & $Python.Source -3 -m venv (Join-Path $AppDir ".venv")
    } else {
        & $Python.Source -m venv (Join-Path $AppDir ".venv")
    }
}
& $VenvPython -m pip install --upgrade pip
& $VenvPython -m pip install -r (Join-Path $AppDir "requirements.txt")

$Action = New-ScheduledTaskAction -Execute $VenvPythonw -Argument "app.py" -WorkingDirectory $AppDir
$Trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$Settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $TaskName -Action $Action -Trigger $Trigger -Settings $Settings -Description "Starts the browser-configurable vMix MIDI Surface Bridge" -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
if ($LegacyTask) {
    Unregister-ScheduledTask -TaskName $LegacyTaskName -Confirm:$false
}

Write-Host "Installed and started. Open http://localhost:8091/"
$Ready = $false
for ($Attempt = 0; $Attempt -lt 60; $Attempt++) {
    try {
        $Health = Invoke-RestMethod "http://localhost:8091/api/health" -TimeoutSec 1
        if ($Health.ok) {
            $Ready = $true
            break
        }
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
if (-not $Ready) {
    Write-Warning "The bridge was installed, but the browser interface has not responded yet."
}
Start-Process "http://localhost:8091/"
