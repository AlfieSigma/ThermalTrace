using ThermalTrace.Model;
using ThermalTrace.Storage;

namespace ThermalTrace.Analysis;

/// <summary>
/// 单日 GPU 聚合（展示侧 G1 口径；使用时长口径 2026-10-16 修正，见 <see cref="HoursUsed"/>）。
/// </summary>
public sealed record GpuDayStat(
    DateOnly Date,
    double TempMean,
    double HoursUsed,
    double UtilMean,
    double IdleTempMean,
    double ActiveTempMean,
    int SlicesWithGpu,
    long Samples,
    long ActiveSamples,
    double[] BucketTemps,
    long[] BucketCounts)
{
    /// 当天有没有真正采到 GPU 温度。没有时 <see cref="TempMean"/> 为 NaN，展示层渲染「—」（R5）。</summary>
    public bool HasData => !double.IsNaN(TempMean);

    /// <summary>
    /// 当天有没有采到 GPU <b>负载</b>数据（2026-10-16 起的新口径）。
    /// 没有 = 跑的是旧构建 / 负载传感器读不到。展示侧据此明说"口径不可比"，
    /// 而不是把"没采到负载"当成"使用 0 小时"。
    /// </summary>
    public bool HasLoadData => ActiveSamples > 0 || BucketCounts[0] > 0;
}

/// <summary>
/// SSD 寿命计数器在一个时间窗口内的统计（G3/G5/G7）。
/// 派生量全部在这里按 G7 的口径只算一次，展示层直接取用，不自己算。
/// </summary>
public sealed record SsdWindowStat(
    DateOnly From,
    DateOnly To,
    int RowCount,
    DriveHealth? Latest,
    double DataWrittenPerDay,
    double PercentageUsedPerDay,
    double ProjectedLifeDays)
{
    /// <summary>G5：日粒度累计量，套用 ΔT 的「3 天」门槛会产出假趋势。</summary>
    public const int MinRowsForTrend = 14;

    public static SsdWindowStat Empty { get; } =
        new(default, default, 0, null, double.NaN, double.NaN, double.NaN);

    /// <summary>G5：够天数才给速率与预估；不足则只画线 + 当前值。</summary>
    public bool HasTrend => RowCount >= MinRowsForTrend;

    /// <summary>
    /// 寿命百分比连日不动。计数器粒度粗（整数 %），一天不动是正常的——
    /// 展示层据此明说，免得用户以为记录坏了。
    /// </summary>
    public bool LifeFlat { get; init; }

    internal static SsdWindowStat Compute(DateOnly from, DateOnly to, List<DriveHealth> rows)
    {
        if (rows.Count == 0) return Empty with { From = from, To = to };

        var latest = rows[^1];

        bool lifeFlat = rows.Count >= 2
            && !double.IsNaN(rows[0].PercentageUsed)
            && rows.All(r => !double.IsNaN(r.PercentageUsed) && r.PercentageUsed == rows[0].PercentageUsed);

        double perWrite = double.NaN, perUsed = double.NaN, projected = double.NaN;

        // G5：不足 14 天只画线 + 当前值，不给速率也不给预估
        if (rows.Count >= MinRowsForTrend)
        {
            var f = rows[0];
            var span = Math.Max(1, latest.Date.DayNumber - f.Date.DayNumber);
            if (!double.IsNaN(f.DataWrittenGb) && !double.IsNaN(latest.DataWrittenGb))
                perWrite = (latest.DataWrittenGb - f.DataWrittenGb) / span;
            if (!double.IsNaN(f.PercentageUsed) && !double.IsNaN(latest.PercentageUsed))
                perUsed = (latest.PercentageUsed - f.PercentageUsed) / span;

            // G7：日均增量为 0（或倒退，换盘）时不给预估——编一个数字比不显示更糟
            if (perUsed > 0 && !double.IsNaN(latest.PercentageUsed))
                projected = (100.0 - latest.PercentageUsed) / perUsed;
        }

        return new SsdWindowStat(from, to, rows.Count, latest, perWrite, perUsed, projected)
        {
            LifeFlat = lifeFlat
        };
    }
}

/// <summary>
/// 非 CPU 信号的趋势报告（趋势页「硬件」视图与 <c>--trend</c> 的共用入口）。
/// </summary>
public sealed class HardwareTrendReport
{
    public required TrendWindow Window { get; init; }

    /// <summary>硬件数据的总跨度（GPU 与 SSD 起点相同但各自可能有缺口，取并集）。无数据时为 null。</summary>
    public DateOnly? FirstDay { get; set; }
    public DateOnly? LastDay { get; set; }

    /// <summary>
    /// 窗口内的每一天（含没有任何硬件数据的日子）。
    /// 【为什么必须含空白天】GPU 数据稀疏是常态，轴里没有这一天，
    /// 折线就会把两段没数据的日子连起来——那是伪造连续性（G6）。
    /// </summary>
    public IReadOnlyList<DateOnly> Axis { get; init; } = Array.Empty<DateOnly>();

    /// <summary>窗口内有 GPU 数据的天（按日期升序）。</summary>
    public IReadOnlyList<GpuDayStat> GpuDays { get; init; } = Array.Empty<GpuDayStat>();

    /// <summary>窗口内的 SSD 记录行（按日期升序）。</summary>
    public IReadOnlyList<DriveHealth> SsdRows { get; init; } = Array.Empty<DriveHealth>();

    public SsdWindowStat Ssd { get; init; } = SsdWindowStat.Empty;

    /// <summary>窗口内 GPU 使用总时长（小时）。</summary>
    public double GpuHours { get; init; }

    /// <summary>窗口内 GPU 加权均温（按样本数近似加权，G1）。无数据为 NaN。</summary>
    public double GpuTempMean { get; init; }

    /// <summary>
    /// 上一窗口（紧邻本期之前、等长）的 GPU 汇总——解读卡片的混杂因素证据要的是
    /// 「本期 vs 上期」的对比（ΔT 涨了是灰堵了还是更常用显卡：ΔT 涨了是灰堵了还是更常用显卡）。
    /// 图表只用本窗口数据，这两个字段不参与绘图。
    /// </summary>
    public double GpuHoursPrev { get; init; }
    public double GpuTempMeanPrev { get; init; }
    public int GpuDaysPrevCount { get; init; }

    /// <summary>窗口内 GPU 忙时（负载 &gt; 0）加权均温。无数据为 NaN。</summary>
    public double GpuActiveTempMean { get; init; }

    /// <summary>窗口内 GPU 闲时（负载 = 0）加权均温。无数据为 NaN。</summary>
    public double GpuIdleTempMean { get; init; }

    /// <summary>
    /// 本窗口 / 上窗口内有 GPU <b>负载</b>数据的天数（2026-10-16 口径）。
    /// 解读卡片据此判断时长对比是否跨口径：上窗口全是旧构建的数据时，
    /// "时长差"是开机时长与真实用量的差，不是使用习惯的变化——不出这条证据。
    /// </summary>
    public int GpuLoadDaysCount { get; init; }
    public int GpuLoadDaysPrevCount { get; init; }

    public bool HasAnyData => Axis.Count > 0 && (GpuDays.Count > 0 || SsdRows.Count > 0);

    private Dictionary<DateOnly, GpuDayStat>? _gpuMap;
    private Dictionary<DateOnly, DriveHealth>? _ssdMap;

    /// <summary>某天的 GPU 均温；没有数据返回 NaN（展示层渲染「—」或断线）。</summary>
    public double GpuTempOn(DateOnly day) =>
        (_gpuMap ??= GpuDays.ToDictionary(g => g.Date)).TryGetValue(day, out var g) ? g.TempMean : double.NaN;

    public double GpuHoursOn(DateOnly day) =>
        (_gpuMap ??= GpuDays.ToDictionary(g => g.Date)).TryGetValue(day, out var g) ? g.HoursUsed : double.NaN;

    public DriveHealth? SsdOn(DateOnly day) =>
        (_ssdMap ??= SsdRows.ToDictionary(r => r.Date)).TryGetValue(day, out var r) ? r : null;
}

/// <summary>
/// 非 CPU 信号（GPU 温度 / SSD 寿命）的取数层。
///
/// 【为什么单独一个类而不是并进 TrendAnalyzer】TrendAnalyzer 的全部口径围绕
/// 「单核峰值分档 + ΔT」建立，GPU 与 SSD 既没有负载档也不参与 ΔT；
/// 硬塞进去会让两个不相关的量互相污染。各自一个入口，展示层按需取。
///
/// 【只做取数与聚合】阈值、文案、颜色一律在 UI 层，本类一个判断都不做。
/// </summary>
public static class HardwareTrend
{
    /// <summary>
    /// 单日 GPU 聚合（G1：仅有 GPU 数据的片、按整片 <c>n</c> 加权）。
    /// </summary>
    /// <param name="rows">当日聚合行；调用方按需先过滤供电（R7：AC/DC 不混算）。</param>
    /// <param name="intervalSeconds">采样间隔，用于把样本数折算成使用时长。</param>
    /// <remarks>
    /// 【权重为什么是整片 n】GPU 样本数没有单独落列（CSV 只有整片 n），只能用整片 n
    /// 近似；没用显卡的片不进分子也不进分母（空 ≠ 0℃）。
    /// 【2026-10-16 口径修正】<see cref="GpuDayStat.HoursUsed"/> 旧口径是"采到 GPU 温度
    /// 即计入"——独显待机也报温度，该数字等于开机时长。新口径：只累计
    /// <b>负载 &gt; 0</b> 的样本，与 LiteMonitor 的「0% / 非 0」同一口径。
    /// 负载分档的日级加权用档内样本数（CSV 第 19/21/23/25/27 列落盘），
    /// 不再拿整片 n 近似——"3 个样本在 90% 档"按整片 353 个样本加权，
    /// 会把闲时温度算成忙时温度。
    /// </remarks>
    public static GpuDayStat GpuDay(IReadOnlyList<SliceRow> rows, DateOnly day, int intervalSeconds)
    {
        double sum = 0, utilSum = 0;
        long weight = 0, samples = 0, activeN = 0, utilN = 0;
        int slices = 0;
        var bucketSum = new double[SliceAggregate.GpuBucketCount];
        var bucketN = new long[SliceAggregate.GpuBucketCount];

        foreach (var r in rows)
        {
            if (r.Date != day) continue;
            if (double.IsNaN(r.GpuTempMean)) continue;

            var n = Math.Max(1, r.SampleCount);
            sum += r.GpuTempMean * n;
            weight += n;
            samples += r.SampleCount;
            slices++;

            // 负载分档：只有档内真有样本的才进分子（旧构建的行没有这些列 → 计数 0 → 跳过）
            for (int b = 0; b < bucketN.Length; b++)
            {
                var bn = r.GpuBucketCounts[b];
                var bt = r.GpuBucketTemps[b];
                if (bn <= 0 || double.IsNaN(bt)) continue;

                bucketSum[b] += bt * bn;
                bucketN[b] += bn;
                if (b > 0) activeN += bn;
            }

            if (!double.IsNaN(r.GpuUtilMean))
            {
                utilSum += r.GpuUtilMean * n;
                utilN += n;
            }
        }

        var bucketTemps = new double[bucketN.Length];
        for (int b = 0; b < bucketN.Length; b++)
            bucketTemps[b] = bucketN[b] > 0 ? bucketSum[b] / bucketN[b] : double.NaN;

        // 忙时均温 = 负载 > 0 的各档合并（按档内样本加权）
        double activeSum = 0;
        for (int b = 1; b < bucketN.Length; b++) activeSum += bucketSum[b];

        // 使用时长 = 负载 > 0 的累计时长。
        // 【缺失 vs 0】当天有 GPU 温度却没有任何负载读数（旧构建 / 传感器读不到）时
        // 必须给 NaN——给 0 等于宣称"那天没用显卡"，而真相是"没量到"。
        // 真闲着的一天（负载全 0）才有资格是 0。
        var hasLoad = activeN > 0 || bucketN[0] > 0;

        return new GpuDayStat(
            day,
            weight > 0 ? sum / weight : double.NaN,
            hasLoad ? activeN * intervalSeconds / 3600.0 : double.NaN,
            utilN > 0 ? utilSum / utilN : double.NaN,
            bucketN[0] > 0 ? bucketSum[0] / bucketN[0] : double.NaN,
            activeN > 0 ? activeSum / activeN : double.NaN,
            slices,
            samples,
            activeN,
            bucketTemps,
            bucketN);
    }

    /// <summary>从存储读当日行再聚合。GUI 今日页与 CLI <c>--today</c> 走这一条。</summary>
    public static GpuDayStat GpuDay(DateOnly day, int intervalSeconds) =>
        GpuDay(DailyStore.ReadRange(day, day), day, intervalSeconds);

    /// <summary>
    /// 生成非 CPU 信号趋势报告。入口与 <see cref="TrendAnalyzer.Build"/> 同形态：
    /// GUI 硬件视图与 <c>--trend</c> 调同一个方法（G4）。
    /// </summary>
    /// <remarks>
    /// 【为什么 GPU 不按供电拆开】趋势页的窗口聚合一向 AC+DC 合并统计
    /// （<see cref="TrendAnalyzer"/> 的同一条取舍：桌面场景以 AC 为主，DC 单独成组不现实）。
    /// 今日页仍按选中供电单算（R7），两个入口口径不同是刻意的。
    /// </remarks>
    public static HardwareTrendReport Build(AppSettings settings, TrendWindow window)
    {
        var interval = Math.Max(1, settings.Sampler.IntervalSeconds);
        var rows = DailyStore.ReadAll();
        var ssdAll = DriveHealthStore.ReadAll();

        // 硬件数据的总跨度：GPU 取「真有 GPU 温度那天」的起止，SSD 取记录起止，两者并集。
        // 【为什么先算跨度再聚合】窗口区间依赖总跨度（<see cref="TrendWindow.Resolve"/> 以 last 为锚），
        // 而跨度本身不需要逐日聚合：一天有没有 GPU 数据 = 那天有没有非 NaN 的 GPU 均温，
        // 与 <see cref="GpuDay"/> 的 HasData 同一条判据（权重只在非 NaN 的行上累加）。
        DateOnly? first = null, last = null;
        foreach (var r in rows)
        {
            if (double.IsNaN(r.GpuTempMean)) continue;
            if (first is null || r.Date < first) first = r.Date;
            if (last is null || r.Date > last) last = r.Date;
        }
        foreach (var r in ssdAll)
        {
            if (first is null || r.Date < first) first = r.Date;
            if (last is null || r.Date > last) last = r.Date;
        }

        if (first is null || last is null)
            return new HardwareTrendReport { Window = window };

        var (from, to, prevFrom, prevTo) = window.Resolve(first.Value, last.Value);

        // 只聚合窗口涉及的日期（本期 + 上期）。窗口之外的历史只参与上面的跨度判断，
        // 不再逐日构建 GpuDayStat——展示层要的只有这两段。
        var gpuByDay = new Dictionary<DateOnly, GpuDayStat>();
        foreach (var g in rows.Where(r => r.Date >= prevFrom && r.Date <= to).GroupBy(r => r.Date))
        {
            var stat = GpuDay(g.ToList(), g.Key, interval);
            if (stat.HasData) gpuByDay[g.Key] = stat;
        }

        var axis = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1)) axis.Add(d);

        var gpuDays = gpuByDay
            .Where(kv => kv.Key >= from && kv.Key <= to)
            .Select(kv => kv.Value)
            .OrderBy(g => g.Date)
            .ToList();

        var ssdRows = ssdAll.Where(r => r.Date >= from && r.Date <= to).ToList();
        var ssd = SsdWindowStat.Compute(from, to, ssdRows);

        // 窗口内 GPU 汇总（页脚结论用）：时长求和、均温按样本数加权（G1 的近似）
        var (hours, tempMean, activeMean, idleMean) = GpuTotals(gpuDays);
        // 上一窗口汇总（解读卡片的混杂因素证据用，3.4）
        var gpuPrevDays = gpuByDay
            .Where(kv => kv.Key >= prevFrom && kv.Key <= prevTo)
            .Select(kv => kv.Value)
            .ToList();
        var (hoursPrev, tempMeanPrev, _, _) = GpuTotals(gpuPrevDays);

        return new HardwareTrendReport
        {
            Window = window,
            FirstDay = first,
            LastDay = last,
            Axis = axis,
            GpuDays = gpuDays,
            SsdRows = ssdRows,
            Ssd = ssd,
            GpuHours = hours,
            GpuTempMean = tempMean,
            GpuActiveTempMean = activeMean,
            GpuIdleTempMean = idleMean,
            GpuHoursPrev = hoursPrev,
            GpuTempMeanPrev = tempMeanPrev,
            GpuDaysPrevCount = gpuPrevDays.Count,
            GpuLoadDaysCount = gpuDays.Count(g => g.HasLoadData),
            GpuLoadDaysPrevCount = gpuPrevDays.Count(g => g.HasLoadData)
        };
    }

    /// <summary>
    /// 一组天的 GPU 汇总：总时长 + 按样本数加权的均温（无数据为 NaN）。
    /// 忙时 / 闲时均温按各自档内样本数加权（2026-10-16 口径）。
    /// 时长跳过 NaN 的天（旧构建没有负载列）——把"没量到"累加成 0 会伪造"没用显卡"。
    /// </summary>
    private static (double Hours, double TempMean, double ActiveTempMean, double IdleTempMean)
        GpuTotals(List<GpuDayStat> days)
    {
        double tempSum = 0, activeSum = 0, idleSum = 0;
        long tempWeight = 0, activeWeight = 0, idleWeight = 0;
        double hours = 0;
        var anyHours = false;
        foreach (var g in days)
        {
            if (!double.IsNaN(g.HoursUsed)) { hours += g.HoursUsed; anyHours = true; }
            if (!double.IsNaN(g.TempMean))
            {
                tempSum += g.TempMean * Math.Max(1, g.Samples);
                tempWeight += Math.Max(1, g.Samples);
            }
            if (!double.IsNaN(g.ActiveTempMean) && g.ActiveSamples > 0)
            {
                activeSum += g.ActiveTempMean * g.ActiveSamples;
                activeWeight += g.ActiveSamples;
            }
            if (!double.IsNaN(g.IdleTempMean) && g.BucketCounts[0] > 0)
            {
                idleSum += g.IdleTempMean * g.BucketCounts[0];
                idleWeight += g.BucketCounts[0];
            }
        }
        return (anyHours ? hours : double.NaN,
            tempWeight > 0 ? tempSum / tempWeight : double.NaN,
            activeWeight > 0 ? activeSum / activeWeight : double.NaN,
            idleWeight > 0 ? idleSum / idleWeight : double.NaN);
    }
}
