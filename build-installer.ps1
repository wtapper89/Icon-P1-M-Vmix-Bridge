param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Release = Join-Path $Root "release"
$Source = Join-Path $Root "installer\Setup.cs"
$Compiler = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $Compiler) {
    throw ".NET Framework C# compiler was not found."
}

New-Item -ItemType Directory -Force -Path $Release | Out-Null
$Output = Join-Path $Release "VMixMidiSurfaceBridge-Setup.exe"
$Resources = @(
    "/resource:$Root\app.py,VMixMidiSurfaceBridge.app.py",
    "/resource:$Root\bridge_core.py,VMixMidiSurfaceBridge.bridge_core.py",
    "/resource:$Root\config.example.json,VMixMidiSurfaceBridge.config.example.json",
    "/resource:$Root\requirements.txt,VMixMidiSurfaceBridge.requirements.txt",
    "/resource:$Root\install_on_windows.ps1,VMixMidiSurfaceBridge.install.ps1"
)

& $Compiler /nologo /target:winexe /optimize+ "/out:$Output" /reference:System.Windows.Forms.dll $Resources $Source
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Built $Output"
