using ThermalTrace.Core;
using ThermalTrace.Model;
using ThermalTrace.Storage;

namespace ThermalTrace.Analysis;

/// <summary>
/// 趋势分析核心。回答唯一的核心问题：
/// <b>"同等真实负载下，现在比一个月前热了多少度？"</b>
///
/// 分析流程：
///  1. DailyStore.ReadAll() 读全部 10% 细档行，按选中供电过滤（R7）
///  2. 按展示类别（settings.CategoryLowerBounds）归并为单日统计
///  3. 每日 ΔT = 类别均温 − 当日空闲档最低温（消除季节/室温影响）
///  4. 滑动窗口：近 7 天 vs 前 7 天、近 30 天 vs 前 30 天
///  5. 最小二乘斜率 + 硬件口径校验
/// </summary>
public static class TrendAnalyzer
{
    /// <summary>
    /// 生成趋势报告。
    ///
    /// 【window 参数是可选的】不传 = 原有行为（7 天 / 30 天两套对比 + 全量斜率），
    /// CLI <c>--trend</c> 与 HTML 报告零改动；传了就只按该窗口产出
    /// <see cref="TrendReport.Comparison"/>，斜率也收敛到该窗口覆盖的天数内
    /// （否则"看近 7 天"却给出全量斜率，图与结论互相打脸）。
    /// </summary>
    /// <param name="onAc">
    /// 只取该路供电的行。放在数据入口过滤，下游 BuildComparisons / BuildSlopes /
    /// Interpreter / EinkModel / HTML 报告自然同口径，不必各自再写
    /// "优先 AC、没有再取 DC"的三元式（2026-11-06 口径统一）。
    /// </param>
    public static TrendReport Build(AppSettings settings, TrendWindow? window = null, bool onAc = true)
    {
        var rows = DailyStore.ReadAll().Where(r => r.OnAcPower == onAc).ToList();
        var interval = Math.Max(1, settings.Sampler.IntervalSeconds);

        var days = new List<DailyTrend>();
        // 分组只做一次并把当日子集物化：DailyIdleBaseline 原先把全量历史逐日再过滤一遍，
        // 复杂度是"天数 × 总行数"，一年数据下会成为 Build 的主要热点。
        var byDate = rows.GroupBy(r => r.Date).OrderBy(g => g.Key).ToList();

        foreach (var g in byDate)
        {
            var dayRows = g.ToList();
            var day = new DailyTrend { Date = g.Key };
            var (baseline, approximated, baselineBand) = Aggregator.DailyIdleBaseline(dayRows, g.Key);
            day.IdleBaseline = baseline;
            day.IdleBaselineBand = baselineBand;
            day.IdleApproximated = approximated;

            var ambientVals = dayRows.Select(r => AmbientLookup.At(r.SliceStart))
                .Where(v => !double.IsNaN(v)).Distinct().ToList();
            if (ambientVals.Count > 0) day.AmbientMean = ambientVals.Average();

            foreach (var r in dayRows)
            {
                var dash = r.BandKey.IndexOf('-');
                if (dash < 1 || !int.TryParse(r.BandKey[..dash], out var lower)) continue;
                var cat = settings.CategoryOf(lower);
                var map = r.OnAcPower ? day.Ac : day.Dc;
                if (!map.TryGetValue(cat, out var stat))
                {
                    stat = new CatDayStat { Category = cat, OnAc = r.OnAcPower };
                    map[cat] = stat;
                }
                stat.Add(r);
            }

            foreach (var s in day.Ac.Values) s.Finalize(day.IdleBaseline, interval);
            foreach (var s in day.Dc.Values) s.Finalize(day.IdleBaseline, interval);
            days.Add(day);
        }

        var report = new TrendReport
        {
            GeneratedAt = DateTimeOffset.Now,
            OnAc = onAc,
            FirstDay = days.FirstOrDefault()?.Date,
            LastDay = days.LastOrDefault()?.Date,
            DaysWithData = days.Count,
            Days = days,
            Week = new List<CategoryComparison>(),
            Month = new List<CategoryComparison>(),
            Slopes = new Dictionary<int, TrendSlope>(),
            Calibers = FingerprintStore.Load()
        };

        if (days.Count > 0)
        {
            var last = days[^1].Date;
            var first = days[0].Date;

            if (window is null)
            {
                // 旧路径：7 天 / 30 天两套对比都算，--trend 的行为一个字都不变
                BuildComparisons(settings, days, report,
                    last.AddDays(-6), last,
                    last.AddDays(-13), last.AddDays(-7),
                    report.Week);
                BuildComparisons(settings, days, report,
                    last.AddDays(-29), last,
                    last.AddDays(-59), last.AddDays(-30),
                    report.Month);
            }

            // 新路径：按（或默认按 30 天）窗口产出 Comparison
            var resolved = window ?? TrendWindow.Month;
            var (curFrom, curTo, prevFrom, prevTo) = resolved.Resolve(first, last);
            BuildComparisons(settings, days, report, curFrom, curTo, prevFrom, prevTo, report.Comparison);

            // 斜率窗口随选择收敛：看 7 天就只按这 7 天（+对照的上期）算趋势
            BuildSlopes(settings, days, report, window is null ? null : prevFrom);
            CheckCalibers(days, report);
        }

        return report;
    }

    /// <summary>
    /// 挑出"最值得说"的那条斜率：点数最多、且每日变化 ≥ 0.02℃。
    /// 与 HTML 报告结论区（HtmlTrendReport.AppendSlopes）同一个取舍规则，
    /// 避免 GUI 页脚和 --trend 报告各挑各的、给出两个不同结论。
    /// </summary>
    public static (int Category, TrendSlope Slope)? PrimarySlope(TrendReport report) =>
        report.Slopes
            .Where(kv => kv.Value.HasTrend && Math.Abs(kv.Value.PerDay) >= TrendThresholds.SlopeSignificant)
            .OrderByDescending(kv => kv.Value.Points)
            .Select(kv => ((int Category, TrendSlope Slope)?)(kv.Key, kv.Value))
            .FirstOrDefault();

    /// <summary>
    /// 槽 2 适用性的轻量事实（开窗提速 D 项）：只扫 daily 行的存在性，不构建完整 <see cref="TrendReport"/>。
    /// 口径与 <see cref="EinkSnapshotBuilder.BuildSlot2"/> 对齐：
    ///  首末数据日 = 该供电侧有行的最早 / 最晚日期；
    ///  有效日 = [max(清灰日, 首日), 末日] 内同时满足"当天有空闲基线（有非 NaN 的 TempMin）"
    ///           与"至少一档有温度均值（有可归档档位且 TempMean 非 NaN）"的日子——ΔT 只有两者都在时才不是 NaN。
    ///
    /// 【为什么不再 ReadAll】设置页首次切过去时这条扫描曾要全量解析所有日文件（实测量级 57–206 ms）。
    /// 现在先列文件名定位候选日、再按日读取：首末数据日各只需从一端找到第一个有行的日；
    /// 有效日从最新一天往回数，达到最少天数门槛即停。只关心门槛的两端语义与旧实现一致，
    /// 不足门槛时仍会逐日扫完整个窗口，返回值与旧实现相同。
    /// </summary>
    public static (DateOnly? FirstDay, DateOnly? LastDay, int EffectiveDays) ScanEinkSlot2Facts(
        AppSettings settings, bool onAc = true)
    {
        var dates = DailyStore.DailyDates();
        if (dates.Count == 0) return (null, null, 0);

        // 最后数据日：从最新文件往前找第一个该供电侧有行的日。
        DateOnly? last = null;
        for (int i = dates.Count - 1; i >= 0; i--)
            if (DayHasSide(dates[i], onAc)) { last = dates[i]; break; }
        if (last is not { } lastDay) return (null, null, 0);

        // 首日 = 该供电侧有行的最早一天。正常情况第一个日文件就有数据，只解析一个文件；
        // 只有连续多天全是另一侧供电时才需要继续向后看（不影响正确性）。
        var clean = settings.CleanDate;
        DateOnly? first = null;
        for (int i = 0; i < dates.Count; i++)
            if (DayHasSide(dates[i], onAc)) { first = dates[i]; break; }

        if (first is not { } firstDay) return (null, null, 0);
        if (clean is null) return (firstDay, lastDay, 0);

        var from = clean.Value > firstDay ? clean.Value : firstDay;
        var effective = 0;
        for (int i = dates.Count - 1; i >= 0; i--)
        {
            var day = dates[i];
            if (day < from || day > lastDay) continue;
            if (DayIsEffective(day, onAc)) effective++;
            if (effective >= EinkSnapshotBuilder.MinDaysToDraw) break;
        }

        return (firstDay, lastDay, effective);
    }

    /// <summary>该日是否有这一侧供电的行。</summary>
    private static bool DayHasSide(DateOnly day, bool onAc) =>
        DailyStore.ReadRange(day, day).Any(r => r.OnAcPower == onAc);

    /// <summary>该日是否有空闲基线（TempMin）与可归档档位的温度均值（TempMean）；两项都齐才算有效日。</summary>
    private static bool DayIsEffective(DateOnly day, bool onAc)
    {
        var hasBaseline = false;
        var hasTemp = false;
        foreach (var r in DailyStore.ReadRange(day, day))
        {
            if (r.OnAcPower != onAc) continue;

            if (!double.IsNaN(r.TempMin)) hasBaseline = true;

            // 与 TrendAnalyzer.Build 的归档条件一致：档位键解析不出下界时该行不进任何类别
            var dash = r.BandKey.IndexOf('-');
            if (dash >= 1 && int.TryParse(r.BandKey[..dash], out _) && !double.IsNaN(r.TempMean))
                hasTemp = true;

            if (hasBaseline && hasTemp) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 窗口聚合
    // ------------------------------------------------------------------

    private sealed class WindowAcc
    {
        private long _samples;
        private double _tempW, _tempN;
        private double _powerW, _powerN;
        private double _deltaW, _deltaN;
        private double _over85W, _overN;
        private double _hours, _hoursOver85;
        private int _days;

        public void Add(CatDayStat s)
        {
            _days++;
            _samples += s.Samples;
            _hours += s.HoursActive;
            _hoursOver85 += s.HoursOver85;

            if (!double.IsNaN(s.TempMean)) { _tempW += s.TempMean * s.Samples; _tempN += s.Samples; }
            if (!double.IsNaN(s.PowerMean)) { _powerW += s.PowerMean * s.Samples; _powerN += s.Samples; }
            if (!double.IsNaN(s.DeltaT)) { _deltaW += s.DeltaT * s.Samples; _deltaN += s.Samples; }
            if (!double.IsNaN(s.Over85Pct)) { _over85W += s.Over85Pct * s.Samples; _overN += s.Samples; }
        }

        public bool HasData => _samples > 0;
        public long Samples => _samples;
        public double Temp => _tempN > 0 ? _tempW / _tempN : double.NaN;
        public double Power => _powerN > 0 ? _powerW / _powerN : double.NaN;
        public double DeltaT => _deltaN > 0 ? _deltaW / _deltaN : double.NaN;
        public double Over85Pct => _overN > 0 ? _over85W / _overN : double.NaN;
        public double Hours => _hours;
        public double HoursOver85 => _hoursOver85;
        public double KPerW
        {
            get
            {
                var t = Temp; var p = Power;
                return !double.IsNaN(t) && !double.IsNaN(p) && p >= 0.5 ? t / p : double.NaN;
            }
        }
    }

    private static void BuildComparisons(
        AppSettings settings, List<DailyTrend> days, TrendReport report,
        DateOnly curFrom, DateOnly curTo,
        DateOnly prevFrom, DateOnly prevTo,
        List<CategoryComparison> output)
    {
        var catCount = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);
        var hasDc = days.Any(d => d.Dc.Count > 0);

        for (int cat = 0; cat < catCount; cat++)
        {
            // 桌面场景以 AC 为主：DC 数据单独成组不现实，因此 DC 与 AC 合并统计，
            // 但在报告中说明 DC 功耗墙不同。对比窗口内两类供电都参与。
            var cur = Accumulate(days, curFrom, curTo, cat);
            var prev = Accumulate(days, prevFrom, prevTo, cat);
            if (!cur.HasData && !prev.HasData) continue;

            string name = settings.CategoryName(cat);
            output.Add(new CategoryComparison(
                Category: name,
                CurHasData: cur.HasData, PrevHasData: prev.HasData,
                CurDeltaT: Nan(cur.DeltaT), PrevDeltaT: Nan(prev.DeltaT),
                DeltaTDiff: Nan(cur.DeltaT - prev.DeltaT),
                CurOver85Hours: Nan(cur.HoursOver85), PrevOver85Hours: Nan(prev.HoursOver85),
                CurOver85Pct: Nan(cur.Over85Pct), PrevOver85Pct: Nan(prev.Over85Pct),
                CurKPerW: Nan(cur.KPerW), PrevKPerW: Nan(prev.KPerW),
                KPerWDiff: Nan(cur.KPerW - prev.KPerW),
                CurHours: Nan(cur.Hours), PrevHours: Nan(prev.Hours),
                CurSamples: cur.HasData ? cur.Samples : null,
                PrevSamples: prev.HasData ? prev.Samples : null));
        }
    }

    /// <summary>
    /// 窗口内各类别的累计时长与样本数（AC / DC 合并，按样本加权）。
    /// 复用 <see cref="Accumulate"/>，保证与 CSV 的 <c>n</c>、柱状对比的
    /// <c>CurHours</c> / <c>CurSamples</c> 完全同源，不另造估算。
    /// </summary>
    public static List<CategoryUsage> WindowUsage(
        AppSettings settings, IReadOnlyList<DailyTrend> days, DateOnly from, DateOnly to)
    {
        var source = days as List<DailyTrend> ?? days.ToList();
        var catCount = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);
        var result = new List<CategoryUsage>(catCount);
        for (int cat = 0; cat < catCount; cat++)
        {
            var acc = Accumulate(source, from, to, cat);
            result.Add(new CategoryUsage(cat, acc.Samples, acc.Hours));
        }
        return result;
    }

    private static WindowAcc Accumulate(List<DailyTrend> days, DateOnly from, DateOnly to, int cat)
    {
        var acc = new WindowAcc();
        foreach (var d in days)
        {
            if (d.Date < from || d.Date > to) continue;
            if (d.Ac.TryGetValue(cat, out var ac)) acc.Add(ac);
            if (d.Dc.TryGetValue(cat, out var dc)) acc.Add(dc);
        }
        return acc;
    }

    private static double? Nan(double v) => double.IsNaN(v) ? null : v;

    // ------------------------------------------------------------------
    // 斜率：ΔT 每日变化（按 report.OnAc 单路，不与另一路混算）
    // ------------------------------------------------------------------

    /// <param name="from">斜率只统计该日及之后的天数；null = 全部（CLI 既有行为）。</param>
    private static void BuildSlopes(AppSettings settings, List<DailyTrend> days, TrendReport report, DateOnly? from)
    {
        var scope = from is null ? days : days.Where(d => d.Date >= from.Value).ToList();
        if (scope.Count == 0) return;

        var catCount = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);
        for (int cat = 0; cat < catCount; cat++)
        {
            var pts = new List<(double X, double Y)>();
            var first = scope[0].Date;
            foreach (var d in scope)
            {
                CatDayStat? s = d.Get(cat, report.OnAc);
                if (s is not null && !double.IsNaN(s.DeltaT))
                    pts.Add((d.Date.DayNumber - first.DayNumber, s.DeltaT));
            }
            if (pts.Count < 3) continue;

            double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
            double num = pts.Sum(p => (p.X - mx) * (p.Y - my));
            double den = pts.Sum(p => (p.X - mx) * (p.X - mx));
            if (den > 0)
                report.Slopes[cat] = new TrendSlope(num / den, pts.Count);
        }
    }

    // ------------------------------------------------------------------
    // 硬件口径校验
    // ------------------------------------------------------------------

    private static void CheckCalibers(List<DailyTrend> days, TrendReport report)
    {
        if (report.Calibers.Count == 0) return;

        var last = days[^1].Date;
        var ranges = new (string Label, DateOnly From, DateOnly To)[]
        {
            ("近 7 天 vs 前 7 天窗口", last.AddDays(-13), last),
            ("近 30 天 vs 前 30 天窗口", last.AddDays(-59), last),
        };

        foreach (var (label, from, to) in ranges)
        {
            var hashes = new List<string>();
            CaliberRecord? firstRec = null, lastRec = null;
            foreach (var d in days)
            {
                if (d.Date < from || d.Date > to) continue;
                // 口径记录只加载一次（report 已持有排好序的列表），逐日二分，不再每天读盘
                var rec = FingerprintStore.ActiveAt(report.Calibers, d.Date);
                if (rec is null) continue;
                if (!hashes.Contains(rec.Hash))
                {
                    hashes.Add(rec.Hash);
                    firstRec ??= rec;
                    lastRec = rec;
                }
            }

            if (hashes.Count > 1 && lastRec is not null && firstRec is not null)
            {
                report.CaliberWarnings.Add(
                    $"{label}内数据口径发生变化（{lastRec.FirstSeen:yyyy-MM-dd} 起）："
                    + $"主温度传感器由「{firstRec.PrimaryTempName}」变为「{lastRec.PrimaryTempName}」。"
                    + "跨口径的温差对比不可信，建议以口径变化日为分界分段查看。");
            }
        }
    }
}
