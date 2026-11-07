namespace ThermalTrace.Analysis;

using ThermalTrace.Model;

/// <summary>
/// 单日、单个展示类别、单一供电状态（AC/DC）的聚合统计。
/// 由当天落入该类别的若干 10% 细档行按样本数加权合并而来。
/// </summary>
public sealed class CatDayStat
{
    public int Category { get; init; }
    public bool OnAc { get; init; }

    private long _samples;
    private double _tempW, _tempWeight;
    private double _powerW, _powerWeight;
    private double _over85W, _over90W, _overWeight;
    private double _tempMax = double.NaN;

    public void Add(SliceRow r)
    {
        var n = Math.Max(1, r.SampleCount);
        _samples += r.SampleCount;

        if (!double.IsNaN(r.TempMean)) { _tempW += r.TempMean * n; _tempWeight += n; }
        if (!double.IsNaN(r.PowerW)) { _powerW += r.PowerW * n; _powerWeight += n; }
        if (!double.IsNaN(r.Over85Pct)) { _over85W += r.Over85Pct * n; _overWeight += n; }
        if (!double.IsNaN(r.Over90Pct)) { _over90W += r.Over90Pct * n; }
        if (!double.IsNaN(r.TempMax))
            _tempMax = double.IsNaN(_tempMax) ? r.TempMax : Math.Max(_tempMax, r.TempMax);
    }

    /// <summary>用当日空闲基线与采样间隔补齐派生指标。</summary>
    public void Finalize(double idleBaseline, int intervalSeconds)
    {
        TempMean = _tempWeight > 0 ? _tempW / _tempWeight : double.NaN;
        PowerMean = _powerWeight > 0 ? _powerW / _powerWeight : double.NaN;
        Over85Pct = _overWeight > 0 ? _over85W / _overWeight : double.NaN;
        Over90Pct = _overWeight > 0 ? _over90W / _overWeight : double.NaN;
        TempMax = _tempMax;

        DeltaT = !double.IsNaN(TempMean) && !double.IsNaN(idleBaseline)
            ? TempMean - idleBaseline : double.NaN;

        // 每瓦温升 K/W：纯物理量，跨季节可比
        KPerW = !double.IsNaN(TempMean) && !double.IsNaN(PowerMean) && PowerMean >= 0.5
            ? TempMean / PowerMean : double.NaN;

        HoursActive = _samples * intervalSeconds / 3600.0;
        HoursOver85 = !double.IsNaN(Over85Pct) ? HoursActive * Over85Pct / 100.0 : 0;
        HoursOver90 = !double.IsNaN(Over90Pct) ? HoursActive * Over90Pct / 100.0 : 0;
    }

    public long Samples => _samples;
    public double TempMean { get; private set; }
    public double PowerMean { get; private set; }
    public double Over85Pct { get; private set; }
    public double Over90Pct { get; private set; }
    public double TempMax { get; private set; }
    public double DeltaT { get; private set; }
    public double KPerW { get; private set; }
    public double HoursActive { get; private set; }
    public double HoursOver85 { get; private set; }
    public double HoursOver90 { get; private set; }
}

/// <summary>单日趋势行：空闲基线 + 环境气温 + 各类别 AC/DC 统计。</summary>
public sealed class DailyTrend
{
    public required DateOnly Date { get; init; }
    public double IdleBaseline { get; set; } = double.NaN;
    public string IdleBaselineBand { get; set; } = "";
    public bool IdleApproximated { get; set; }
    public double AmbientMean { get; set; } = double.NaN;
    public Dictionary<int, CatDayStat> Ac { get; } = new();
    public Dictionary<int, CatDayStat> Dc { get; } = new();

    public CatDayStat? Get(int category, bool onAc) =>
        (onAc ? Ac : Dc).TryGetValue(category, out var s) ? s : null;
}

/// <summary>两个滚动时间窗口（近 7 天 vs 前 7 天 / 近 30 天 vs 前 30 天）在某类别上的对比。</summary>
public sealed record CategoryComparison(
    string Category,
    bool CurHasData, bool PrevHasData,
    double? CurDeltaT, double? PrevDeltaT, double? DeltaTDiff,
    double? CurOver85Hours, double? PrevOver85Hours,
    double? CurOver85Pct, double? PrevOver85Pct,
    double? CurKPerW, double? PrevKPerW, double? KPerWDiff,
    double? CurHours, double? PrevHours,
    long? CurSamples, long? PrevSamples)
{
    public bool Comparable => CurHasData && PrevHasData;
}

/// <summary>窗口内单个类别的累计量（AC / DC 合并，样本加权）。</summary>
public sealed record CategoryUsage(int Category, long Samples, double Hours);

/// <summary>最小二乘趋势斜率。</summary>
public sealed record TrendSlope(double PerDay, int Points)
{
    public bool HasTrend => Points >= 3;
}

/// <summary>
/// 趋势对比的时间窗口：本期 CurDays 天 vs 紧邻其前的 PrevDays 天。
///
/// 【为什么要有它】<c>TrendAnalyzer.Build</c> 原本把 7 天 / 30 天两套窗口写死，
/// 而趋势页要能切换时间范围。加它时用<b>可选参数</b>：<c>window == null</c> 时行为保持不变，
/// CLI <c>--trend</c> 一个字都不用改。
///
/// 【几何约定】两个窗口首尾相接、不留空隙：
/// 本期 = [last-(CurDays-1), last]，上期 = [curFrom-PrevDays, curFrom-1]。
/// 这与原实现的 last-6..last / last-13..last-7 逐日对齐。
/// </summary>
public sealed record TrendWindow(int CurDays, int PrevDays, string Label)
{
    /// <summary>
    /// 清灰基准日（G8）。设置后本期固定为 [基准日, 最后一天]，不再按天数回推；
    /// null 表示按 <see cref="CurDays"/> 的滚动窗口。
    /// </summary>
    public DateOnly? CleanFrom { get; init; }

    /// <summary>CurDays ≤ 0 = "全部数据"：本期取 [第一天, 最后一天]，上期取等长的前一段。</summary>
    public bool IsAll => CurDays <= 0 && CleanFrom is null;

    /// <summary>「自上次清灰」：上期取清灰日前与本期等长的一段（G8）。</summary>
    public bool IsSinceClean => CleanFrom is not null;

    public static TrendWindow Week { get; } = new(7, 7, "近 7 天 vs 前 7 天");
    public static TrendWindow Month { get; } = new(30, 30, "近 30 天 vs 前 30 天");
    public static TrendWindow All { get; } = new(0, 0, "全部数据");

    /// <summary>
    /// 「自上次清灰」窗口：从清灰基准日到最后一个有数据的日子。
    /// 【单窗口】解读不拿它跟基准日前的一段做对比（G8 第二轮修正）；<see cref="Resolve"/>
    /// 仍给出等长前区间，供硬件视图等既有调用方不变，解读引擎只读本期区间。
    /// </summary>
    public static TrendWindow SinceClean(DateOnly cleanDate) =>
        new(0, 0, "自上次清灰以来") { CleanFrom = cleanDate };

    /// <summary>把窗口解析成具体日期区间。<see cref="IsAll"/> 时按实际数据跨度等长回推。</summary>
    public (DateOnly CurFrom, DateOnly CurTo, DateOnly PrevFrom, DateOnly PrevTo)
        Resolve(DateOnly first, DateOnly last)
    {
        if (CleanFrom is { } clean)
        {
            int n = Math.Max(1, last.DayNumber - clean.DayNumber + 1);
            var beforeTo = clean.AddDays(-1);
            return (clean, last, beforeTo.AddDays(-(n - 1)), beforeTo);
        }

        if (IsAll)
        {
            int n = Math.Max(1, last.DayNumber - first.DayNumber + 1);
            return (first, last, first.AddDays(-n), first.AddDays(-1));
        }

        var curTo = last;
        var curFrom = last.AddDays(-(Math.Max(1, CurDays) - 1));
        var prevTo = curFrom.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(Math.Max(1, PrevDays) - 1));
        return (curFrom, curTo, prevFrom, prevTo);
    }
}

/// <summary>完整趋势报告数据。</summary>
public sealed class TrendReport
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public DateOnly? FirstDay { get; set; }
    public DateOnly? LastDay { get; set; }
    public int DaysWithData { get; set; }
    public required List<DailyTrend> Days { get; init; }
    public required List<CategoryComparison> Week { get; init; }
    public required List<CategoryComparison> Month { get; init; }

    /// <summary>本报告按哪路供电算出（R7）。展示层据此取数，不再各自猜"优先 AC"。</summary>
    public bool OnAc { get; init; } = true;

    /// <summary>
    /// 指定窗口时的对比结果（趋势页用）。未指定窗口时按 30 天窗口产出，
    /// 保证任何调用方读它都有一份可用的对比，不必回头去猜该读 Week 还是 Month。
    /// </summary>
    public List<CategoryComparison> Comparison { get; init; } = new();
    public required Dictionary<int, TrendSlope> Slopes { get; init; }
    public required List<CaliberRecord> Calibers { get; init; }
    public List<string> CaliberWarnings { get; } = new();

    public bool HasWeekComparison => Week.Exists(w => w.Comparable);
    public bool HasMonthComparison => Month.Exists(m => m.Comparable);
}
