<#
.SYNOPSIS
    编译 ThermalTrace 安装包（Inno Setup）。

.DESCRIPTION
    流程：结束运行中的实例 → publish 单文件 exe → 调用 ISCC 编译 .iss。
    每一步失败都会以非零退出码退出，并指出失败在哪一步。

.PARAMETER Edition
    full（默认）＝极趣推送版安装包，源 exe 为 publish\，装到
                  %LOCALAPPDATA%\Programs\ThermalTrace，可选注册开机自启。
    standard     ＝普通版安装包，源 exe 为 publish-standard\（不含墨水屏推送），
                  独立 AppId 与安装目录（ThermalTrace-Standard），不覆盖极趣推送版；
                  安装器不注册计划任务、不额外请求管理员权限。

    【为什么普通版不结束运行中的实例】
    普通版只读 publish-standard\ 并写到 dist\，不碰 publish\ 与极趣推送版安装目录。
    极趣推送版正由计划任务以管理员权限跑着，结束它既没必要又要额外弹一次 UAC。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Edition standard
    powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [ValidateSet('full', 'standard')]
    [string]$Edition = 'full',

    # 复用已有的发布产物，跳过重新 publish（快速迭代安装向导时用）
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$iss = Join-Path $root 'installer\ThermalTrace.iss'
$project = Join-Path $root 'src\ThermalTrace\ThermalTrace.csproj'
$dist = Join-Path $root 'dist'
$publishDir = if ($Edition -eq 'standard') { 'publish-standard' } else { 'publish' }
$exe = Join-Path (Join-Path $root $publishDir) 'ThermalTrace.exe'

# 版本号单一来源：csproj 的 <Version>。安装包文件名与 Inno 的 AppVersion 都从这里取，
# 四段版本由 scripts\version.ps1 统一映射，避免脚本与项目各写一份而漂移。
. (Join-Path $PSScriptRoot 'version.ps1')
$versionInfo = Get-ThermalTraceVersion -ProjectPath $project
$version = $versionInfo.Version
$versionNum = $versionInfo.NumericVersion

$setupName = if ($Edition -eq 'standard') {
    "ThermalTrace-$version-Setup.exe"
} else {
    "ThermalTrace-$version-Zectrix-Setup.exe"
}
$setup = Join-Path $dist $setupName

function Find-InnoCompiler {
    <#
        优先用常见安装路径，找不到再看 PATH 里的 ISCC。
        【为什么不用 winget 自动装】安装系统级软件需要用户明确同意，
        不该由构建脚本擅自改变机器状态——只给出可复制的安装命令即可。
    #>
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

# ---------- 步骤 0：结束运行中的实例（仅极趣推送版） ----------
Write-Host "[0/3] 检查运行中的实例 ..." -ForegroundColor Cyan
if ($Edition -eq 'standard') {
    Write-Host '      跳过：普通版构建不触碰 publish\ 与极趣推送版安装目录' -ForegroundColor DarkGray
} else {
    $running = @(Get-Process -Name ThermalTrace -ErrorAction SilentlyContinue)
    if ($running) {
        Write-Host "      发现 $($running.Count) 个实例，正在结束 ..." -ForegroundColor Yellow
        & schtasks /end /tn ThermalTrace 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        $still = Get-Process -Name ThermalTrace -ErrorAction SilentlyContinue
        # -ErrorAction SilentlyContinue 是必须的：由计划任务以最高权限拉起的进程，
        # 普通权限 Stop-Process 会抛"拒绝访问"，在 $ErrorActionPreference='Stop' 下
        # 会把整个构建脚本中断掉。
        if ($still) { $still | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1 }
    } else {
        Write-Host '      无运行中实例' -ForegroundColor DarkGray
    }
}

# ---------- 步骤 1：publish ----------
if ($SkipPublish) {
    Write-Host "[1/3] 跳过 publish（使用已有的 $exe）" -ForegroundColor Cyan
} else {
    Write-Host "[1/3] 发布 $Edition 版单文件 exe ..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'publish.ps1') -Edition $Edition
    if ($LASTEXITCODE -ne 0) {
        Write-Host '[ERR] publish 失败' -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path $exe)) {
    Write-Host "[ERR] 未找到 $exe" -ForegroundColor Red
    exit 1
}

# ---------- 步骤 2：定位 Inno Setup ----------
Write-Host "[2/3] 定位 Inno Setup 编译器 ..." -ForegroundColor Cyan
$iscc = Find-InnoCompiler
if (-not $iscc) {
    Write-Host ''
    Write-Host '[ERR] 未找到 Inno Setup（ISCC.exe），无法生成安装包。' -ForegroundColor Red
    Write-Host ''
    Write-Host '     请先安装其一：' -ForegroundColor Yellow
    Write-Host '       winget install --id JRSoftware.InnoSetup -e' -ForegroundColor Yellow
    Write-Host '     或从 https://jrsoftware.org/isdl.php 下载安装。' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "     当前只有裸 exe 可供测试：$exe" -ForegroundColor DarkGray
    exit 2
}
Write-Host "      $iscc" -ForegroundColor DarkGray

# ---------- 步骤 3：编译安装包 ----------
Write-Host "[3/3] 编译安装包 ..." -ForegroundColor Cyan
$defines = @("/DAppVersion=$version", "/DAppVersionNum=$versionNum")
if ($Edition -eq 'standard') { $defines += '/DStandardEdition=1' }
& $iscc @defines $iss
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERR] Inno Setup 编译失败（退出码 $LASTEXITCODE）" -ForegroundColor Red
    exit $LASTEXITCODE
}

if (-not (Test-Path $setup)) {
    Write-Host "[ERR] 未生成 $setup" -ForegroundColor Red
    exit 1
}

$sizeMb = [Math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ''
Write-Host '[OK] 安装包已生成' -ForegroundColor Green
Write-Host "    $setup  ($sizeMb MB)"
Write-Host ''
if ($Edition -eq 'standard') {
    Write-Host '普通版实测清单（用临时 /DIR 装、装完即卸，不动真实安装）:' -ForegroundColor Yellow
    Write-Host '  1) 安装全程不弹 UAC'
    Write-Host '  2) 装到 %LOCALAPPDATA%\Programs\ThermalTrace-Standard，不覆盖极趣推送版'
    Write-Host '  3) 开始菜单出现「ThermalTrace 普通版」，卸载项名带「普通版」'
    Write-Host '  4) 首次启动弹一次 UAC（程序清单 requireAdministrator，预期行为）'
    Write-Host '  5) 卸载不弹 UAC；卸载后临时目录与卸载项都应消失'
} else {
    Write-Host '极趣推送版实测清单:' -ForegroundColor Yellow
    Write-Host '  1) 双击安装，确认安装过程不弹 UAC；勾选自启时才会弹一次'
    Write-Host '  2) 桌面/开始菜单出现「ThermalTrace 极趣推送版（Zectrix）」图标'
    Write-Host '  3) 启动时弹 UAC，选择''是''后托盘出现温度曲线图标'
    Write-Host '  4) 控制面板 -> 程序 -> 卸载，确认卸载项存在'
    Write-Host '  5) 卸载后运行 schtasks /query /tn ThermalTrace，应提示找不到任务'
}
