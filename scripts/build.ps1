<#
.SYNOPSIS
    还原并编译 ThermalTrace，同时生成解决方案文件（供 VS/Rider 打开）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'

# 优先使用项目本地 SDK，其次回退到系统 PATH
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[ERR] 找不到 dotnet。请先运行 scripts\install-dotnet.ps1" -ForegroundColor Red
    exit 1
}

$project = Join-Path $root 'src\ThermalTrace\ThermalTrace.csproj'
$solution = Join-Path $root 'ThermalTrace.sln'

# 首次构建时生成解决方案文件
if (-not (Test-Path $solution)) {
    Write-Host "[*] 生成解决方案文件..." -ForegroundColor Cyan
    & $dotnet new sln -n ThermalTrace -o $root | Out-Null
    & $dotnet sln $solution add $project | Out-Null
    Write-Host "[OK] 已生成 $solution" -ForegroundColor Green
}

Write-Host "[*] 还原依赖..." -ForegroundColor Cyan
& $dotnet restore $project

Write-Host "[*] 编译 ($Configuration)..." -ForegroundColor Cyan
& $dotnet build $project -c $Configuration --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERR] 编译失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 日常构建不再带 RID（发布属性只在 publish 时生效，见 BUILD.md），输出目录没有 win-x64 子层
$exe = Join-Path $root "src\ThermalTrace\bin\$Configuration\net8.0-windows\ThermalTrace.exe"
Write-Host ""
Write-Host "[OK] 编译成功" -ForegroundColor Green
Write-Host "    可执行文件: $exe"
Write-Host ""
Write-Host "下一步:" -ForegroundColor Yellow
Write-Host "  1) 右键以管理员身份运行:  ThermalTrace.exe --doctor"
Write-Host "  2) 绑定传感器:            ThermalTrace.exe --bind"
Write-Host "  3) 自检:                  ThermalTrace.exe --self-check"
Write-Host ""
Write-Host "提示: --doctor 与 --bind 需要管理员权限读取 CPU MSR。" -ForegroundColor DarkGray