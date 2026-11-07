<#
.SYNOPSIS
    一条命令从干净状态产出可分发的普通版 beta 安装包。

.DESCRIPTION
    只出普通版；极趣推送版仅在本地部署，不对外打包。
    流程：前置检查 → 还原/编译 → publish 普通版单文件 → ISCC 编译普通版安装器 → 自检产物。
    每一步失败都以非零退出码退出，并明确指出失败在哪一步，便于接进 CI 或别人机器。

    【为什么不直接调 build.ps1 + publish.ps1 + build-installer.ps1】
    三步必须按顺序做，参数还要保持一致；收口到一处后，"打不出安装包"这类问题就只剩一个需要排查的地方。
    普通版构建只读写 publish-standard\，不碰 publish\ 与极趣推送版安装目录，因此不需要结束运行中的实例。

    【为什么要 -SkipInstaller】
    改 C# 代码时不需要每次都跑 Inno Setup（要十几秒）。加上这个开关后，
    日常迭代走 -SkipInstaller，只在真要出包时才走完整流程。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\build-beta.ps1
    powershell -ExecutionPolicy Bypass -File scripts\build-beta.ps1 -SkipInstaller
    powershell -ExecutionPolicy Bypass -File scripts\build-beta.ps1 -SkipSmoke
#>
[CmdletBinding()]
param(
    # 只出裸 exe，不跑 Inno Setup（日常快速迭代用）
    [switch]$SkipInstaller,

    # 跳过产物自检
    [switch]$SkipSmoke,

    # 打包后跑冒烟测试（scripts\smoke.ps1）。
    # 默认不跑：它要额外 publish 一个 65 MB 的免提权副本，耗时约 20 秒，
    # 而 CI/日常迭代关心的是"能不能出包"而不是"命令行路径有没有坏"。
    # 需要管理员，非提权时会自动 SKIP。
    [switch]$WithSmoke
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\ThermalTrace\ThermalTrace.csproj'
$iss = Join-Path $root 'installer\ThermalTrace.iss'
$publishExe = Join-Path $root 'publish-standard\ThermalTrace.exe'
$dist = Join-Path $root 'dist'

# 版本号单一来源：csproj 的 <Version>，由 scripts\version.ps1 统一解析，
# 避免脚本与项目各写一份而漂移。
# 安装包文件名跟着版本号走：此前这里写死 beta.1，出 beta.2 时会照旧校验旧文件名、
# 明明打出了新包却报"缺少产物"。
. (Join-Path $PSScriptRoot 'version.ps1')
$versionInfo = Get-ThermalTraceVersion -ProjectPath $project
$version = $versionInfo.Version
$setupExe = Join-Path $dist "ThermalTrace-$version-Setup.exe"
Write-Host "[*] ThermalTrace $version" -ForegroundColor Cyan
Write-Host ''

function Step($n, $text) {
    Write-Host "[$n/3] $text" -ForegroundColor Cyan
}

function Find-InnoCompiler {
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

# ---------- 1. 前置检查 ----------
Step 1 '检查构建工具 ...'

$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '[ERR] 找不到 dotnet。请先运行 scripts\install-dotnet.ps1' -ForegroundColor Red
    exit 1
}
Write-Host "      dotnet: $dotnet" -ForegroundColor DarkGray

$iscc = $null
if (-not $SkipInstaller) {
    $iscc = Find-InnoCompiler
    if (-not $iscc) {
        Write-Host '[ERR] 未找到 Inno Setup（ISCC.exe）。安装命令：' -ForegroundColor Red
        Write-Host '      winget install --id JRSoftware.InnoSetup -e' -ForegroundColor Red
        exit 2
    }
    Write-Host "      iscc:   $iscc" -ForegroundColor DarkGray
}

# ---------- 2. 还原并编译 ----------
Step 2 '还原并编译 (Release) ...'
& $dotnet restore $project
if ($LASTEXITCODE -ne 0) { Write-Host '[ERR] restore 失败' -ForegroundColor Red; exit 3 }

& $dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { Write-Host '[ERR] 编译失败' -ForegroundColor Red; exit 3 }
Write-Host ''

# ---------- 3. publish 并打包 ----------
if ($SkipInstaller) {
    Write-Host '[3/3] 已跳过打包（-SkipInstaller）' -ForegroundColor Cyan
} else {
    Step 3 'publish 普通版单文件并编译安装器 ...'
    & (Join-Path $PSScriptRoot 'publish.ps1') -Edition standard
    if ($LASTEXITCODE -ne 0) { Write-Host '[ERR] publish 失败' -ForegroundColor Red; exit 4 }

    if (-not (Test-Path $publishExe)) {
        Write-Host "[ERR] 未生成 $publishExe" -ForegroundColor Red
        exit 4
    }

    # 版本号也由脚本传入，.iss 里不再保留第二份默认值
    $defines = @('/DStandardEdition=1',
                 "/DAppVersion=$version",
                 "/DAppVersionNum=$($versionInfo.NumericVersion)")
    & $iscc @defines $iss | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERR] Inno Setup 编译失败（退出码 $LASTEXITCODE）" -ForegroundColor Red
        exit 4
    }
    Write-Host ''
}

# ---------- 产物自检 ----------
if (-not $SkipSmoke) {
    Write-Host '[自检] 校验产物 ...' -ForegroundColor Cyan

    if (-not (Test-Path $publishExe)) {
        Write-Host '  [FAIL] 缺少 publish-standard\ThermalTrace.exe' -ForegroundColor Red
        exit 5
    }
    Write-Host ('  [ OK ] publish-standard\ThermalTrace.exe  ({0} MB)' -f [Math]::Round((Get-Item $publishExe).Length / 1MB, 1))

    # 图标必须真的嵌进了 exe。ApplicationIcon 配错路径时 MSBuild 会静默忽略，
    # 只看"编译成功"是发现不了的——托盘会悄悄退回系统默认图标。
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($publishExe)
    if ($null -eq $icon) {
        Write-Host '  [FAIL] exe 里提取不到图标，托盘会退回系统默认图标' -ForegroundColor Red
        exit 5
    }
    Write-Host "  [ OK ] exe 图标可提取 ($($icon.Width)x$($icon.Height))"

    if (-not $SkipInstaller) {
        if (-not (Test-Path $setupExe)) {
            Write-Host "  [FAIL] 缺少 $setupExe" -ForegroundColor Red
            exit 5
        }
        Write-Host ('  [ OK ] {0}  ({1} MB)' -f (Split-Path $setupExe -Leaf), [Math]::Round((Get-Item $setupExe).Length / 1MB, 1))

        $setupIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($setupExe)
        if ($null -eq $setupIcon) {
            Write-Host '  [WARN] 安装包提取不到图标（不影响安装）' -ForegroundColor Yellow
        }
    }
    Write-Host ''
}

# ---------- 冒烟测试（可选）----------
if ($WithSmoke) {
    Write-Host ''
    Write-Host '=== 冒烟测试 ===' -ForegroundColor Cyan
    # -Rebuild 强制重建免提权副本：刚 publish 过一次，不重建会用到上次的旧产物，
    # 那样测的就不是这次的代码了。
    & (Join-Path $PSScriptRoot 'smoke.ps1') -Rebuild
    if ($LASTEXITCODE -ne 0) {
        Write-Host '' -ForegroundColor Red
        Write-Host '[ERR] 冒烟测试未通过，安装包可能有问题，先别发' -ForegroundColor Red
        exit 6
    }
}

# ---------- 收尾 ----------
Write-Host '[OK] beta 构建完成' -ForegroundColor Green
if (Test-Path $publishExe) {
    Write-Host ('    裸 exe:  {0}  ({1} MB)' -f $publishExe, [Math]::Round((Get-Item $publishExe).Length / 1MB, 1)) -ForegroundColor DarkGray
}
if ((Test-Path $setupExe) -and (-not $SkipInstaller)) {
    Write-Host ('    安装包:  {0}  ({1} MB)' -f $setupExe, [Math]::Round((Get-Item $setupExe).Length / 1MB, 1)) -ForegroundColor DarkGray
}
Write-Host ''
# 【为什么这句从"需先卸载"改成"可直接覆盖"】
# 旧注释说的"按 AppId 判重"被误读成了"装重了要先卸"。实测：卸载项落在
# HKCU\...\Uninstall\{7B3D6C58-...}_is1（键名不含版本号），同 AppId 的新包会
# 就地改写这一个键 —— 控制面板始终只有一个条目，不需要先卸载，历史数据
# （%LOCALAPPDATA%\ThermalTrace\）在安装目录之外，也不受影响。
# 唯一前提是程序不能在跑：CloseApplications=yes 会自动提示关闭，不用手工 kill。
Write-Host '提示: 新版可直接覆盖安装旧版，无需先卸载（卸载项按 AppId 就地更新，历史数据不受影响）。' -ForegroundColor Yellow
