param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\IconP1MVmixBridge\IconP1MVmixBridge.csproj"
$out = Join-Path $PSScriptRoot "artifacts\$Runtime"

New-Item -ItemType Directory -Force -Path $out | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -o $out

Write-Host "Built $out\IconP1MVmixBridge.exe"
