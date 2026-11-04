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

# 用 MyInvocation 取脚本自身路径（比 $PSScriptRoot 更稳，兼容 PowerShell 5.1 的所有调用方式）
$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionDir = Split-Path $scriptDir -Parent
$project     = Join-Path $solutionDir 'MaterialManagementApp\MaterialManagementApp.csproj'
$issScript   = Join-Path $scriptDir 'setup.iss'

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
    # 用管道 Out-Host 把 dotnet 的输出消费掉，避免 PowerShell 把工具输出当错误处理；
    # 同时 Start-Process 隔离反而会让后续 Get-Item / Split-Path 属性读取偶发返回 null
    dotnet restore $project --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
    dotnet publish $project -c Release -r win-x64 -p:Platform=x64 --self-contained true -v:m | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    # publish 产物路径
    $projInfo = Get-Item $project
    $publishDir = Join-Path $projInfo.DirectoryName 'bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish'
    $cleanup = @(
        @{ Sub = 'Data'; Filter = '*.db' },
        @{ Sub = 'Data'; Filter = '*.db-wal' },
        @{ Sub = 'Data'; Filter = '*.db-shm' },
        @{ Sub = 'Data'; Filter = '*.db-journal' },
        @{ Sub = 'Logs'; Filter = '*.log' },
        @{ Sub = '.';   Filter = '*.trn' }
    )
    foreach ($c in $cleanup) {
        $target = if ($c.Sub -eq '.') { $publishDir } else { Join-Path $publishDir $c.Sub }
        if (Test-Path $target) {
            Get-ChildItem $target -Filter $c.Filter -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        }
    }
    # Logs 目录若清空，一并删除——安装包不需要空目录
    $logsDir = Join-Path $publishDir 'Logs'
    if ((Test-Path $logsDir) -and -not (Get-ChildItem $logsDir -Force -ErrorAction SilentlyContinue)) {
        Remove-Item $logsDir -Force
    }
    Write-Host "      Publish output cleaned ($($cleanup.Count) patterns removed)." -ForegroundColor DarkGray
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
