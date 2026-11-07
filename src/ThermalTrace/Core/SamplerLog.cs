using System.Text;

namespace ThermalTrace.Core;

/// <summary>
/// 采样运行期日志：只记启动 / 跨片 / 异常 / 停摆，不逐拍写。
///
/// 【为什么需要它】2026-11-06 实机：采样线程在 10:00:02 之后静默停止，
/// CSV 冻结 45 分钟，而 <see cref="ConsoleHost"/> 的输出在托盘模式下不落盘，
/// 事后完全无法区分"卡住"、"抛异常退出"还是"正常 Cancel"。
/// 这份日志配合 SamplerService 的心跳，让"采样还活着吗"变成可观测事实。
///
/// 【写入方式】追加写 + 按体积轮转，不再"读全文 → 截断 400 行 → 重写"。
/// 旧写法每次落日志都要读整份文件再整体覆盖，高频时日志自己会变成采样循环的阻塞点，
/// 400 行截断还会把 start / power / stall 这些关键证据挤掉。
/// 写失败一律静默，日志不能反过来影响采样。
/// </summary>
internal static class SamplerLog
{
    private const string FileName = "sampler.txt";
    private const long RotateBytes = 64 * 1024;
    private const int MaxArchives = 3;

    private static readonly object Gate = new();

    public static string FilePath => AppPaths.LogFile(FileName);

    public static void Write(string kind, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {message}"
            .Replace('\r', ' ').Replace('\n', ' ');
        lock (Gate)
        {
            try { Append(line); }
            catch
            {
                // 写日志失败不影响采样
            }
        }
    }

    /// <summary>
    /// 最近一条供电切换记录（原始日志行），供 GUI 空态回答"这段时间为什么没有电池数据"。
    /// 只读，不写；轮转后当前文件里可能没有，按由新到旧的顺序再查归档。
    /// </summary>
    public static string? LastPowerChange()
    {
        foreach (var path in ReadOrder())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var lines = File.ReadAllLines(path);
                for (var i = lines.Length - 1; i >= 0; i--)
                    if (lines[i].Contains("] power:", StringComparison.Ordinal)
                        && lines[i].Contains("供电切换：", StringComparison.Ordinal))
                        return lines[i];
            }
            catch
            {
                // 日志不可读不该拖垮界面
            }
        }
        return null;
    }

    private static void Append(string line)
    {
        var path = FilePath;
        var dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);

        if (File.Exists(path) && new FileInfo(path).Length >= RotateBytes)
            Rotate(path, dir!);

        File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void Rotate(string path, string dir)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var archive = Path.Combine(dir, $"sampler.{stamp}.txt");
        for (var i = 1; File.Exists(archive); i++)
            archive = Path.Combine(dir, $"sampler.{stamp}-{i}.txt");
        File.Move(path, archive);

        var archives = Directory.GetFiles(dir, "sampler.*.txt")
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .ToArray();
        foreach (var old in archives.Skip(MaxArchives))
        {
            try { File.Delete(old); }
            catch { /* 归档清理失败不影响后续写入 */ }
        }
    }

    private static string[] ReadOrder()
    {
        var paths = new List<string> { FilePath };
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                paths.AddRange(Directory.GetFiles(dir, "sampler.*.txt")
                    .OrderByDescending(p => p, StringComparer.Ordinal));
        }
        catch
        {
            // 归档目录不可读不影响主日志
        }
        return paths.ToArray();
    }
}
