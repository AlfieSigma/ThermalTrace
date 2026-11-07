<#
.SYNOPSIS
    把发布好的单文件 exe 安装到固定位置，并（可选）注册计划任务形式的开机自启。

.DESCRIPTION
    为什么需要这一步：
      若直接运行构建目录里的 exe，目录可能被清理，自启指向的路径会静默失效、且没有任何提示。
      正式安装位置固定为 %LOCALAPPDATA%\Programs\ThermalTrace\。

    自启为什么是计划任务而不是注册表 Run：
      程序需要管理员权限（读 MSR），Explorer 对需提权的启动项不弹 UAC、直接静默跳过，
      HKCU Run 永远不会被启动。这是自启采用计划任务的原因。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
    powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Register
#>
[CmdletBinding()]
param(
    # 安装后立即注册开机自启（会触发 UAC 提权）
    [switch]$Register,

    # 目标 exe 被正在运行的 ThermalTrace 锁住时，自动结束它再覆盖
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root 'publish\ThermalTrace.exe'

if (-not (Test-Path $src)) {
    Write-Host "[ERR] 找不到 $src —— 请先运行 scripts\publish.ps1" -ForegroundColor Red
    exit 1
}

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ThermalTrace'
$target = Join-Path $installDir 'ThermalTrace.exe'

# 正在运行的实例会锁住 exe（已知问题：publish 也常被它锁住导致挂起）
$running = Get-Process -Name ThermalTrace -ErrorAction SilentlyContinue
if ($running) {
    if (-not $Force) {
        Write-Host "[ERR] ThermalTrace 正在运行，无法覆盖 $target。" -ForegroundColor Red
        Write-Host "     请先从托盘退出，或加 -Force 自动结束进程。" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "[*] 结束运行中的 ThermalTrace（只丢失当前未落盘的时间片）..." -ForegroundColor Yellow
    # 先让任务计划程序去结束它：由计划任务以最高权限拉起的进程，
    # 普通的 Stop-Process 会被"拒绝访问"（2026-10-10 实测）。
    & schtasks /end /tn ThermalTrace 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    $still = Get-Process -Name ThermalTrace -ErrorAction SilentlyContinue
    if ($still) { $still | Stop-Process -Force; Start-Sleep -Seconds 2 }
}

if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Copy-Item -Path $src -Destination $target -Force
Write-Host "[OK] 已安装: $target" -ForegroundColor Green

if ($Register) {
    Write-Host "[*] 注册开机自启（计划任务，需要管理员权限）..." -ForegroundColor Cyan
    Start-Process -FilePath $target -ArgumentList '--install' -Verb RunAs -Wait
    Write-Host "[*] 可用以下命令复核真实状态:" -ForegroundColor Cyan
    Write-Host "     schtasks /query /tn ThermalTrace /fo LIST /v"
    Write-Host "     & '$target' --status"
}
else {
    Write-Host ""
    Write-Host "下一步（二选一）:" -ForegroundColor Yellow
    Write-Host "  1) 注册开机自启:  powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Register"
    Write-Host "  2) 手动:         右键『以管理员身份运行』$target --install"
}

Write-Host ""
Write-Host "数据目录: $env:LOCALAPPDATA\ThermalTrace\daily\" -ForegroundColor DarkGray
Write-Host "启动记录: $env:LOCALAPPDATA\ThermalTrace\logs\boot.txt" -ForegroundColor DarkGray
