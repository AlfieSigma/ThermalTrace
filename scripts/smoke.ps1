<#
.SYNOPSIS
    ThermalTrace 命令行冒烟测试：用退出码把各条命令路径钉死。

.DESCRIPTION
    为什么需要它：本项目没有任何单元测试，而历史上出现过多次"编译 0 警告 0 错误
    但实机行为是坏的"（控件无父容器不绘制、按钮不重绘）。退出码是最便宜的一道网。

    【绝对不能放进来的命令】
      --bind      → 交互式传感器绑定向导，会等待控制台输入
      --dump <分> → 前台采样直到指定分钟数才退出，耗时不可控
    无参数（后台常驻）只在受控条件下才会由这组检查启动：独立临时数据根、
    进程按 PID 精确结束，绝不用 /IM 或 Stop-Process -Name 杀全部实例。
    其余场景仍必须人工手动验证。

    【/ E 覆盖什么】
      D：--trend / --eink 的产物真的落盘且非空（退出码 0 不等于文件写出来了）。
      E：真启动一次后台常驻实例，模拟托盘双击打开主窗口，用 UI 自动化断言
         "主窗口可见、四个页签名称正确且可切换、控件与关键文本存在"，
         并断言启动时补写的当日 CSV 与 logs\sampler.txt 的本次启动记录。
         GUI 检查始终使用独立的临时数据根（%TEMP%\ThermalTrace-smoke-gui-<随机>），
         即使加 -UseRealData 也不读写真实目录；因为真实实例通常正在运行，
         第二个实例碰同一份快照会互相覆盖。
      E 的前置条件：测试机至少有一个可读温度传感器（非提权副本读 GPU 温度即可）。
      读不到任何温度时 Commands.Background 会直接退出 1，E1 会明确报出来。

    【为什么要单独构建免提权副本】
    app.manifest 是 requireAdministrator，正式 exe 每跑一次就弹一次 UAC。
    自动化里弹 UAC 会卡住，所以用 -p:NoElevation=true 另建一个诊断副本。
    注意该副本读不到 MSR，传感器相关输出必然为空——所以只记录不断言。

    【需要管理员】
    计划任务是以最高权限创建的，创建与删除都需要管理员。
    非提权运行时自动 SKIP，并提示如何补跑。

    【数据目录隔离与严格模式】
    默认把 THERMALTRACE_DATA_DIR 指到 %TEMP%\ThermalTrace-smoke-<随机>，
    /C 的 CLI 子进程都写在这个临时目录里，运行前后不读写真实的
    %LOCALAPPDATA%\ThermalTrace。临时目录在脚本结束时清理。
    只有显式加 -UseRealData 才使用真实目录。的 --install 会由计划任务
    拉起实例，该实例不继承脚本的环境变量，所以隔离模式下直接 SKIP。

    严格模式默认开启（发现任何 SKIP 即视为未通过）；加 -NonStrict 恢复旧语义（SKIP 不计失败）。

    退出码：
      0  通过（严格模式下要求 PASS 全覆盖；非严格模式允许 SKIP）
      1  存在 FAIL，或严格模式下存在 SKIP
      2  环境/构建错误（找不到 dotnet、免提权副本构建失败）

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1
    powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1 -Rebuild
    powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1 -Rebuild -NonStrict
    powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1 -OnlyAdmin -UseRealData
#>
[CmdletBinding()]
param(
    # 强制重建免提权诊断副本
    [switch]$Rebuild,

    # 只跑（自启生命周期），其余全部跳过
    [switch]$OnlyAdmin,

    # 使用真实数据目录 %LOCALAPPDATA%\ThermalTrace（默认使用隔离临时目录）
    [switch]$UseRealData,

    # 恢复旧语义：SKIP 不算失败（默认严格模式下 SKIP 即未通过）
    [switch]$NonStrict
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\ThermalTrace\ThermalTrace.csproj'
$smokeDir = Join-Path $root 'artifacts\smoke'
$smokeExe = Join-Path $smokeDir 'ThermalTrace.exe'
$realExe = Join-Path $root 'publish\ThermalTrace.exe'
$taskName = 'ThermalTrace'

# ---------- 数据目录隔离 ----------
# 默认把子进程的数据根指到临时目录；只有显式 -UseRealData 才碰真实目录。
# 子进程（Start-Process 与 CLI）继承当前进程的环境变量，所以只设置一次即可。
$realDataRoot = Join-Path $env:LOCALAPPDATA 'ThermalTrace'
$isolatedDataRoot = $null
if ($UseRealData) {
    Remove-Item Env:THERMALTRACE_DATA_DIR -ErrorAction SilentlyContinue
    $dataRoot = $realDataRoot
} else {
    $isolatedDataRoot = Join-Path ([IO.Path]::GetTempPath()) `
        ('ThermalTrace-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $isolatedDataRoot -Force | Out-Null
    $env:THERMALTRACE_DATA_DIR = $isolatedDataRoot
    $dataRoot = $isolatedDataRoot
}

# 严格模式默认开启：任何 SKIP 都算未通过，避免"全跳过却显示通过"。
$strict = -not [bool]$NonStrict

# GUI 实例与产物目录都用各自的临时数据根，不碰上面的 $dataRoot。
$script:guiDataRoot = $null
$script:artifactRoot = $null

function Clear-IsolatedData {
    foreach ($dir in @($script:isolatedDataRoot, $script:guiDataRoot, $script:artifactRoot)) {
        if ([string]::IsNullOrEmpty($dir)) { continue }
        try {
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        } catch {
            # 清理失败不影响退出码；临时目录留着也不会影响真实数据
        }
    }
}

$results = New-Object System.Collections.ArrayList

function Add-Result($id, $name, $status, $detail) {
    $null = $results.Add([pscustomobject]@{ Id = $id; Name = $name; Status = $status; Detail = $detail })
    $color = switch ($status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'SKIP' { 'DarkGray' }
        default { 'Yellow' }
    }
    Write-Host ('  [{0,-4}] {1,-4} {2}' -f $id, $status, $name) -ForegroundColor $color
    if ($detail) { Write-Host "         $detail" -ForegroundColor DarkGray }
}

function Invoke-Exe($exe, $argList) {
    <#
        运行一个必然立即返回的命令，返回真实退出码。

        【必须用 Start-Process，不能用 & $exe】
        ThermalTrace 是 WinExe（GUI 子系统）。PowerShell 用 & 调用 GUI 子系统的程序时
        **不会等待它结束**，$LASTEXITCODE 拿到的是上一条原生命令的残留值——
        表现为"所有命令的退出码都是 0"，测试全绿但什么都没验证到。
        这是本脚本第一个版本踩的坑（实测 --badflag 真值 2，却被读成 0）。

        程序的 ConsoleHost 会把输出附加到当前控制台，所以这里不重定向 stdout：
        输出直接打在终端里给��看，断言只用退出码。
    #>
    $p = Start-Process -FilePath $exe -ArgumentList $argList -Wait -PassThru
    return [pscustomobject]@{ Code = $p.ExitCode }
}

function Invoke-Native {
    <#
        运行原生命令并只返回退出码，**不因 stderr 输出抛异常**。

        【为什么必须屏蔽 stderr】
        schtasks 查不到任务、taskkill 杀不存在的进程，都会把错误写到 stderr。
        而 $ErrorActionPreference='Stop' 会把这类 stderr 输出升级成 terminating error
        直接中断整个脚本。可"任务不存在""进程没在跑"恰恰是本脚本要验证的正常结果——
        最后一项就是靠确认 schtasks 查询返回非 0 来判定任务已删除。
        第一次跑这组检查时就是这么崩的：前三项全 PASS，最后一项却因为脚本自己挂掉而没跑成。
    #>
    param([string]$FilePath, [string[]]$Arguments)

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @Arguments 2>&1 | Out-Null
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Get-TaskExists {
    return ((Invoke-Native 'schtasks.exe' @('/query', '/tn', $taskName)) -eq 0)
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ============ 准备免提权副本 ============
Write-Host ''
Write-Host '=== ThermalTrace 冒烟测试 ===' -ForegroundColor Cyan
if ($UseRealData) {
    Write-Host "  数据目录: $dataRoot（真实目录，本次会读写真实数据）" -ForegroundColor Yellow
} else {
    Write-Host "  数据目录: $dataRoot（隔离临时目录，结束即清理）" -ForegroundColor DarkGray
}
Write-Host ("  严格模式: {0}" -f $(if ($strict) { '开（SKIP 视为未通过）' } else { '关（SKIP 不计失败）' })) -ForegroundColor DarkGray

if (-not $OnlyAdmin) {
    if ($Rebuild -or (-not (Test-Path $smokeExe))) {
        Write-Host ''
        Write-Host '[准备] 构建免提权诊断副本 ...' -ForegroundColor Cyan

        $localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
        $dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }
        if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
            Write-Host '[ERR] 找不到 dotnet' -ForegroundColor Red
            Clear-IsolatedData
            exit 2
        }

        if (Test-Path $smokeDir) { Remove-Item $smokeDir -Recurse -Force }
        & $dotnet publish $project -c Release -r win-x64 -o $smokeDir `
            -p:NoElevation=true `
            -p:SelfContained=true -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true | Out-Null

        if ($LASTEXITCODE -ne 0) {
            Write-Host '[ERR] 免提权副本构建失败，/C 无法进行' -ForegroundColor Red
            Clear-IsolatedData
            exit 2
        }
        Write-Host '       完成' -ForegroundColor DarkGray
    } else {
        Write-Host ''
        Write-Host '[准备] 复用已有的免提权副本（加 -Rebuild 可强制重建）' -ForegroundColor DarkGray
    }

    # ============ ：可断言的免提权路径 ============
    Write-Host ''
    Write-Host '--- / 命令行路径（免提权副本）---' -ForegroundColor Cyan

    # A1 --status：纯查询，不该有任何副作用
    $r = Invoke-Exe $smokeExe @('--status')
    Add-Result 'A1' '--status 返回 0' `
        $(if ($r.Code -eq 0) { 'PASS' } else { 'FAIL' }) `
        $(if ($r.Code -eq 0) { '' } else { "退出码 $($r.Code)" })

    # A2 未知参数必须走参数错误路径，不能被当成正常命令执行
    $r = Invoke-Exe $smokeExe @('--badflag')
    Add-Result 'A2' '未知参数返回 2' `
        $(if ($r.Code -eq 2) { 'PASS' } else { 'FAIL' }) `
        $(if ($r.Code -eq 2) { '' } else { "退出码 $($r.Code)，期望 2" })

    # A3 缺取值也必须被拒：--dump 后面没跟分钟数
    $r = Invoke-Exe $smokeExe @('--dump')
    Add-Result 'A3' '参数缺取值返回 2' `
        $(if ($r.Code -eq 2) { 'PASS' } else { 'FAIL' }) `
        $(if ($r.Code -eq 2) { '' } else { "退出码 $($r.Code)，期望 2" })

    # A4 --uninstall 的幂等性：用户从没装过自启时也必须安静返回，不能崩。
    #    这里断言"没有未处理异常"（1 = 权限不足，属预期），不锁死具体码。
    $r = Invoke-Exe $smokeExe @('--uninstall')
    $okA4 = ($r.Code -eq 0 -or $r.Code -eq 1)
    Add-Result 'A4' '--uninstall 幂等（0 或 1）' `
        $(if ($okA4) { 'PASS' } else { 'FAIL' }) `
        $(if ($okA4) { "退出码 $($r.Code)" } else { "退出码 $($r.Code)，期望 0 或 1" })

    # ============ 真实路径：只记录不断言 ============
    Write-Host ''
    Write-Host '--- 真实路径（免提权读不到传感器，只验证不崩溃）---' -ForegroundColor Cyan

    $cases = @(
        @{ Id = '1'; Args = @('--self-check'); Name = '--self-check 不崩溃' },
        @{ Id = '2'; Args = @('--today'); Name = '--today 不崩溃' },
        @{ Id = '3'; Args = @('--trend', '--no-open'); Name = '--trend --no-open 不崩溃' }
    )
    foreach ($c in $cases) {
        $r = Invoke-Exe $smokeExe $c.Args
        # 允许的退出码：0 正常 / 1 自检未通过或未处理异常 / 740 权限不足。
        # 【为什么 1 也算通过】--self-check 在读不到传感器时本来就返回 1，
        # 而 Program.cs 对未处理异常也返回 1，两者用退出码无法区分。
        # 所以只能兜住"卡死 / 异常退出"这类硬故障，
        # 真正的传感器是否可用只能靠 B7 在干净机上人工看数值。
        $ok = ($r.Code -eq 0 -or $r.Code -eq 1 -or $r.Code -eq 740)
        Add-Result $c.Id $c.Name $(if ($ok) { 'PASS' } else { 'FAIL' }) "退出码 $($r.Code)"
    }

    # ============ ：CLI 产物 ============
    # 退出码 0 只说明"命令没崩"，不代表文件真的写出来了。
    # 注意两处命名/缓存碰撞：--trend 的报告名精确到分钟（trend-yyyyMMdd-HHmm.html），
    # 同一分钟内两项检查会撞同名文件；--eink 的 nodata-{N}.png 命中"天数没变就复用"，
    # 同目录重复跑不会重写。因此 D 用一份全新的空数据根，按"目录里出现非空产物"判定，
    # 与前序用例、文件名、缓存彻底解耦，也不往真实目录写报告或 PNG。
    Write-Host ''
    Write-Host '--- / CLI 产物 ---' -ForegroundColor Cyan

    $script:artifactRoot = Join-Path ([IO.Path]::GetTempPath()) `
        ('ThermalTrace-smoke-artifacts-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $artifactReports = Join-Path $script:artifactRoot 'reports'
    $artifactEink = Join-Path $script:artifactRoot 'eink'
    foreach ($sub in @($artifactReports, $artifactEink)) {
        New-Item -ItemType Directory -Path $sub -Force | Out-Null
    }

    $previousDataDir = $env:THERMALTRACE_DATA_DIR
    $env:THERMALTRACE_DATA_DIR = $script:artifactRoot
    try {
        $r = Invoke-Exe $smokeExe @('--trend', '--no-open')
        $reportArts = @(
            Get-ChildItem -LiteralPath $artifactReports -Filter '*.html' -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Length -gt 0 } |
                Sort-Object Name
        )
        $okD1 = ($r.Code -eq 0) -and ($reportArts.Count -ge 1)
        Add-Result '1' '--trend 生成非空 HTML 报告' `
            $(if ($okD1) { 'PASS' } else { 'FAIL' }) `
            $(if ($okD1) { "$($reportArts[0].Name) / $($reportArts[0].Length) 字节（共 $($reportArts.Count) 个）" }
              else { "退出码 $($r.Code)；空数据根下未产生非空 HTML" })

        $r = Invoke-Exe $smokeExe @('--eink', '--no-open', '--slot', '1')
        $einkArts = @(
            Get-ChildItem -LiteralPath $artifactEink -Filter '*.png' -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Length -gt 0 } |
                Sort-Object Name
        )
        $okD2 = ($r.Code -eq 0) -and ($einkArts.Count -ge 1)
        Add-Result '2' '--eink 生成非空 PNG' `
            $(if ($okD2) { 'PASS' } else { 'FAIL' }) `
            $(if ($okD2) { "$($einkArts[0].Name) / $($einkArts[0].Length) 字节（共 $($einkArts.Count) 个）" }
              else { "退出码 $($r.Code)；空数据根下未产生非空 PNG" })
    } finally {
        $env:THERMALTRACE_DATA_DIR = $previousDataDir
    }

    # ============ GUI 与启动落盘 ============
    # 说明见脚本头注释。核心难点：后台常驻启动时主窗口是隐藏的，只有托盘双击才 Show()。
    # 这里向本进程（按 PID 过滤）的 NotifyIcon 隐藏窗口投递一次回调消息，模拟托盘双击；
    # 之后全部用 UI 自动化读页签，不做像素比对。
    Write-Host ''
    Write-Host '--- GUI 与启动落盘 ---' -ForegroundColor Cyan

    $guiCases = [ordered]@{
        'E1' = '主窗口出现并可见'
        'E2' = '四个页签存在且名称正确'
        'E3' = '控件数量与关键文本存在'
        'E4' = '四个页签可切换'
        'E5' = '当日 CSV 增长'
        'E6' = 'sampler.txt 有本次启动记录'
    }

    function Add-MissingGuiCases([string]$reason) {
        foreach ($id in $script:guiCases.Keys) {
            if (@($results | Where-Object { $_.Id -eq $id }).Count -eq 0) {
                Add-Result $id $script:guiCases[$id] 'SKIP' $reason
            }
        }
    }

    $uiaReady = $true
    try {
        Add-Type @"
    using System;
    using System.Text;
    using System.Runtime.InteropServices;

    public static class SmokeWin32
    {
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
"@
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
    } catch {
        $uiaReady = $false
        $uiaError = $_.Exception.Message
    }

    if (-not $uiaReady) {
        foreach ($id in $guiCases.Keys) {
            Add-Result $id $guiCases[$id] 'SKIP' "当前 PowerShell 不支持 UI 自动化（请用 Windows PowerShell 5.1）: $uiaError"
        }
    } else {
        function Get-ProcessWindows([int]$ProcessId) {
            $found = New-Object System.Collections.ArrayList
            $callback = [SmokeWin32+EnumProc]{
                param($handle, $unused)
                $owner = 0
                [void][SmokeWin32]::GetWindowThreadProcessId($handle, [ref]$owner)
                if ($owner -ne $ProcessId) { return $true }
                $title = New-Object System.Text.StringBuilder 256
                [void][SmokeWin32]::GetWindowText($handle, $title, 256)
                $class = New-Object System.Text.StringBuilder 256
                [void][SmokeWin32]::GetClassName($handle, $class, 256)
                $null = $found.Add([pscustomobject]@{
                    Handle  = $handle
                    Title   = $title.ToString()
                    Class   = $class.ToString()
                    Visible = [SmokeWin32]::IsWindowVisible($handle)
                })
                return $true
            }
            [void][SmokeWin32]::EnumWindows($callback, [IntPtr]::Zero)
            return $found
        }

        function Get-ChildWindows([IntPtr]$Parent) {
            $found = New-Object System.Collections.ArrayList
            $callback = [SmokeWin32+EnumProc]{
                param($handle, $unused)
                $text = New-Object System.Text.StringBuilder 512
                [void][SmokeWin32]::GetWindowText($handle, $text, 512)
                $class = New-Object System.Text.StringBuilder 256
                [void][SmokeWin32]::GetClassName($handle, $class, 256)
                $null = $found.Add([pscustomobject]@{
                    Handle  = $handle
                    Class   = $class.ToString()
                    Text    = $text.ToString()
                    Visible = [SmokeWin32]::IsWindowVisible($handle)
                })
                return $true
            }
            [void][SmokeWin32]::EnumChildWindows($Parent, $callback, [IntPtr]::Zero)
            return $found
        }

        function Initialize-GuiDataRoot([string]$Root) {
            <#
                给 GUI 实例准备可预测的数据根：
                  1) 关掉首次运行提示与在线天气，避免弹窗/联网干扰自动化；
                  2) 造一条"上一时间片未落盘"的快照——启动时会补写进当日 CSV，
                     这样"CSV 增长"在几秒内就能断言，不必等 30 分钟片边界。
                返回补写行在 CSV 里的 slice 值（yyyy-MM-ddTHH:mm）。
            #>
            foreach ($sub in @('daily', 'logs', 'reports', 'eink', 'health', 'weather')) {
                New-Item -ItemType Directory -Path (Join-Path $Root $sub) -Force | Out-Null
            }

            $settings = [ordered]@{
                cleanDateIso = (Get-Date).ToString('yyyy-MM-dd')
                ui           = [ordered]@{ showSetupHint = $false; showTrayHint = $false }
                ambient      = [ordered]@{ useOnlineWeather = $false }
                sampler      = [ordered]@{ intervalSeconds = 5; sliceMinutes = 30 }
            }
            $settings | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $Root 'settings.json') -Encoding UTF8

            $start = (Get-Date).AddMinutes(-30)
            $start = $start.Date.AddHours($start.Hour).AddMinutes($start.Minute - ($start.Minute % 30))
            $iso = $start.ToString('o')
            $snapshot = [ordered]@{
                Version      = 1
                SliceMinutes = 30
                SliceStart   = $iso
                Aggregates   = @(
                    [ordered]@{
                        SliceStart  = $iso
                        Band        = 0
                        OnAcPower   = $true
                        Count       = 1
                        TempSum     = 50.0
                        PowerSum    = 0.0
                        CoreMaxSum  = 0.0
                        UtilSum     = 0.0
                        GpuTempSum  = 0.0
                        GpuCount    = 0
                        GpuUtilSum  = 0.0
                        GpuUtilN    = 0
                        TempMin     = 50.0
                        TempMax     = 50.0
                        Over85Count = 0
                        Over90Count = 0
                        Temps       = @(50.0)
                    }
                )
            }
            # SliceSnapshot 用的是默认序列化（PascalCase），与 settings.json 的 camelCase 不同。
            $snapshot | ConvertTo-Json -Depth 6 |
                Set-Content -LiteralPath (Join-Path $Root 'slice-snapshot.json') -Encoding UTF8

            return $iso.Substring(0, 16)
        }

        $script:guiDataRoot = Join-Path ([IO.Path]::GetTempPath()) `
            ('ThermalTrace-smoke-gui-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $script:guiDataRoot -Force | Out-Null
        $sliceKey = Initialize-GuiDataRoot -Root $script:guiDataRoot

        $guiCsvName = (Get-Date).ToString('yyyy-MM-dd') + '.csv'
        $guiCsv = Join-Path (Join-Path $script:guiDataRoot 'daily') $guiCsvName
        $guiCsvBefore = 0
        if (Test-Path -LiteralPath $guiCsv) { $guiCsvBefore = (Get-Item -LiteralPath $guiCsv).Length }

        $guiProc = $null
        try {
            $previousDataDir = $env:THERMALTRACE_DATA_DIR
            $env:THERMALTRACE_DATA_DIR = $script:guiDataRoot
            $guiProc = Start-Process -FilePath $smokeExe -PassThru
            $env:THERMALTRACE_DATA_DIR = $previousDataDir

            # 后台常驻启动：先给托盘与消息循环一点时间，再模拟一次托盘双击
            Start-Sleep -Seconds 3
            if ($guiProc.HasExited) {
                Add-Result 'E1' $guiCases['E1'] 'FAIL' "进程启动后退出，退出码 $($guiProc.ExitCode)"
                Add-Result 'E2' $guiCases['E2'] 'SKIP' '主窗口未出现'
                Add-Result 'E3' $guiCases['E3'] 'SKIP' '主窗口未出现'
                Add-Result 'E4' $guiCases['E4'] 'SKIP' '主窗口未出现'
            } else {
                $guiMain = $null
                $sends = 0
                $nextSend = Get-Date
                $deadline = (Get-Date).AddSeconds(15)
                while ((Get-Date) -lt $deadline) {
                    $guiMain = Get-ProcessWindows $guiProc.Id |
                        Where-Object { $_.Visible -and $_.Title -like 'ThermalTrace*' } |
                        Select-Object -First 1
                    if ($null -ne $guiMain) { break }
                    if ($guiProc.HasExited) { break }

                    # 最多补发两次：单文件副本首次解包较慢时，第一次可能还没有托盘窗口；
                    # 两次之间隔 2 秒，且每次发之前都先确认主窗口仍未出现，避免反复开关。
                    if ($sends -lt 2 -and (Get-Date) -ge $nextSend) {
                        $hidden = @(Get-ProcessWindows $guiProc.Id | Where-Object { -not $_.Visible })
                        $sink = @($hidden | Where-Object { $_.Class -like '*WindowsForms10.Window.0*' })
                        $targets = if ($sink.Count -gt 0) { $sink } else { $hidden }
                        foreach ($w in $targets) {
                            # WM_USER + 1024 / WM_LBUTTONDBLCLK：NotifyIcon 对托盘双击的处理入口
                            [void][SmokeWin32]::SendMessage($w.Handle, 0x0400 + 1024, [IntPtr]::Zero, [IntPtr]0x0203)
                        }
                        $sends++
                        $nextSend = (Get-Date).AddSeconds(2)
                    }
                    Start-Sleep -Milliseconds 300
                }

                if ($null -eq $guiMain) {
                    $why = if ($guiProc.HasExited) { "进程已退出，退出码 $($guiProc.ExitCode)" }
                           else { '15 秒内未出现可见主窗口（托盘双击消息可能未被处理）' }
                    Add-Result 'E1' $guiCases['E1'] 'FAIL' $why
                    Add-Result 'E2' $guiCases['E2'] 'SKIP' '主窗口未出现'
                    Add-Result 'E3' $guiCases['E3'] 'SKIP' '主窗口未出现'
                    Add-Result 'E4' $guiCases['E4'] 'SKIP' '主窗口未出现'
                } else {
                    Add-Result 'E1' $guiCases['E1'] 'PASS' "「$($guiMain.Title)」"

                    $expectedTabs = @('今日概览', '趋势分析', '解读', '设置')
                    $uiaRoot = [System.Windows.Automation.AutomationElement]::FromHandle($guiMain.Handle)
                    $uiaAll = $uiaRoot.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants,
                        [System.Windows.Automation.Condition]::TrueCondition)
                    $tabItems = @($uiaAll | Where-Object {
                        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem
                    })
                    $tabNames = @($tabItems | ForEach-Object { $_.Current.Name })
                    $okE2 = ($tabNames.Count -eq 4) -and (($tabNames -join '|') -eq ($expectedTabs -join '|'))
                    Add-Result 'E2' $guiCases['E2'] `
                        $(if ($okE2) { 'PASS' } else { 'FAIL' }) `
                        "实际页签：$($tabNames -join ' / ')"

                    # E3 必须在默认页（今日概览）上做：切页后概览页的控件会不可见。
                    $childTexts = @(Get-ChildWindows $guiMain.Handle |
                        Where-Object { $_.Visible -and $_.Text.Length -gt 0 } |
                        ForEach-Object { $_.Text })
                    $keyTexts = @('当前口径（单核峰值）', '今日最高', '超 85℃ 累计')
                    $missing = @()
                    foreach ($key in $keyTexts) {
                        if (@($childTexts | Where-Object { $_.Contains($key) }).Count -eq 0) {
                            $missing += $key
                        }
                    }
                    $okE3 = ($uiaAll.Count -ge 20) -and ($missing.Count -eq 0)
                    Add-Result 'E3' $guiCases['E3'] `
                        $(if ($okE3) { 'PASS' } else { 'FAIL' }) `
                        $(if ($okE3) { "UI 元素 $($uiaAll.Count) 个；关键文本齐全" }
                          else { "UI 元素 $($uiaAll.Count) 个；缺少文本：$($missing -join '、')" })

                    $switchError = $null
                    $switchedLog = New-Object System.Collections.ArrayList
                    if ($tabItems.Count -ne 4) {
                        $switchError = "页签数量为 $($tabItems.Count)，无法逐页切换"
                    }
                    for ($i = 1; $i -lt $tabItems.Count -and $null -eq $switchError; $i++) {
                        $tabName = $expectedTabs[$i]
                        $pattern = $null
                        if (-not $tabItems[$i].TryGetCurrentPattern(
                                [System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                            $switchError = "$tabName：页签不支持选择模式"
                            break
                        }
                        ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()

                        $switched = $false
                        $deadline = (Get-Date).AddSeconds(3)
                        while ((Get-Date) -lt $deadline) {
                            $selected = ([System.Windows.Automation.SelectionItemPattern]$pattern).Current.IsSelected
                            $pageShown = @(Get-ChildWindows $guiMain.Handle |
                                Where-Object { $_.Visible -and $_.Text -eq $tabName }).Count -gt 0
                            if ($selected -and $pageShown) { $switched = $true; break }
                            Start-Sleep -Milliseconds 200
                        }
                        if (-not $switched) {
                            $switchError = "$tabName：选择后 3 秒内页面未显示"
                        } else {
                            $null = $switchedLog.Add($tabName)
                        }
                    }
                    Add-Result 'E4' $guiCases['E4'] `
                        $(if ($null -eq $switchError) { 'PASS' } else { 'FAIL' }) `
                        $(if ($null -eq $switchError) { "已切到：$($switchedLog -join ' → ')" } else { $switchError })
                }
            }

            # E5/E6 只依赖"实例真的启动并写了盘"，不依赖窗口是否打开，所以放在窗口断言之外。
            $deadline = (Get-Date).AddSeconds(5)
            while (-not (Test-Path -LiteralPath $guiCsv) -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 200
            }
            $csvRows = @()
            if (Test-Path -LiteralPath $guiCsv) {
                $csvRows = @(Get-Content -LiteralPath $guiCsv -Encoding UTF8)
            }
            $guiCsvAfter = 0
            if (Test-Path -LiteralPath $guiCsv) { $guiCsvAfter = (Get-Item -LiteralPath $guiCsv).Length }
            $seededRow = @($csvRows | Where-Object { $_.StartsWith($sliceKey + ',') }).Count -gt 0
            $okE5 = ($guiCsvAfter -gt $guiCsvBefore) -and ($csvRows.Count -ge 2) -and $seededRow
            Add-Result 'E5' $guiCases['E5'] `
                $(if ($okE5) { 'PASS' } else { 'FAIL' }) `
                $(if ($okE5) { "$guiCsvName：$($csvRows.Count - 1) 行 / $guiCsvAfter 字节；含启动时补写的 $sliceKey" }
                  else { "$guiCsvName 未增长或缺补写行（slice=$sliceKey，$($csvRows.Count) 行 / $guiCsvAfter 字节）" })

            $samplerLog = Join-Path (Join-Path $script:guiDataRoot 'logs') 'sampler.txt'
            $startLine = $null
            if (Test-Path -LiteralPath $samplerLog) {
                $startLine = @(Get-Content -LiteralPath $samplerLog -Encoding UTF8 |
                    Where-Object { $_ -match '\] start:' -and $_.Contains($script:guiDataRoot) }) |
                    Select-Object -Last 1
            }
            $okE6 = ($null -ne $startLine)
            Add-Result 'E6' $guiCases['E6'] `
                $(if ($okE6) { 'PASS' } else { 'FAIL' }) `
                $(if ($okE6) { $startLine } else { 'sampler.txt 中未找到包含本次数据目录的 start 记录' })
        } catch {
            Add-Result 'E0' 'GUI 自动化执行' 'FAIL' $_.Exception.Message
            Add-MissingGuiCases 'GUI 自动化中断，未能执行'
        } finally {
            # 只按 PID 结束本次启动的实例：绝不能用进程名杀，否则会误杀用户正在运行的实例。
            if ($null -ne $guiProc) {
                if (-not $guiProc.HasExited) {
                    Stop-Process -Id $guiProc.Id -Force -ErrorAction SilentlyContinue
                }
                Start-Sleep -Milliseconds 300
            }
        }
    }
}

# ============ ：开机自启生命周期（需要管理员）============
Write-Host ''
Write-Host '--- / 开机自启生命周期 ---' -ForegroundColor Cyan

$exeB = if (Test-Path $realExe) { $realExe } else { $smokeExe }

# 的 --install 会由计划任务拉起一个实例；它的数据目录不受当前进程环境变量
# 控制（计划任务用自己的用户环境），所以隔离模式下不跑，避免写真实数据。
$adminSkipReason =
    if (-not (Test-Admin)) { '需要管理员权限' }
    elseif (-not $UseRealData) { '隔离模式不跑（--install 会拉起实例写真实数据）；需 -UseRealData' }
    else { $null }

if ($null -ne $adminSkipReason) {
    Write-Host '  跳过。' -ForegroundColor Yellow
    if ($adminSkipReason -eq '需要管理员权限') {
        Write-Host '  补跑方式（会弹 UAC，选"是"，并用真实目录）：' -ForegroundColor Yellow
        Write-Host '    powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1 -OnlyAdmin -UseRealData' -ForegroundColor Yellow
    } else {
        Write-Host '  补跑方式（管理员，并显式允许真实数据）：' -ForegroundColor Yellow
        Write-Host '    powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1 -UseRealData' -ForegroundColor Yellow
    }

    $skipCases = @(
        @{ Id = '1'; Name = '自启：--install 创建计划任务' },
        @{ Id = '2'; Name = '自启：任务确实存在' },
        @{ Id = '3'; Name = '自启：--uninstall 删除计划任务' },
        @{ Id = '4'; Name = '自启：任务确实已删除' }
    )
    foreach ($c in $skipCases) { Add-Result $c.Id $c.Name 'SKIP' $adminSkipReason }
} else {
    $r = Invoke-Exe $exeB @('--install')
    Add-Result '1' '--install 创建计划任务' `
        $(if ($r.Code -eq 0) { 'PASS' } else { 'FAIL' }) `
        $(if ($r.Code -eq 0) { '' } else { "退出码 $($r.Code)" })

    # 第二项不能只看"注册成功"这句话，必须查任务是否真的存在
    $exists = Get-TaskExists
    Add-Result '2' '任务确实存在' `
        $(if ($exists) { 'PASS' } else { 'FAIL' }) `
        $(if ($exists) { "schtasks /query /tn $taskName 返回 0" } else { 'schtasks 查询失败' })

    $r = Invoke-Exe $exeB @('--uninstall')
    Add-Result '3' '--uninstall 删除计划任务' `
        $(if ($r.Code -eq 0) { 'PASS' } else { 'FAIL' }) `
        $(if ($r.Code -eq 0) { '' } else { "退出码 $($r.Code)" })

    # 最后一项最容易漏掉的一半：只删不验等于没删。任务残留会指向已不存在的 exe。
    $still = Get-TaskExists
    Add-Result '4' '任务确实已删除' `
        $(if (-not $still) { 'PASS' } else { 'FAIL' }) `
        $(if (-not $still) { 'schtasks 查询已返回非 0' } else { '任务仍然存在' })

    # --install 会立刻跑一次程序验证任务可用，测完必须收尾，否则留个常驻托盘。
    # 进程若已自行退出，taskkill 返回非 0——这属于正常，不该中断脚本。
    $null = Invoke-Native 'taskkill.exe' @('/F', '/IM', 'ThermalTrace.exe')
}

# ============ 汇总 ============
Write-Host ''
Write-Host '=== 汇总 ===' -ForegroundColor Cyan

$fail = @($results | Where-Object { $_.Status -eq 'FAIL' })
$skip = @($results | Where-Object { $_.Status -eq 'SKIP' })
$pass = @($results | Where-Object { $_.Status -eq 'PASS' })

$strictSkip = $strict -and $skip.Count -gt 0
$failed = $fail.Count -gt 0 -or $strictSkip
$summaryColor = if ($failed) { 'Red' } else { 'Green' }
Write-Host ('  PASS {0}    FAIL {1}    SKIP {2}' -f $pass.Count, $fail.Count, $skip.Count) -ForegroundColor $summaryColor

if ($failed) {
    Write-Host ''
    if ($fail.Count -gt 0) {
        Write-Host '失败项:' -ForegroundColor Red
        foreach ($f in $fail) {
            Write-Host ('  {0}  {1}  {2}' -f $f.Id, $f.Name, $f.Detail) -ForegroundColor Red
        }
    }
    if ($strictSkip) {
        Write-Host "严格模式: 有 $($skip.Count) 项 SKIP，按未通过处理。" -ForegroundColor Red
        foreach ($s in $skip) {
            Write-Host ('  {0}  {1}  {2}' -f $s.Id, $s.Name, $s.Detail) -ForegroundColor Red
        }
    }
    Clear-IsolatedData
    exit 1
}

Write-Host ''
Write-Host '[OK] 冒烟测试通过' -ForegroundColor Green
if ($skip.Count -gt 0) {
    Write-Host "注意: 有 $($skip.Count) 项被跳过（非严格模式，未按失败处理）。" -ForegroundColor Yellow
}
Clear-IsolatedData
exit 0
