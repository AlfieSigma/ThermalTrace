using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ThermalTrace.Core;

/// <summary>自启的真实状态。<b>必须落到可观测的运行事实</b>，不能只看"配置写成功"。</summary>
internal enum AutoStartState
{
    /// <summary>没有注册任何自启任务。</summary>
    NotInstalled,

    /// <summary>任务存在，但被禁用。</summary>
    Disabled,

    /// <summary>任务已启用，但本次开机之后还没有运行过（含"从未运行过"）。</summary>
    NotRunSinceBoot,

    /// <summary>任务已启用，且本次开机之后确实运行过。</summary>
    Active
}

internal sealed record AutoStartStatus(
    AutoStartState State,
    DateTime? LastRun,
    int? LastResult,
    DateTime BootTime,
    string Command,
    string Raw)
{
    public bool TaskExists => State != AutoStartState.NotInstalled;

    /// <summary>
    /// 开关是否打开（任务已注册且启用）。
    /// 注意：这<b>不等于</b>本次开机真的跑起来了 —— 要看 <see cref="State"/>。
    /// 旧实现"只看注册表值非空"就是在这里把假成功放大成了长期未被发现的故障。
    /// </summary>
    public bool IsEnabled => State is AutoStartState.Active or AutoStartState.NotRunSinceBoot;

    public string Describe() => State switch
    {
        AutoStartState.NotInstalled => "未启用（未注册计划任务）",
        AutoStartState.Disabled => "已注册但被禁用",
        AutoStartState.NotRunSinceBoot when LastRun is null =>
            "已启用，但从未运行过（下次登录后自动启动）",
        AutoStartState.NotRunSinceBoot =>
            $"已启用，但本次开机（{BootTime:MM-dd HH:mm}）后还没跑起来 — 上次运行 {LastRun:MM-dd HH:mm}",
        _ => $"已启用（本次开机 {LastRun:HH:mm} 已随登录启动）"
    };

    /// <summary>
    /// 最近一次任务执行的退出码含义。
    ///
    /// 【踩坑】常驻后台任务永远不会返回 0：它一直跑着，任务计划程序记录的是
    /// 267009（0x41301 =「任务正在运行」）。这不是失败 —— 2026-10-10 实机安装后
    /// 第一次看到它就误报成"失败"了。0x413xx 这一段都是状态位而非错误码。
    /// </summary>
    public string LastResultText => LastResult switch
    {
        null => "未知",
        0 => "0（成功）",
        0x00041300 => "267008（等待下次运行，正常）",
        0x00041301 => "267009（正在运行中，正常）",
        0x00041302 => "267010（任务已禁用）",
        0x00041303 => "267011（尚未运行过）",
        0x00041304 => "267012（不会再运行）",
        0x00041305 => "267013（未排定）",
        0x00041306 => "267014（已终止）",
        0x00041307 => "267015（没有有效触发器）",
        _ => $"{LastResult}（失败，详见任务计划程序的历史记录）"
    };
}

internal sealed record AutoStartResult(bool Ok, string Message)
{
    public static AutoStartResult Success(string m) => new(true, m);
    public static AutoStartResult Fail(string m) => new(false, m);
}

/// <summary>
/// 开机自启 —— 计划任务实现。
///
/// 【为什么必须是计划任务，不能用 HKCU\...\Run】
/// app.manifest 声明了 requireAdministrator（读 Intel MSR 必须加载 WinRing0）。
/// Explorer 在登录阶段枚举 Run 键时用普通令牌 ShellExecute，遇到需要提权的 exe
/// <b>不弹 UAC、直接静默跳过</b>。结果就是：注册表写对了、IsEnabled 也显示"已启用"，
/// 但开机后程序从头到尾没被启动过（2026-10-10 实机重启实测确认，
/// 实机重启验证了这一点）。
/// 同机的 LiteMonitor 用计划任务（LogonTrigger + HighestAvailable + InteractiveToken）
/// 在同一次开机后 16 秒成功自启 —— 提权自启只有计划任务这条路。
///
/// 【为什么用 schtasks.exe 而不是 NuGet 包】
/// 不引入额外依赖；且用 /xml 建任务才能精确设置电池策略
/// （schtasks /create 的命令行开关设不了 DisallowStartIfOnBatteries）。
///
/// 【电池策略：与 LiteMonitor 相反】
/// 2026-10-10 决定：电池时也采集。因此显式设
/// DisallowStartIfOnBatteries=false + StopIfGoingOnBatteries=false。
/// CSV 已有 ac 列区分 AC/DC，DC 档数据有价值（分析时按 ac 分组看，不可混算）。
/// </summary>
internal static class AutoStart
{
    public const string TaskName = "ThermalTrace";

    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "ThermalTrace";

    // 单文件发布下 Assembly.Location 恒为空（IL3000），ExePath 直接取进程路径。
    public static string ExePath => Environment.ProcessPath ?? "";

    private static AutoStartStatus? _cache;
    private static DateTime _cacheAt;
    /// <summary>查询缓存周期。程序外改动计划任务后，界面最多滞后这么久；本进程内改任务（<see cref="Enable"/> /
    /// <see cref="Disable"/> / <see cref="StartNow"/>）都会清缓存，下一次 <see cref="Status"/> 立即重查。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    /// <summary>缓存读写锁：设置页改到后台线程查询后，查询与 GUI 切换可能并发读写同一份缓存。</summary>
    private static readonly object CacheGate = new();

    /// <summary>查询真实状态（60 秒内缓存，避免 GUI 刷新反复起 schtasks 进程）。</summary>
    public static AutoStartStatus Status()
    {
        lock (CacheGate)
        {
            if (_cache is not null && DateTime.Now - _cacheAt < CacheTtl) return _cache;
            _cache = Query();
            _cacheAt = DateTime.Now;
            return _cache;
        }
    }

    private static void InvalidateCache()
    {
        lock (CacheGate) _cache = null;
    }

    /// <summary>兼容旧调用点：只表示"开关是否打开"，不代表本次开机真的运行过。</summary>
    public static bool IsEnabled => Status().IsEnabled;

    // ---------- 操作 ----------

    public static AutoStartResult Enable()
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe)) return AutoStartResult.Fail("无法定位当前程序路径。");
        if (!File.Exists(exe)) return AutoStartResult.Fail($"程序路径不存在：{exe}");

        // 建 HIGHEST 任务必须提权；不提权时建出来的任务同样不会启动，
        // 必须明确报错，绝不能像旧实现那样"写成功了"就打印 [OK]。
        if (!Elevation.IsElevated)
            return AutoStartResult.Fail(
                "创建『最高权限』计划任务需要管理员权限。请右键『以管理员身份运行』ThermalTrace.exe 后重试。\n" +
                "  （普通权限下即使写成功，开机后也永远不会启动 —— 这正是旧 HKCU 方案失效的原因。）");

        var tmp = Path.Combine(Path.GetTempPath(), $"ThermalTrace-task-{Environment.ProcessId}.xml");
        try
        {
            // Task Scheduler 的 XML 以 UTF-16 读取最稳妥（schtasks /query /xml 也输出 UTF-16 声明）
            File.WriteAllText(tmp, BuildTaskXml(exe), new UnicodeEncoding(false, true));
            var r = Run($"/create /tn \"{TaskName}\" /xml \"{tmp}\" /f");
            if (r.Exit != 0)
                return AutoStartResult.Fail("schtasks 创建任务失败：" + FirstLine(r.Err, r.Out));
        }
        catch (Exception ex)
        {
            return AutoStartResult.Fail("创建任务时异常：" + ex.Message);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 临时文件清理失败无所谓 */ }
        }

        RemoveLegacyRunValue();
        InvalidateCache();

        var st = Status();
        if (!st.TaskExists)
            return AutoStartResult.Fail("任务创建返回成功，但随后查询不到 —— 请检查任务计划程序是否被策略禁止。");

        var msg = $"计划任务 \\{TaskName} 已创建：登录时触发 · 最高权限运行 · 电池供电时照常采集\n" +
                  $"  命令: {st.Command}";

        // 安装位置若还在工作区（目录名带日期），重建工作区就会静默失效，必须提示。
        if (!IsUnderInstallDir(exe))
            msg += $"\n[提示] 当前 exe 位于 {Path.GetDirectoryName(exe)}，建议安装到固定位置 "
                 + $"{AppPaths.InstallDir}（运行 scripts\\deploy.ps1），"
                 + "否则工作区目录变动后自启会静默失效。";

        return AutoStartResult.Success(msg);
    }

    public static bool Disable()
    {
        var r = Run($"/delete /tn \"{TaskName}\" /f");
        RemoveLegacyRunValue();
        InvalidateCache();
        return r.Exit == 0;
    }

    /// <summary>立刻触发一次（不必等下次登录），用于验证"任务真的能启动"。</summary>
    public static bool StartNow(out string message)
    {
        var r = Run($"/run /tn \"{TaskName}\"");
        InvalidateCache();
        message = r.Exit == 0 ? "已触发一次运行" : "触发失败：" + FirstLine(r.Err, r.Out);
        return r.Exit == 0;
    }

    /// <summary>exe 是否在正式安装目录下。</summary>
    public static bool IsUnderInstallDir(string exe) =>
        !string.IsNullOrEmpty(exe)
        && exe.StartsWith(AppPaths.InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>清理旧实现的 HKCU 启动项（与提权互斥，它永远不会启动）。</summary>
    public static void RemoveLegacyRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 清理失败不影响新机制
        }
    }

    /// <summary>旧 HKCU 启动项是否还在（排查残留用）。</summary>
    public static bool HasLegacyRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: false);
            return key?.GetValue(LegacyValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 查询 ----------

    private static AutoStartStatus Query()
    {
        var boot = BootLog.LastBootTime;
        var fields = QueryVerboseFields();
        if (fields is null)
            return new AutoStartStatus(AutoStartState.NotInstalled, null, null, boot, "", "任务不存在");

        // 是否启用只能从任务定义 XML 里读：CSV 里的"状态 / 计划任务状态"列是本地化文案，
        // 中文系统下会显示"就绪 / 已禁用"，无法可靠比较。
        var def = QueryDefinition();

        var lastRun = ParseTimestamp(Field(fields, 5));
        var lastResult = ParseLastResult(Field(fields, 6));
        var command = def.Command.Length > 0 ? def.Command : Field(fields, 8).Trim('"');

        var state = !def.Enabled ? AutoStartState.Disabled
            : lastRun is null ? AutoStartState.NotRunSinceBoot
            : lastRun.Value >= boot ? AutoStartState.Active
            : AutoStartState.NotRunSinceBoot;

        var raw = $"state={state} lastRun={Field(fields, 5)} lastResult={Field(fields, 6)}";
        return new AutoStartStatus(state, lastRun, lastResult, boot, command, raw);
    }

    /// <summary>
    /// <c>schtasks /query /tn X /fo CSV /v</c> 的字段。
    /// 用 CSV 而不是 LIST：LIST 的键名是本地化的（"上次运行时间:"），
    /// 而 CSV 的<b>列顺序</b>与系统语言无关（实测列名会本地化，顺序不变）。
    /// </summary>
    private static List<string>? QueryVerboseFields()
    {
        var r = Run($"/query /tn \"{TaskName}\" /fo CSV /v");
        if (r.Exit != 0) return null;

        var lines = r.Out.Split('\n')
            .Select(s => s.Trim('\r'))
            .Where(s => s.Trim().Length > 0)
            .ToList();
        if (lines.Count < 2) return null;

        return SplitCsv(lines[^1]);
    }

    /// <summary>
    /// 从任务定义 XML 里读"是否启用"与"命令路径"。
    ///
    /// 【踩坑】Task Scheduler 导出 XML 时会<b>省略取默认值的元素</b>：
    /// 启用状态为 true 时根本不输出 &lt;Enabled&gt;，只有 false 才写出来
    /// （2026-10-10 实测确认）。因此"找不到 &lt;Enabled&gt;"必须理解为"已启用"，
    /// 否则每个正常启用的任务都会被误判成"已被禁用"。
    /// </summary>
    private static (bool Enabled, string Command) QueryDefinition()
    {
        var r = Run($"/query /tn \"{TaskName}\" /xml");
        if (r.Exit != 0) return (false, "");

        // 只看 Settings 段：Triggers 里也有 <Enabled>，且导出顺序不保证
        var settings = Regex.Match(r.Out, "<Settings>(.*?)</Settings>", RegexOptions.Singleline).Groups[1].Value;
        var enabled = true;
        if (settings.Length > 0)
        {
            var m = Regex.Match(settings, @"<Enabled>\s*(true|false)\s*</Enabled>", RegexOptions.IgnoreCase);
            enabled = !m.Success
                      || string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        var cmd = Regex.Match(r.Out, @"<Command>(.*?)</Command>", RegexOptions.Singleline).Groups[1].Value.Trim();
        return (enabled, cmd.Trim('"'));
    }

    private static string Field(List<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index].Trim() : "";

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cur.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        result.Add(cur.ToString());
        return result;
    }

    private static DateTime? ParseTimestamp(string s)
    {
        if (s.Length == 0) return null;

        // schtasks 按区域设置格式化时间（实测 "2026/10/10 1:19:53"），全为 ASCII，
        // 即使控制台编码判定有误也不影响数字解析。
        string[] formats =
        [
            "yyyy/M/d H:mm:ss", "yyyy/M/d HH:mm:ss", "yyyy-M-d H:mm:ss", "yyyy-M-d HH:mm:ss",
            "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm:ss", "M/d/yyyy HH:mm:ss",
            "d.M.yyyy H:mm:ss", "yyyy/M/d", "M/d/yyyy"
        ];
        foreach (var f in formats)
            if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;

        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var c)) return c;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var i)) return i;
        return null; // "N/A" / "不适用" / "尚未运行"
    }

    private static int? ParseLastResult(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
            return h;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        return null;
    }

    // ---------- 任务定义 ----------

    private static string BuildTaskXml(string exePath)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var dir = Path.GetDirectoryName(exePath) ?? "";
        // LogonTrigger + InteractiveToken：托盘需要交互式会话
        // RunLevel=HighestAvailable：读 MSR 必须提权（HKCU Run 做不到这一点）
        // 电池策略：2026-10-10 决定，电池供电时照常采集（CSV 有 ac 列可区分）
        return $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Author>ThermalTrace</Author>
                    <Description>开机自启：后台记录散热长期趋势（需提权读 MSR；电池供电时照常采集）</Description>
                    <URI>\{TaskName}</URI>
                  </RegistrationInfo>
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                      <UserId>{user}</UserId>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{user}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>HighestAvailable</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>false</AllowHardTerminate>
                    <StartWhenAvailable>true</StartWhenAvailable>
                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                    <IdleSettings>
                      <StopOnIdleEnd>false</StopOnIdleEnd>
                      <RestartOnIdle>false</RestartOnIdle>
                    </IdleSettings>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>false</Hidden>
                    <RunOnlyIfIdle>false</RunOnlyIfIdle>
                    <WakeToRun>false</WakeToRun>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>7</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>"{exePath}"</Command>
                      <WorkingDirectory>{dir}</WorkingDirectory>
                    </Exec>
                  </Actions>
                </Task>
                """;
    }

    // ---------- 进程调用 ----------

    /// <summary>
    /// 单次 schtasks 调用的等待上限。
    ///
    /// 【为什么必须有】Task Scheduler 服务被策略 / 安全软件挡住时，schtasks.exe 可能
    /// 卡在启动或查询阶段而不返回；旧实现直接 <c>WaitForExit()</c> 无超时，
    /// 设置页首次显示会被这条链一直钉住。超时按"查不到"处理，由 <see cref="Status"/>
    /// 照常缓存 60 秒，之后重试，绝不让调用方无限等待。
    /// </summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(3);

    private static (int Exit, string Out, string Err) Run(string args)
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            p = Process.Start(psi);
            if (p is null) return (-1, "", "无法启动 schtasks.exe");

            // 读原始字节自己解码：schtasks 在部分区域设置下输出带 BOM 的 UTF-16，
            // 直接交给 StreamReader 用默认编码会解出乱码。
            //
            // 【为什么把读取放到线程池】同步 <c>CopyTo</c> 等的是管道而不是进程：
            // 进程卡住不退出时，读取会跟着一起无限阻塞，WaitForExit 的时限就失去意义。
            var outTask = Task.Run(() => ReadAll(p.StandardOutput.BaseStream));
            var errTask = Task.Run(() => ReadAll(p.StandardError.BaseStream));

            if (!p.WaitForExit((int)RunTimeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出或无权结束都不影响结论 */ }
                // 进程被杀后管道会关闭，读任务收尾；给一点余量避免异常路径继续占线程
                _ = Task.WaitAll([outTask, errTask], TimeSpan.FromSeconds(1));
                return (-1, "", $"schtasks {RunTimeout.TotalSeconds:0} 秒内未返回，按查不到处理");
            }

            if (!Task.WaitAll([outTask, errTask], TimeSpan.FromSeconds(1)))
                return (-1, "", "schtasks 已退出，但输出流未在 1 秒内读完");

            return (p.ExitCode, Decode(outTask.Result), Decode(errTask.Result));
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
        finally
        {
            p?.Dispose();
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        // 无 BOM 但呈 "A\0B\0" 形态 —— 也是 UTF-16LE
        if (bytes.Length > 4 && bytes[1] == 0 && bytes[3] == 0)
            return Encoding.Unicode.GetString(bytes);
        return new UTF8Encoding(false).GetString(bytes);
    }

    private static string FirstLine(params string[] texts)
    {
        foreach (var t in texts)
            foreach (var line in t.Split('\n'))
            {
                var s = line.Trim('\r').Trim();
                if (s.Length > 0) return s;
            }
        return "(无输出)";
    }
}
