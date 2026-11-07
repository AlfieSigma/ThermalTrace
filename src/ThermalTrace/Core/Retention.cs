using System.Globalization;
using System.Text.RegularExpressions;

namespace ThermalTrace.Core;

/// <summary>
/// 本程序自身产物的保留策略（G15）：HTML 报告、墨水屏快照、推送日志。
///
/// 【只按文件名规则处理自己的产物】报告只认 <c>trend-yyyyMMdd-HHmm.html</c>，
/// 墨水屏只认 <c>p6-v9-yyyyMMdd.png</c> / <c>p6-slot2-yyyyMMdd.png</c>。
/// 用户手动放进同目录的其它文件（包括前缀相似但日期格式不对的）一律不碰。
///
/// 【清理时机】都在主产物写完之后同步执行，此时报告 / 图片已经落盘，
/// 清理只枚举目录并删旧文件；报告生成在 GUI 走后台线程，推送路径不触发图片清理。
/// 每次删除单独 <c>try</c>，任何失败（占用 / 权限 / 目录不可读）都静默忽略，
/// 不影响生成、打开与推送。
/// </summary>
internal static class Retention
{
    /// <summary>HTML 报告保留份数（按文件名时间戳由新到旧）。</summary>
    public const int ReportKeepCount = 30;

    /// <summary>墨水屏 PNG 保留天数（含今天，按文件名日期判断）。</summary>
    public const int EinkKeepDays = 90;

    private static readonly Regex ReportName =
        new(@"^trend-(\d{8})-(\d{4})\.html$", RegexOptions.CultureInvariant);

    private static readonly Regex EinkName =
        new(@"^p6-(v9|slot2)-(\d{8})\.png$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 清掉第 <see cref="ReportKeepCount"/> 份之后更旧的 HTML 报告，返回删除份数。
    /// 最新一份永远在保留集合里，不会被删。
    /// </summary>
    public static int PruneReports()
    {
        try
        {
            var dir = AppPaths.ReportsDir;
            if (!Directory.Exists(dir)) return 0;

            var candidates = new List<(string Path, string Key)>();
            foreach (var path in Directory.GetFiles(dir, "trend-*.html"))
            {
                var match = ReportName.Match(Path.GetFileName(path));
                if (!match.Success) continue;

                var key = match.Groups[1].Value + match.Groups[2].Value;
                if (!DateTime.TryParseExact(key, "yyyyMMddHHmm",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    continue;

                candidates.Add((path, key));
            }

            if (candidates.Count <= ReportKeepCount) return 0;

            candidates.Sort((a, b) => string.CompareOrdinal(b.Key, a.Key));
            var removed = 0;
            foreach (var older in candidates.Skip(ReportKeepCount))
            {
                try
                {
                    File.Delete(older.Path);
                    removed++;
                }
                catch { /* 单份删不掉就留到下次，不影响其它份 */ }
            }
            return removed;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 按颜色槽分别保留最近 <see cref="EinkKeepDays"/> 天的快照，返回删除份数。
    /// 两个槽是两套产品，互不挤占；每个槽的最新一份即使早于窗口也保留，
    /// 避免程序长时间没运行时把唯一可用的快照删空。
    /// </summary>
    public static int PruneEink(DateOnly today)
    {
        try
        {
            var dir = AppPaths.EinkDir;
            if (!Directory.Exists(dir)) return 0;

            var cutoff = today.AddDays(-(EinkKeepDays - 1));
            var families = new Dictionary<string, List<(string Path, DateOnly Day)>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var path in Directory.GetFiles(dir, "p6-*.png"))
            {
                var match = EinkName.Match(Path.GetFileName(path));
                if (!match.Success) continue;

                if (!DateTime.TryParseExact(match.Groups[2].Value, "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    continue;

                var family = match.Groups[1].Value;
                if (!families.TryGetValue(family, out var list))
                    families[family] = list = new List<(string, DateOnly)>();
                list.Add((path, DateOnly.FromDateTime(parsed)));
            }

            var removed = 0;
            foreach (var list in families.Values)
            {
                if (list.Count == 0) continue;
                list.Sort((a, b) => b.Day.CompareTo(a.Day));
                var newest = list[0].Path;

                foreach (var item in list)
                {
                    if (item.Day >= cutoff || item.Path == newest) continue;
                    try
                    {
                        File.Delete(item.Path);
                        removed++;
                    }
                    catch { /* 单份删不掉就留到下次 */ }
                }
            }
            return removed;
        }
        catch
        {
            return 0;
        }
    }
}
