#Requires -Version 5.1
<#
.SYNOPSIS
    按日期生成开发日记文件（DevLog\yyyy-MM-dd.md）。

.DESCRIPTION
    无参数执行时使用系统当天日期。若目标文件已存在则**不覆盖**，只打印现有内容，
    防止把当天已经记录的进展清空。模板取自 DevLog\_template.md。

.PARAMETER Date
    要生成的日期，格式 yyyy-MM-dd。省略时使用系统当天日期。

.EXAMPLE
    .\Scripts\New-DevLog.ps1
    生成当天的开发日记文件（已存在则打印内容）。

.EXAMPLE
    .\Scripts\New-DevLog.ps1 -Date 2024-06-01
    补建 2024-06-01 的开发日记文件。

.NOTES
    脚本为半自动化工具：只负责建骨架，具体内容仍需人工或 AI 填写。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Date = (Get-Date -Format 'yyyy-MM-dd')
)

$ErrorActionPreference = 'Stop'

# 通过脚本自身位置推导仓库根目录，保证在任意工作目录下调用都能找到 DevLog
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDir)) {
    $scriptDir = (Get-Location).Path
}
$repoRoot = Split-Path -Parent $scriptDir
$devLogDir = Join-Path $repoRoot 'DevLog'
$templatePath = Join-Path $devLogDir '_template.md'
$targetPath = Join-Path $devLogDir "$Date.md"

# 日期格式必须严格匹配，否则会生成难以排序与识别的文件名
if ($Date -notmatch '^\d{4}-\d{2}-\d{2}$') {
    Write-Error "日期格式不正确：$Date，应为 yyyy-MM-dd。"
    return
}

if (-not (Test-Path -LiteralPath $templatePath)) {
    Write-Error "未找到模板文件：$templatePath"
    return
}

if (-not (Test-Path -LiteralPath $devLogDir)) {
    New-Item -ItemType Directory -Path $devLogDir -Force | Out-Null
}

if (Test-Path -LiteralPath $targetPath) {
    # 已存在时绝不覆写：日记内容比骨架更重要
    Write-Host "开发日记已存在：$targetPath"
    Write-Host '---------------- 现有内容 ----------------'
    [System.IO.File]::ReadAllText($targetPath, [System.Text.UTF8Encoding]::new($false))
    Write-Host '------------------------------------------'
    return
}

# Windows PowerShell 5.1 的 Get-Content/Set-Content 对无 BOM 的 UTF-8 文件处理不可靠（中文会变乱码），
# 因此统一走 .NET IO 显式指定 UTF-8，并写出不带 BOM 的文件，保证 VS 与 Git 下都正常显示中文
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$template = [System.IO.File]::ReadAllText($templatePath, $utf8NoBom)
# 将模板中的日期占位符统一替换为实际日期（标题行与「## 日期」段各一处）
$content = $template.Replace('<!-- yyyy-MM-dd -->', $Date)

[System.IO.File]::WriteAllText($targetPath, $content, $utf8NoBom)
Write-Host "已创建开发日记：$targetPath"
