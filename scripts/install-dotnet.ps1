<#
.SYNOPSIS
    安装 .NET 8 SDK。

.DESCRIPTION
    按优先级尝试三种方式：
      1. 复用本机已安装的 .NET 8 SDK
      2. winget 安装（系统级，最稳）
      3. 官方 dotnet-install.ps1 安装到项目本地目录（免管理员，兜底）

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install-dotnet.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('auto', 'winget', 'local')]
    [string]$Method = 'auto'
)

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'

function Test-SdkReady {
    param([string]$DotnetPath)
    # 【坑】$DotnetPath 既可能是本地路径（.tools\dotnet\dotnet.exe）也可能是裸命令名（dotnet）。
    # 原来只写 Test-Path，对裸命令名永远返回 False——「复用本机已安装 SDK」这条优先级从未生效过，
    # 每次都白跑一趟 winget 再回退本地安装。故两种形态都要认。
    if (-not (Test-Path $DotnetPath) -and -not (Get-Command $DotnetPath -ErrorAction SilentlyContinue)) { return $false }
    $sdks = & $DotnetPath --list-sdks 2>$null
    return [bool]($sdks -match '^8\.')
}

# ---- 1. 复用现有 SDK ----
if ((Test-SdkReady $localDotnet) -or (Test-SdkReady 'dotnet')) {
    $exe = if (Test-SdkReady $localDotnet) { $localDotnet } else { 'dotnet' }
    Write-Host "[OK] 已存在可用的 .NET 8 SDK:" -ForegroundColor Green
    & $exe --list-sdks | ForEach-Object { Write-Host "    $_" }
    exit 0
}

# ---- 2. winget ----
if ($Method -ne 'local') {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host "[*] 通过 winget 安装 .NET 8 SDK ..." -ForegroundColor Cyan
        & winget install --id Microsoft.DotNet.SDK.8 --silent `
            --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 |
            Out-String | Write-Host

        # winget 可能需要新 shell 才能看到 PATH
        $env:Path = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                    [System.Environment]::GetEnvironmentVariable('Path', 'User')
        if (Test-SdkReady 'dotnet') {
            Write-Host "[OK] winget 安装成功" -ForegroundColor Green
            & dotnet --list-sdks | ForEach-Object { Write-Host "    $_" }
            exit 0
        }
        Write-Host "[warn] winget 未生效，转用本地安装" -ForegroundColor Yellow
    }
}

# ---- 3. 官方脚本本地安装（显式命名参数，避免数组 splat 的参数绑定歧义）----
Write-Host "[*] 回退到官方 dotnet-install.ps1，安装到: $localDotnet" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path (Split-Path $localDotnet -Parent) | Out-Null

$installer = Join-Path $env:TEMP 'dotnet-install.ps1'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if (-not (Test-Path $installer)) {
    Write-Host "[*] 下载安装脚本..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
}

# 注意：必须用显式命名参数调用。用数组 splat 时 PowerShell 会把 -Channel 的值
# 错绑到 -Quality，导致 "'8.0' is not a supported value for -Quality option"。
& $installer -Channel '8.0' -Quality 'GA' -InstallDir (Split-Path $localDotnet -Parent) -NoPath

if (Test-SdkReady $localDotnet) {
    Write-Host "[OK] 本地安装成功:" -ForegroundColor Green
    & $localDotnet --list-sdks | ForEach-Object { Write-Host "    $_" }
    Write-Host ""
    Write-Host "构建前请设置 PATH:" -ForegroundColor Yellow
    Write-Host "  `$env:PATH = '$(Split-Path $localDotnet -Parent);' + `$env:PATH"
    exit 0
}

Write-Host "[ERR] .NET 8 SDK 安装失败，请手动安装:" -ForegroundColor Red
Write-Host "  winget install Microsoft.DotNet.SDK.8" -ForegroundColor Red
Write-Host "  或访问 https://dotnet.microsoft.com/download" -ForegroundColor Red
exit 1