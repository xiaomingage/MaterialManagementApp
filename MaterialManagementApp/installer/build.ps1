# ============================================================================
# MaterialManagementApp installer build script
# Steps: clean stale NuGet locks -> dotnet restore -> dotnet publish -> ISCC
# Usage:
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 -SkipPublish
# Prerequisite: Inno Setup 6 (https://jrsoftware.org/isdl.php)
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 can parse it
#       regardless of file encoding (no BOM required).
# ============================================================================
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$solutionDir = Split-Path $PSScriptRoot -Parent
$project     = Join-Path $solutionDir 'MaterialManagementApp\MaterialManagementApp.csproj'
$issScript   = Join-Path $PSScriptRoot 'setup.iss'

# Try both per-machine and per-user Inno Setup locations
$isccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $SkipPublish) {
    Write-Host '[1/3] Cleaning stale obj/ and NuGet lock files...' -ForegroundColor Cyan
    # Remove partial downloads left by an interrupted restore
    Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages') -Recurse -Include '*.eok','*.pyt','*.3sc','*.tmp' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem $solutionDir -Recurse -Directory -Filter 'obj' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '      Done.' -ForegroundColor DarkGray

    Write-Host '[2/3] Restoring and publishing Release self-contained (win-x64)...' -ForegroundColor Cyan
    dotnet restore $project --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
    dotnet publish $project -c Release -r win-x64 -p:Platform=x64 --self-contained true -v:m
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
} else {
    Write-Host '[1/2] Skipping publish, reusing existing publish output.' -ForegroundColor Yellow
}

if (-not $iscc) {
    throw "Inno Setup 6 not found (checked per-machine and per-user paths). Install it first: https://jrsoftware.org/isdl.php"
}

Write-Host '[3/3] Compiling installer...' -ForegroundColor Cyan
& $iscc $issScript
if ($LASTEXITCODE -ne 0) { throw "ISCC compilation failed with exit code $LASTEXITCODE" }

$output = Get-ChildItem (Join-Path $PSScriptRoot 'output\*.exe') | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ''
Write-Host "Done: $($output.FullName)" -ForegroundColor Green
