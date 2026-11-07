namespace ThermalTrace.Analysis;

using ThermalTrace.Model;

// ==================================================================
// 墨水屏图的**唯一**数据模型。
//
// 【2026-10-17】旧链（EinkModel + EinkModelBuilder + A/B/C 版面）已整体删除，
// 本文件是出图的唯一数据源；推送（PushService）与 --eink 走同一条链，一个数都不另算。
//
// 【为什么全画 5 条】办公本的重度/满载一天只有几分钟，"挑一个主类别"根本不成立。
// 【为什么不吃 Interpreter】解读（健康度/总结/建议）不上图，图只吃 TrendReport。
// 【为什么区间不写死】跟随 settings——GUI 里改负载区间，图的分区跟着改。
// ==================================================================

/// <summary>墨水屏的两个图片页槽位。槽 1 保持固定趋势版面，槽 2 走自清灰以来的平滑版面。</summary>
public enum EinkSlot
{
    Slot1 = 1,
    Slot2 = 2
}

/// <summary>
/// 一屏快照：5 条负载档 ΔT 日序列 + 加权总体趋势。
/// 槽 1 为近 30 日逐日线；槽 2 为自清灰以来的平滑线 + 固定预测线。
/// </summary>
public sealed record EinkSnapshot(
    string Title,
    DateOnly DataDay,
    /// <summary>距清灰天数；null = 未设基准日（黑栏右侧不画）。</summary>
    int? CleanDays,
    IReadOnlyList<EinkLine> Lines)
{
    /// <summary>
    /// 样本数加权最小二乘（WLS）拟合的总体趋势。不显著（|斜率| &lt; 0.02℃/天或
    /// 有效点 &lt; 3）时为 null，渲染层不画趋势线。
    /// </summary>
    public WeightedTrend? Trend { get; init; }

    /// <summary>
    /// 窗口天数（近30日 → 30）。渲染层要把它单独画成小一号的字（"ThermalTrace 30"），
    /// 所以不能只给拼好的 <see cref="Title"/>。
    /// </summary>
    public int SpanDays { get; init; }

    /// <summary>槽标识；默认槽 1，渲染层据此分派版面。</summary>
    public EinkSlot Slot { get; init; } = EinkSlot.Slot1;

    /// <summary>槽 2 的平滑窗口天数（5/7/15）；0 = 不平滑（槽 1）。</summary>
    public int SmoothWindowDays { get; init; }

    /// <summary>
    /// 槽 2 取数用的清灰基准日（来自设置，不按首末数据日推算）。
    /// 渲染层据此画 "ThermalTrace since yyyy-MM-dd"；null = 未设置清灰日（槽 2 不适用）。
    /// </summary>
    public DateOnly? CleanDate { get; init; }

    /// <summary>
    /// 槽 2 不适用的原因（无清灰日 / 清灰日晚于末数据日 / 有效日不足 3 天等）。
    /// null = 本槽可正常出图。不适用不是失败，不写推送状态。
    /// </summary>
    public string? SkipReason { get; init; }

    public bool IsApplicable => SkipReason is null;

    public bool HasData => Lines.Any(l => l.Points.Any(p => p.DeltaT is not null));
}

/// <summary>一条负载档的日序列。</summary>
public sealed record EinkLine(string Name, IReadOnlyList<EinkWeightedPoint> Points)
{
    /// <summary>
    /// 窗口内该档的累计时长（小时），取自 <c>CatDayStat.HoursActive</c>——
    /// 右缘标签 `1·162h` 里的小时数。**不要拿样本数反推采样间隔换算**，
    /// 那个常数会与采样器配置打架；HoursActive 已经按 intervalSeconds 算好了。
    /// </summary>
    public double TotalHours { get; init; }
}

/// <summary>
/// 曲线上的一个点。<see cref="DeltaT"/> 为 null = 该日该档没采到，渲染时必须断开；
/// <see cref="Weight"/> = 该日该档样本数（WLS 的权重）；
/// <see cref="Hours"/> = 该日该档时长（小时）。
/// </summary>
public sealed record EinkWeightedPoint(DateOnly Day, double? DeltaT, long Weight, double Hours);

/// <summary>加权总体趋势：斜率（℃/天）与窗口首末日的拟合值。</summary>
public sealed record WeightedTrend(double PerDay, double From, double To);

/// <summary>
/// 槽 2 适用性结论。由 <see cref="EinkSnapshotBuilder.Slot2Applicability"/> 一处给出，
/// 推送、<c>--eink</c> 与设置页共用同一份判定与文案，不允许任何一方另算一套。
/// </summary>
public readonly record struct Slot2Verdict(
    bool Applicable,
    string? SkipReason,
    int SpanDays,
    int SmoothWindowDays,
    DateOnly? CleanDate);

/// <summary>
/// 组装近30日快照。纯函数：无 IO、无 UI 依赖、不碰 CSV、不调 Interpreter。
/// </summary>
public static class EinkSnapshotBuilder
{
    /// <summary>
    /// 斜率显著阈值 0.02℃/天，与 <see cref="Interpreter.SlopeSignificant"/> 同一判据：
    /// 项目里"够不够格叫趋势"只有这一个标准，不能墨水屏另立一套。
    /// </summary>
    private const double SlopeSignificant = 0.02;

    /// <summary>至少这么多天数据才谈得上"趋势"（与趋势页"不足 3 天不画图"同一条线）。</summary>
    public const int MinDaysToDraw = 3;

    /// <summary>槽 1（近 30 日逐日线）。</summary>
    public static EinkSnapshot Build(AppSettings settings, TrendReport report)
        => BuildSlot1(settings, report);

    public static EinkSnapshot Build(AppSettings settings, TrendReport report, EinkSlot slot)
        => slot == EinkSlot.Slot2 ? BuildSlot2(settings, report) : BuildSlot1(settings, report);

    /// <summary>图幅只有 400×300 1bpp，标题里用两个字点明本次供电口径。</summary>
    private static string PowerTag(TrendReport report) => report.OnAc ? "交流" : "电池";

    private static EinkSnapshot BuildSlot1(AppSettings settings, TrendReport report)
    {
        var first = report.FirstDay;
        var last = report.LastDay;
        if (first is null || last is null)
            return new EinkSnapshot($"ThermalTrace 近30日·{PowerTag(report)}", last ?? default, null, Array.Empty<EinkLine>())
            {
                SpanDays = TrendWindow.Month.CurDays
            };

        var lastDay = last.Value;
        var (curFrom, _, _, _) = TrendWindow.Month.Resolve(first.Value, lastDay);

        int count = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);
        var lines = new List<EinkLine>();
        for (int c = 0; c < count; c++)
        {
            var pts = new List<EinkWeightedPoint>();
            double totalHours = 0;
            foreach (var d in report.Days)
            {
                if (d.Date < curFrom || d.Date > lastDay) continue;
                // 取报告口径那一侧的统计；权重取同侧样本数，线和趋势必须来自同一批数。
                var s = d.Get(c, report.OnAc);
                var hasDt = s?.DeltaT is { } dt && !double.IsNaN(dt);
                var hours = s?.HoursActive ?? 0;
                // 累计时长只算真正画出 ΔT 的那些天，与图上可见的点保持一致
                if (hasDt) totalHours += hours;
                pts.Add(new EinkWeightedPoint(
                    d.Date,
                    hasDt ? s!.DeltaT : null,
                    s?.Samples ?? 0,
                    hours));
            }

            // 整个类别一次都没采到就不建线（与 GUI 趋势页同一条规则：图例不出现没数据的线）
            if (pts.Any(p => p.DeltaT is not null))
                lines.Add(new EinkLine(settings.CategoryName(c), pts) { TotalHours = totalHours });
        }

        // 距清灰天数：纯日历事实（今天 − 基准日），不等 14 天数据、不靠 Interpreter。
        // 这正是旧版"距清灰"挂在 interp.Clean 下被门槛挡住的纠正。
        int? cleanDays = settings.CleanDate is { } cd ? lastDay.DayNumber - cd.DayNumber : null;

        int spanDays = (int)(lastDay.DayNumber - curFrom.DayNumber) + 1;

        return new EinkSnapshot($"ThermalTrace {spanDays}·{PowerTag(report)}", lastDay, cleanDays, lines)
        {
            Trend = FitWeighted(lines, curFrom, lastDay),
            SpanDays = spanDays
        };
    }

    /// <summary>
    /// 槽 2 适用性判定（纯函数）：只吃首末数据日与窗口内有效日数，不需要完整 <see cref="TrendReport"/>。
    /// 设置页显示的那一行状态、推送与 <c>--eink</c> 的跳过原因都从这一处出，文案与判定不允许分家。
    /// </summary>
    public static Slot2Verdict Slot2Applicability(
        AppSettings settings, DateOnly? firstDay, DateOnly? lastDay, int effectiveDays)
    {
        if (firstDay is not { } first || lastDay is not { } last)
            return new Slot2Verdict(false, "没有可绘制的数据日", 0, 0, null);

        if (settings.CleanDate is not { } clean)
            return new Slot2Verdict(false, "未设置清灰基准日，槽 2 不适用", 0, 0, null);

        if (clean > last)
            return new Slot2Verdict(false,
                $"清灰基准日 {clean:yyyy-MM-dd} 晚于最后数据日 {last:yyyy-MM-dd}，槽 2 不适用",
                0, 0, clean);

        var from = clean > first ? clean : first;
        var spanDays = last.DayNumber - from.DayNumber + 1;
        if (spanDays <= 0)
            return new Slot2Verdict(false, "自清灰以来没有可绘制的窗口", 0, 0, clean);

        var window = SmoothWindowForSpan(spanDays);
        if (effectiveDays < MinDaysToDraw)
            return new Slot2Verdict(false,
                $"窗口内有效日 {effectiveDays} 天，不足 {MinDaysToDraw} 天，槽 2 不适用",
                spanDays, window, clean);

        return new Slot2Verdict(true, null, spanDays, window, clean);
    }

    /// <summary>
    /// 从一份现成报告数出槽 2 窗口内的有效日数（有任一类别的 ΔT 可用即算一天）。
    /// 与 <see cref="BuildSlot2"/> 建原始数组时的计数同口径；供设置页在命中共享报告时使用，
    /// 避免为了一个数字再把 CSV 全量扫一遍。
    /// </summary>
    public static int Slot2EffectiveDays(AppSettings settings, TrendReport report)
    {
        if (report.FirstDay is not { } first || report.LastDay is not { } last) return 0;
        if (settings.CleanDate is not { } clean) return 0;

        var from = clean > first ? clean : first;
        int count = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);
        int effective = 0;
        foreach (var day in report.Days)
        {
            if (day.Date < from || day.Date > last) continue;
            for (int c = 0; c < count; c++)
            {
                CatDayStat? s = day.Get(c, report.OnAc);
                if (s?.DeltaT is { } dt && !double.IsNaN(dt)) { effective++; break; }
            }
        }
        return effective;
    }

    /// <summary>
    /// 槽 2：自清灰以来，按日历日连续排点，做居中、按样本数加权的移动平均。
    /// 窗口按跨度选 5/7/15 天；两端裁剪、缺日跳过、不补 0。
    /// 另对全部非缺失平滑点做一次池化加权拟合，固定输出预测线（无显著性门槛）。
    /// </summary>
    private static EinkSnapshot BuildSlot2(AppSettings settings, TrendReport report)
    {
        // 适用性判定与设置页 / 推送共用 Slot2Applicability：先按"有效日已够"问一次拿区间与提前退出分支，
        // 有效日数算出来之后再问一次，判定与文案始终只有一处。
        var pre = Slot2Applicability(settings, report.FirstDay, report.LastDay, MinDaysToDraw);
        if (!pre.Applicable)
            return Skip(report.LastDay ?? default, pre.SkipReason!, null,
                pre.SmoothWindowDays, pre.SpanDays, pre.CleanDate);

        var first = report.FirstDay!.Value;
        var lastDay = report.LastDay!.Value;
        var clean = pre.CleanDate!.Value;   // 适用分支必然带清灰日，见 Slot2Applicability
        var from = clean > first ? clean : first;
        var spanDays = pre.SpanDays;
        var window = pre.SmoothWindowDays;
        var half = window / 2;

        int count = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);
        var byDate = new Dictionary<DateOnly, DailyTrend>(report.Days.Count);
        foreach (var d in report.Days) byDate[d.Date] = d;

        // 原始日值：按日历日连续排列，缺测日保留 null，不补 0。
        var raw = new (double? Delta, long Weight, double Hours)[count][];
        for (int c = 0; c < count; c++)
            raw[c] = new (double?, long, double)[spanDays];

        int effectiveDays = 0;
        for (int i = 0; i < spanDays; i++)
        {
            var day = byDate.GetValueOrDefault(from.AddDays(i));
            bool any = false;
            for (int c = 0; c < count; c++)
            {
                CatDayStat? s = day?.Get(c, report.OnAc);
                var hasDt = s?.DeltaT is { } dt && !double.IsNaN(dt);
                if (hasDt) any = true;
                raw[c][i] = (
                    hasDt ? s!.DeltaT : null,
                    hasDt ? Math.Max(1L, s!.Samples) : 0,
                    s?.HoursActive ?? 0);
            }
            if (any) effectiveDays++;
        }

        var verdict = Slot2Applicability(settings, report.FirstDay, report.LastDay, effectiveDays);
        if (!verdict.Applicable)
            return Skip(lastDay, verdict.SkipReason!, null,
                verdict.SmoothWindowDays, verdict.SpanDays, verdict.CleanDate);

        var lines = new List<EinkLine>();
        for (int c = 0; c < count; c++)
        {
            var pts = new List<EinkWeightedPoint>(spanDays);
            double totalHours = 0;
            for (int i = 0; i < spanDays; i++)
            {
                double num = 0;
                long den = 0;
                for (int j = Math.Max(0, i - half); j <= Math.Min(spanDays - 1, i + half); j++)
                {
                    var v = raw[c][j];
                    if (v.Delta is not { } dv) continue;
                    var w = Math.Max(1L, v.Weight);
                    num += dv * w;
                    den += w;
                }
                var smoothed = den > 0 ? num / den : (double?)null;
                if (raw[c][i].Delta is not null) totalHours += raw[c][i].Hours;
                pts.Add(new EinkWeightedPoint(from.AddDays(i), smoothed, den, raw[c][i].Hours));
            }
            if (pts.Any(p => p.DeltaT is not null))
                lines.Add(new EinkLine(settings.CategoryName(c), pts) { TotalHours = totalHours });
        }

        if (lines.Count == 0)
            return Skip(lastDay, "窗口内没有可绘制的平滑线，槽 2 不适用", null,
                window, spanDays, clean);

        int cleanDays = lastDay.DayNumber - clean.DayNumber;
        return new EinkSnapshot($"ThermalTrace since {clean:yyyy-MM-dd}·{PowerTag(report)}", lastDay, cleanDays, lines)
        {
            Slot = EinkSlot.Slot2,
            SpanDays = spanDays,
            SmoothWindowDays = window,
            CleanDate = clean,
            Trend = FitWeighted(lines, from, lastDay, requireSignificant: false)
        };
    }

    private static EinkSnapshot Skip(DateOnly lastDay, string reason, int? cleanDays,
                                     int smoothWindow = 0, int spanDays = 0, DateOnly? cleanDate = null)
        => new("ThermalTrace slot2", lastDay, cleanDays, Array.Empty<EinkLine>())
        {
            Slot = EinkSlot.Slot2,
            SmoothWindowDays = smoothWindow,
            SpanDays = spanDays,
            CleanDate = cleanDate,
            SkipReason = reason
        };

    /// <summary>槽 2 平滑窗口：≤30 天取 5，31–90 天取 7，&gt;90 天取 15（均为奇数，居中）。</summary>
    private static int SmoothWindowForSpan(int spanDays)
        => spanDays <= 30 ? 5 : spanDays <= 90 ? 7 : 15;

    /// <summary>
    /// 对 5×N 全部非缺失点做**按样本数加权**的最小二乘。
    ///
    /// 【为什么不能等权 pooled OLS】每日每档使用时长不同——等权等于让"用了 5 分钟"
    /// 和"用了 5 小时"说一样响的话（2026-10-16 提出）。权重 = 该日该档样本数 n，
    /// 与 architecture.md 口径约定 1（所有聚合按 n 加权）同一纪律。
    ///
    /// 【拟合线跨缺失日是允许的】它是拟合不是数据连线；数据线的断开纪律
    /// （缺失日不连过去）只约束 5 条实线，不约束这条光滑估计。
    /// </summary>
    /// <param name="requireSignificant">
    /// 槽 1 沿用 0.02℃/天 门槛；槽 2 的加权平均预测线按 v1.2 固定绘制，传 false。
    /// </param>
    private static WeightedTrend? FitWeighted(List<EinkLine> lines, DateOnly from, DateOnly to,
                                              bool requireSignificant = true)
    {
        double s0 = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        int n = 0;

        foreach (var line in lines)
            foreach (var p in line.Points)
            {
                if (p.DeltaT is not { } y) continue;
                var w = Math.Max(1.0, p.Weight);   // 权重=样本数；0 保底为 1（有 ΔT 就有样本）
                var x = (double)(p.Day.DayNumber - from.DayNumber);
                s0 += w; sx += w * x; sy += w * y; sxx += w * x * x; sxy += w * x * y;
                n++;
            }

        if (n < 3) return null;

        var denom = s0 * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return null;

        var slope = (s0 * sxy - sx * sy) / denom;
        if (requireSignificant && Math.Abs(slope) < SlopeSignificant) return null;   // 槽 1：不显著不画

        var intercept = (sy - slope * sx) / s0;
        var x1 = (double)(to.DayNumber - from.DayNumber);
        return new WeightedTrend(slope, intercept, intercept + slope * x1);
    }
}
