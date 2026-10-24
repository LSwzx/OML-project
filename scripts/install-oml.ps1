param(
    [string]$InstallDir,
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'

$setup = Join-Path $PSScriptRoot 'OML-win-Setup.exe'
if (-not (Test-Path $setup)) {
    Write-Error "找不到 OML-win-Setup.exe，请把本脚本放在安装包同一目录。"
}

if (-not $InstallDir) {
    Add-Type -AssemblyName System.Windows.Forms

    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择 OML 安装目录（会直接装到这个目录，例如 D:\Apps\OML）'
    $dialog.ShowNewFolderButton = $true
    $dialog.SelectedPath = Join-Path $env:LOCALAPPDATA 'OML'

    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Host '已取消安装。'
        exit 1
    }

    $InstallDir = $dialog.SelectedPath
}

# 允许用户直接选中盘符根目录，但拒绝装到系统关键目录。
$resolved = [System.IO.Path]::GetFullPath($InstallDir)
if ($resolved -match '^[A-Za-z]:\\?$') {
    Write-Error "不要把 OML 直接装到盘符根目录（$resolved），请再建一层文件夹，例如 $resolved\OML。"
}

if (Test-Path (Join-Path $resolved 'current')) {
    Write-Error "目录 $resolved 里已经有一份 OML，请选择其它目录。"
}

Write-Host "安装到：$resolved"

$arguments = @('--installto', "`"$resolved`"")
if ($Silent) {
    $arguments += '--silent'
}

$process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru
if ($process.ExitCode -ne 0) {
    Write-Error "安装器退出码 $($process.ExitCode)"
}

Write-Host "完成。启动：$resolved\OML.exe"
