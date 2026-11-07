namespace ThermalTrace.Core;

/// <summary>
/// 启动可观测记录：每次进程启动往 <c>logs\boot.txt</c> 追加一行。
///
/// 【为什么需要它】
/// 2026-10-10 实机重启时发现自启从未生效，但只能靠"CSV 时间戳 vs 开机事件"
/// 反推才敢下结论 —— 也就是说"程序没起来"这件事本身是不可观测的。
/// 有了这份记录，"本次开机后到底起来过没有、起来时是否提权成功"
/// 可以直接看文件，不用再猜。
///
/// 【数据量】
/// 每行约 150 字节，最多保留 400 行（约 60 KB），超出丢弃最旧的。
/// </summary>
internal static class BootLog
{
    private const string FileName = "boot.txt";
    private const int KeepLines = 400;

    /// <summary>最近一次开机时刻。用系统启动时长反推，无需读事件日志（6005）。</summary>
    public static DateTime LastBootTime =>
        DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static string FilePath => AppPaths.LogFile(FileName);

    /// <summary>进程启动即调用。任何失败都静默忽略 —— 日志不该影响主流程。</summary>
    public static void Record(string[]? args = null)
    {
        try
        {
            var boot = LastBootTime;
            var now = DateTime.Now;
            var uptimeSec = (int)Math.Max(0, (now - boot).TotalSeconds);

            var line = string.Join(" | ",
                $"{now:yyyy-MM-dd HH:mm:ss}",
                $"boot={boot:yyyy-MM-dd HH:mm:ss}",
                $"开机后{uptimeSec}s",
                "v" + VersionText,
                "admin=" + (Elevation.IsElevated ? "yes" : "no"),
                "pid=" + Environment.ProcessId,
                "args=" + (args is { Length: > 0 } ? string.Join(' ', args) : "(none)"),
                "exe=" + (Environment.ProcessPath ?? "?"));

            Append(line);
        }
        catch
        {
            // 写日志失败不影响采样
        }
    }

    /// <summary>
    /// 补一条偶发事件记录（如"本次启动自动绑定了传感器"）。
    ///
    /// 【为什么不并进 Record 的格式】Record 是每次启动固定的一行结构化事实，
    /// 而 --status 只显示最近 5 行；混进另一种格式会让那几行读起来莫名其妙。
    /// Note 有自己的时间戳前缀，一眼能区分。
    /// </summary>
    public static void Note(string message)
    {
        try
        {
            Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] note: {message}");
        }
        catch
        {
            // 写日志失败不影响主流程
        }
    }

    /// <summary>最近若干条启动记录（新的在后）。</summary>
    public static IReadOnlyList<string> Recent(int count)
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<string>();
            var lines = File.ReadAllLines(FilePath);
            return lines.Length <= count ? lines : lines[^count..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void Append(string line)
    {
        var path = FilePath;
        var existing = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        var lines = new List<string>(existing.Length + 1);
        lines.AddRange(existing);
        lines.Add(line);
        if (lines.Count > KeepLines)
            lines = lines.GetRange(lines.Count - KeepLines, KeepLines);
        File.WriteAllLines(path, lines);
    }

    private static string VersionText =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
}
