using System.Globalization;
using System.Text;
using ThermalTrace.Core;
using ThermalTrace.Model;

namespace ThermalTrace.Storage;

/// <summary>
/// 逐日聚合 CSV 的读写。
///
/// 【按时间片覆盖】每次写入当前时间片时，先移除该日文件里同一时间片的旧行，再写入本次的行；
/// 其它时间片逐行原样保留。这样同一 30 分钟片内重启不会出现第二套重复行（样本数被重复计入）。
/// 覆盖的代价：同片内旧实例已写入的样本被新实例替换，不合并求和。
///
/// 【断电安全】每 30 分钟才写一次；覆盖走"临时文件 + 原子替换"，不会留下半行。
/// 突然断电/被杀最多丢失当前未落盘的那一个时间片（30 分钟），
/// 不会像"每秒 flush 一次原始流"那样既伤 SSD 又丢大量数据。
///
/// 【格式】UTF-8 with BOM —— 保证 Excel 双击打开中文表头不乱码。
/// </summary>
internal static class DailyStore
{
    public static readonly string[] Fields =
    [
        "slice", "band", "ac", "n",
        "temp_mean", "temp_p95", "temp_max", "temp_min",
        "power_w", "temp_per_watt",
        "over85_pct", "over90_pct",
        "tjmax_min", "core_max_mean", "util_mean", "flag",
        // 第 17 列，2026-10-13 新增（GPU 温度均值）。表头只在新建文件时写，
        // 已存在的旧文件不会重写表头 → 旧行保持 16 列，解析时缺列一律填 NaN。
        "gpu_temp_mean",
        // 第 18–28 列，2026-10-16 新增（GPU 负载口径）：使用时长改为"负载>0 时长"，
        // 温度按负载分 5 档（0 / 1–25 / 26–50 / 51–75 / 76–100）落盘。
        // 同样只给新文件写全表头；旧文件追加上来的宽行按列位解析，不缺列即不缺数。
        "gpu_util_mean",
        "gpu_t_load0", "gpu_n_load0",
        "gpu_t_load25", "gpu_n_load25",
        "gpu_t_load50", "gpu_n_load50",
        "gpu_t_load75", "gpu_n_load75",
        "gpu_t_load100", "gpu_n_load100"
    ];

    private static readonly string Header = string.Join(',', Fields);

    /// <summary>
    /// 写入一个时间片的所有档位，按时间片键覆盖该片已有行。返回写入行数。
    ///
    /// 【为什么不能只追加】程序在同一个 30 分钟片内重启时，旧实例退出前已把该片写进 CSV，
    /// 新实例从零开始聚合同一片，再追加就会让该片出现两套行，样本数被重复计入。
    /// 因此写入前先按 slice 键删掉该片旧行；更早的时间片原样保留，不做任何回填或清理。
    /// </summary>
    public static int WriteSlice(DateOnly date, IEnumerable<SliceAggregate> aggregates, string? dailyDir = null)
    {
        var list = aggregates.Where(a => a.HasData).ToList();
        if (list.Count == 0) return 0;

        var dir = dailyDir ?? AppPaths.DailyDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{date:yyyy-MM-dd}.csv");

        var rows = list.Select(a => a.ToCsv()).ToList();
        var keys = list.Select(a => SliceKey(a.SliceStart)).ToHashSet(StringComparer.Ordinal);

        var kept = new List<string>();
        var hasHeader = false;
        var dropped = false;

        if (File.Exists(file))
        {
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                if (line.Length == 0) continue;

                if (!hasHeader && line.StartsWith("slice,", StringComparison.Ordinal))
                {
                    hasHeader = true;
                    kept.Add(line);
                    continue;
                }

                // 只比较首列（slice），旧行 / 宽窄列一律原样保留，不重新序列化
                var comma = line.IndexOf(',');
                var key = comma < 0 ? line : line[..comma];
                if (keys.Contains(key))
                {
                    dropped = true;
                    continue;
                }
                kept.Add(line);
            }
        }

        var sb = new StringBuilder();
        if (dropped)
        {
            if (!hasHeader) sb.AppendLine(Header);
            foreach (var line in kept) sb.AppendLine(line);
            foreach (var r in rows) sb.AppendLine(r);
            ReplaceAtomically(file, sb.ToString());
            InvalidateCache(file);
            return rows.Count;
        }

        if (!hasHeader) sb.AppendLine(Header);
        foreach (var r in rows) sb.AppendLine(r);
        File.AppendAllText(file, sb.ToString(), new UTF8Encoding(true));
        InvalidateCache(file);
        return rows.Count;
    }

    /// <summary>CSV 首列的时间片键，与 <see cref="SliceAggregate.ToCsv"/> 的格式一致。</summary>
    private static string SliceKey(DateTimeOffset t) =>
        t.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 写入后主动失效该文件的解析缓存。
    /// 【为什么不能只靠元数据判失效】<see cref="ReplaceAtomically"/> 用
    /// File.Replace(..., ignoreMetadataErrors: true)，目标文件的写入时间可能被保留；
    /// ReadFileCached 只比 (LastWriteTimeUtc, Length)，边界情况下会继续返回旧行。
    /// </summary>
    private static void InvalidateCache(string file)
    {
        lock (CacheGate) FileCache.Remove(file);
    }

    /// <summary>
    /// 临时文件 + 原子替换重写整日文件。
    /// 【为什么要重试】另一个进程（托盘 / GUI / 外部读 CSV 的工具）可能正打开该文件读取；
    /// 目标文件被占用的瞬间替换会失败，短暂重试通常即可成功。
    /// 重试仍失败则抛异常，由采样循环按失败处理——宁可本片本次不落盘，也不退回追加写造成重复行。
    /// </summary>
    private static void ReplaceAtomically(string file, string content)
    {
        var temp = file + ".rewrite-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content, new UTF8Encoding(true));
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Replace(temp, file, null, ignoreMetadataErrors: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ---------------- 解析缓存（内存优化） ----------------

    /// <summary>
    /// 文件级解析缓存：路径 -> (最后写入时间 UTC, 长度, 已解析行)。
    ///
    /// 【为什么必须有】托盘 tooltip 每 10 秒调一次 <see cref="ReadRange"/>（只要今天），
    /// 趋势页每次打开调 <see cref="ReadAll"/>。没有缓存时每次都是全量重新解析——
    /// 攒满一年数据后就是每 10 秒解析 365 个文件 / 3.6 万行，纯属白烧 CPU 与堆。
    ///
    /// 【失效判据】(写入时间, 长度) 任一变化即重读。落盘是追加写、长度必变，
    /// 所以不会读到旧数据；只按路径缓存才是会出事的写法。
    ///
    /// 【并发】缓存只在 <see cref="ReadFileCached"/> / <see cref="PruneCache"/> 里改，
    /// 一律走 <see cref="CacheGate"/>；缓存进表的 List 之后只读不写，可以安全跨线程共享。
    /// </summary>
    private static readonly Dictionary<string, (DateTime WriteUtc, long Length, List<SliceRow> Rows)>
        FileCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object CacheGate = new();

    /// <summary>读取全部逐日数据，按时间排序。文件缺失或损坏的行会被跳过。</summary>
    public static IReadOnlyList<SliceRow> ReadAll()
    {
        if (!Directory.Exists(AppPaths.DailyDir)) return Array.Empty<SliceRow>();

        var result = new List<SliceRow>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.DailyDir, "*.csv"))
            result.AddRange(ReadFileCached(file));
        PruneCache();
        result.Sort((a, b) => a.SliceStart.CompareTo(b.SliceStart));
        return result;
    }

    /// <summary>
    /// 只读 [from, to] 区间内的逐日数据，按时间排序。
    ///
    /// 【优化】按文件名日期过滤——区间外的文件<b>根本不打开</b>。
    /// 老实现是 <c>ReadAll()</c> 全解析完再在内存里过滤，而托盘每 10 秒调一次，
    /// 等于每 10 秒解析一遍全部历史。
    /// 行级过滤（<c>row.Date</c>）保留，行为与老实现完全一致。
    /// </summary>
    public static IReadOnlyList<SliceRow> ReadRange(DateOnly from, DateOnly to)
    {
        if (!Directory.Exists(AppPaths.DailyDir)) return Array.Empty<SliceRow>();

        var result = new List<SliceRow>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.DailyDir, "*.csv"))
        {
            // 文件名必须是日期（daily\YYYY-MM-DD.csv）才可能落在区间内，其余直接跳过
            if (FileDate(file) is not { } day || day < from || day > to) continue;

            foreach (var row in ReadFileCached(file))
                if (row.Date >= from && row.Date <= to)
                    result.Add(row);
        }
        result.Sort((a, b) => a.SliceStart.CompareTo(b.SliceStart));
        return result;
    }

    /// <summary>从文件名取日期；不是日期命名的文件（改名过 / 临时文件）返回 null。</summary>
    private static DateOnly? FileDate(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    /// <summary>
    /// daily\ 下按日期命名的文件日期，升序。
    /// 只列文件名、不解析内容，供"先定位候选日、再按日读取"的扫描使用
    /// （设置页槽 2 适用性扫描，见 <see cref="Analysis.TrendAnalyzer.ScanEinkSlot2Facts"/>）。
    /// </summary>
    public static IReadOnlyList<DateOnly> DailyDates()
    {
        if (!Directory.Exists(AppPaths.DailyDir)) return Array.Empty<DateOnly>();

        var dates = new List<DateOnly>();
        foreach (var file in Directory.EnumerateFiles(AppPaths.DailyDir, "*.csv"))
            if (FileDate(file) is { } day) dates.Add(day);
        dates.Sort();
        return dates;
    }

    /// <summary>带缓存地读一个文件。损坏 / 被占用时返回空列表（跳过该文件）。</summary>
    private static List<SliceRow> ReadFileCached(string file)
    {
        try
        {
            var info = new FileInfo(file);
            lock (CacheGate)
            {
                if (FileCache.TryGetValue(file, out var hit)
                    && hit.WriteUtc == info.LastWriteTimeUtc && hit.Length == info.Length)
                    return hit.Rows;
            }

            var rows = new List<SliceRow>();
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("slice,")) continue;
                if (SliceRow.Parse(line) is { } row) rows.Add(row);
            }

            lock (CacheGate)
                FileCache[file] = (info.LastWriteTimeUtc, info.Length, rows);
            return rows;
        }
        catch
        {
            // 单个文件损坏 / 被占用时跳过该文件，不影响整体分析（与老实现同一取舍）
            return new List<SliceRow>();
        }
    }

    /// <summary>清掉已被删除文件的缓存项，防止长期运行后缓存只增不减。</summary>
    private static void PruneCache()
    {
        lock (CacheGate)
        {
            if (FileCache.Count == 0) return;

            List<string>? dead = null;
            foreach (var key in FileCache.Keys)
                if (!File.Exists(key)) (dead ??= new()).Add(key);
            if (dead is null) return;
            foreach (var key in dead) FileCache.Remove(key);
        }
    }
}