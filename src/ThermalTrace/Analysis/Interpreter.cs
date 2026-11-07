using System.Globalization;
using System.Text;
using ThermalTrace.Model;
using static ThermalTrace.Model.TrendThresholds;

namespace ThermalTrace.Analysis;

/// <summary>
/// 趋势解读规则引擎（分析层，纯函数）：把 <see cref="TrendReport"/> 里散落的数字
/// （ΔT 同期对比、每瓦温升、超 85℃ 时长、OLS 斜率、口径警告、清灰基准日）
/// 组织成五层人话解读——总结 → 证据 → 归因 → 建议 → 清灰基准，外加一份
/// 「复制数据摘要给 AI」的导出（解读口径要求）。
///
/// 【三条红线】
///  1. 纯函数：无 IO、无 UI 依赖、不碰 CSV。输入只有 settings + report（+ window），
///     输出只有 <see cref="InterpretationReport"/>。GUI 卡片、HTML 报告、CLI 三处
///     调用同一个 <see cref="Build"/>，从结构上保证"三处同源同值"。
///  2. window 必须与构造 <see cref="TrendReport"/> 时传给
///     <see cref="TrendAnalyzer.Build(AppSettings, TrendWindow?)"/> 的那个一致：
///     <see cref="TrendReport.Comparison"/> 已按该窗口聚合，本引擎只按同一段区间
///     取环境气温与日期范围。不一致 = 证据区间与对比区间不是同一段，数字必对不上。
///  3. 缺失一律「—」，绝不填 0；数据不足不给趋势判定。
/// </summary>
public static class Interpreter
{
    /// <summary>解读末尾的固定口径声明，单窗口与前后窗口两条路径共用同一句。</summary>
    private const string Disclaimer = "解读基于 ΔT（同负载归一化）与每瓦温升；样本数以 CSV 实际记录为准。";

    // 阈值与结论权重统一登记在 TrendThresholds（G12），本文件通过 using static 直接引用，
    // 与 HTML 报告、趋势分析、落盘聚合同一份数值。

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------

    /// <summary>
    /// 生成一份趋势解读。
    /// </summary>
    /// <param name="settings">应用设置（类别名/边界、清灰基准日）。</param>
    /// <param name="report">趋势报告，必须由 <see cref="TrendAnalyzer.Build(AppSettings, TrendWindow?)"/> 生成。</param>
    /// <param name="window">
    /// 对比窗口。<b>必须与构造 report 时传给 <see cref="TrendAnalyzer.Build(AppSettings, TrendWindow?)"/>
    /// 的窗口一致</b>；不传按 30 天窗口解释（与 <see cref="TrendAnalyzer"/> 不传时的默认行为对齐）。
    /// </param>
    /// <param name="hardware">
    /// 非 CPU 信号报告（GPU / SSD），由 <see cref="HardwareTrend.Build(AppSettings, TrendWindow)"/>
    /// 按<b>同一个 window</b> 生成；不传则解读里不出现 GPU 混杂因素证据与 A7 归因
    /// （旧调用方行为不变）。传了就是为了回答显卡使用时长是否上升那个问题：
    /// ΔT 涨了，是灰堵了，还是最近更常用显卡？
    /// </param>
    public static InterpretationReport Build(AppSettings settings, TrendReport report,
        TrendWindow? window = null, HardwareTrendReport? hardware = null)
    {
        var win = window ?? TrendWindow.Month;

        // 「自上次清灰」是单一周期（G8 第二轮修正）：不做本期 vs 上期的对比判定，
        // 走独立的单窗口解读路径，不再出现"前后两个窗口没有同时覆盖"这类失败文案。
        if (win.IsSinceClean) return BuildSinceClean(settings, report, win);

        var comparable = report.Comparison.Where(c => c.Comparable).ToList();

        // 环境气温：按窗口区间聚合（仅在温差 ≥ 2℃ 时入证据，它是归因辅助判据，不是健康度信号）
        var (curAmb, prevAmb) = AmbientMeans(report, win);
        var ambDiff = double.IsNaN(curAmb) || double.IsNaN(prevAmb) ? double.NaN : curAmb - prevAmb;
        var ambientRose = !double.IsNaN(ambDiff) && ambDiff >= AmbientRise;

        // 主类别（健康度判定与归因共用）：可比类别中 ΔT 差最大者。
        // 取"带符号的最大"而不是"绝对值最大"：健康度要盯的是恶化最厉害的那个类别，
        // 绝对值最大可能是个"改善最多"的类别，拿它判健康度会掩盖同时在恶化的类别。
        CategoryComparison? primary = comparable
            .Where(c => c.DeltaTDiff is not null)
            .OrderByDescending(c => c.DeltaTDiff)
            .FirstOrDefault();

        // 数据不足 → 后续规则全部不执行，总结只讲"还差几天"
        var insufficient = report.DaysWithData < MinDaysForJudgment || comparable.Count == 0;

        // 清灰层的"衰减判定"本质上也是趋势判定（"比清灰那天热了 N 度"）：
        // 诚实性硬约束优先于渲染条件——数据不足时整个分区不渲染，
        // 这与验收标准第 3 条（卡片只有"还差几天"）一致。
        // 注：口径变更窗口不受影响，清灰层照常渲染（验收标准第 4 条）。
        var cleanStat = BuildClean(settings, report, comparable);
        var clean = insufficient ? null : cleanStat;

        var health = insufficient
            ? HealthLevel.Insufficient
            : JudgeHealth(comparable, primary, clean);

        var summary = BuildSummary(report, win, primary, health, insufficient);

        var evidence = insufficient
            ? new List<EvidenceItem>()
            : BuildEvidence(settings, report, comparable, primary, curAmb, prevAmb, ambDiff, hardware);

        var attributions = BuildAttributions(settings, report, primary, comparable, ambDiff, ambientRose, insufficient, win, hardware);

        var suggestions = BuildSuggestions(report, primary, health, insufficient);

        var note = BuildConfidenceNote(report, comparable);

        // 清灰行文案：把"不渲染 / 不能评估 / 有结论"三种情况说清楚。
        // 数据不足时若直接写"未设置"，等于把"这次不展示"说成了"用户没填"——
        // 同一个人在卡片和 AI 摘要里会读到两个互相矛盾的事实。
        var cleanDigestLine = cleanStat is null
            ? settings.CleanDate is null
                ? "- 清灰基准日：未设置"
                : $"- 清灰基准日：{settings.CleanDate:yyyy-MM-dd}（暂无数据，无法评估）"
            : insufficient
                ? $"- 清灰基准日：{cleanStat.CleanDateIso}（数据不足 {MinDaysForJudgment} 天，暂不评估衰减）"
                : cleanStat.Rise is null
                    ? $"- 清灰基准日：{cleanStat.Verdict}"
                    : $"- 清灰基准日：{cleanStat.CleanDateIso}（{cleanStat.DaysSinceClean} 天前），"
                      + $"之后 ΔT {T(cleanStat.DeltaTAfterClean, "0.0")}℃ → {T(cleanStat.DeltaTNow, "0.0")}℃"
                      + $"（上升 {T(cleanStat.Rise, "0.0")}℃）";

        var digest = BuildDigest(report, win, health, evidence, attributions, cleanDigestLine, note,
            insufficient, curAmb, prevAmb);

        return new InterpretationReport(
            health, summary, evidence, attributions, suggestions, clean, note, digest);
    }

    // ------------------------------------------------------------------
    // 单窗口解读（自上次清灰）
    // ------------------------------------------------------------------

    /// <summary>
    /// 「自上次清灰」的单窗口解读：清灰基准日 → 最后记录日，只描述这一段，
    /// 不做本期 vs 上期比较。证据、归因、建议与清灰层沿用同一批模型，
    /// 因此 GUI 的分层排版与复制摘要格式都不用另起一套。
    /// </summary>
    private static InterpretationReport BuildSinceClean(
        AppSettings settings, TrendReport report, TrendWindow win)
    {
        var clean = win.CleanFrom!.Value;
        var last = report.LastDay;
        if (last is null)
            return EmptySinceClean(clean, null, "尚无任何记录");
        var lastDay = last.Value;
        if (clean > lastDay)
            return EmptySinceClean(clean, lastDay, $"晚于最后记录日 {lastDay:yyyy-MM-dd}");

        var coverageDays = lastDay.DayNumber - clean.DayNumber + 1;
        var days = report.Days.Where(d => d.Date >= clean && d.Date <= lastDay).ToList();
        var bands = AggregateBands(settings, days, report.OnAc);
        if (bands.Count == 0)
            return EmptySinceClean(clean, lastDay, "清灰后暂无可用负载数据");

        // 健康度盯"最恶化"的档位（与前后窗口路径一致）。全都在变凉时改盯降幅最大的档位，
        // 否则「空闲 0℃ 不变」会盖过「满载 −6℃」，把清灰见效误判成平稳。
        // 首末都无 ΔT 时退回样本最多的档位。
        var measured = bands.Where(b => b.FirstDate is not null && b.LastDate is not null).ToList();
        var worst = measured
            .OrderByDescending(b => b.Rise)
            .ThenByDescending(b => b.Samples)
            .FirstOrDefault();
        var best = measured
            .OrderBy(b => b.Rise)
            .ThenByDescending(b => b.Samples)
            .FirstOrDefault();
        var primary = worst is not null && worst.Rise > 0
            ? worst
            : best ?? bands.MaxBy(b => b.Samples)!;
        var over85Hours = bands.Sum(b => b.Over85Hours);
        var totalHours = bands.Sum(b => b.Hours);
        var peak = bands
            .Where(b => !double.IsNaN(b.TempMax))
            .OrderByDescending(b => b.TempMax)
            .FirstOrDefault();

        var rise = primary.FirstDate is not null && primary.LastDate is not null
            ? primary.LastDeltaT - primary.FirstDeltaT
            : double.NaN;

        var health = JudgeSingleWindowHealth(rise, over85Hours, peak?.TempMax ?? double.NaN);
        var summary = BuildSingleWindowSummary(primary, days.Count, coverageDays, health, rise,
            over85Hours, peak);
        var evidence = BuildSingleWindowEvidence(bands, clean, coverageDays, days.Count,
            over85Hours, totalHours);
        var attributions = BuildSingleWindowAttributions(primary, days.Count, rise, over85Hours, peak);
        var suggestions = BuildSingleWindowSuggestions(rise, over85Hours, peak);
        var cleanStat = BuildSingleWindowClean(primary, clean, lastDay, peak);
        var note = BuildSingleWindowNote(report, coverageDays, days.Count, primary);
        var cleanDigestLine = $"- 清灰基准：{cleanStat.Text}";
        var digest = BuildSingleWindowDigest(report, win, clean, lastDay, coverageDays, days.Count,
            health, evidence, attributions, cleanDigestLine, note);

        return new InterpretationReport(
            health, summary, evidence, attributions, suggestions, cleanStat, note, digest);
    }

    /// <summary>清灰日晚于最后记录日、或清灰后没有可用数据时的空态（G8 第二轮）。</summary>
    private static InterpretationReport EmptySinceClean(DateOnly clean, DateOnly? last, string why)
    {
        var iso = clean.ToString("yyyy-MM-dd");
        var summary = $"自上次清灰以来暂无数据（基准日 {iso}，{why}）。";
        var stat = new CleanServiceStat(iso, null, null, null, null, null, "清灰后暂无数据");
        return new InterpretationReport(
            HealthLevel.Insufficient, summary,
            new List<EvidenceItem>(), new List<AttributionItem>(), new List<string>(),
            stat, "数据 0 天。", "");
    }

    /// <summary>
    /// 单窗口内的单个负载档汇总。ΔT / 均温 / 超温占比按样本数加权；
    /// 前后数值取"清灰后第一个有该档数据的日子"与"最近一个有数据的日子"。
    /// </summary>
    private sealed class BandWindowStat
    {
        public required string Name { get; init; }
        public long Samples { get; private set; }
        public double Hours { get; private set; }
        public double Over85Hours { get; private set; }
        public double TempMax { get; private set; } = double.NaN;
        public DateOnly? PeakDate { get; private set; }
        public DateOnly? FirstDate { get; private set; }
        public double FirstDeltaT { get; private set; } = double.NaN;
        public DateOnly? LastDate { get; private set; }
        public double LastDeltaT { get; private set; } = double.NaN;

        private double _tempW, _tempN, _overW, _overN;

        public double TempMean => _tempN > 0 ? _tempW / _tempN : double.NaN;
        public double Over85Pct => _overN > 0 ? _overW / _overN : double.NaN;
        public double Rise => FirstDate is not null && LastDate is not null
            ? LastDeltaT - FirstDeltaT
            : double.NaN;

        public void Add(DateOnly date, CatDayStat s)
        {
            var n = Math.Max(1L, s.Samples);
            Samples += s.Samples;
            Hours += s.HoursActive;
            Over85Hours += s.HoursOver85;

            if (!double.IsNaN(s.DeltaT))
            {
                if (FirstDate is null)
                {
                    FirstDate = date;
                    FirstDeltaT = s.DeltaT;
                }
                LastDate = date;
                LastDeltaT = s.DeltaT;
            }

            if (!double.IsNaN(s.TempMean)) { _tempW += s.TempMean * n; _tempN += n; }
            if (!double.IsNaN(s.Over85Pct)) { _overW += s.Over85Pct * n; _overN += n; }

            if (!double.IsNaN(s.TempMax) && (double.IsNaN(TempMax) || s.TempMax > TempMax))
            {
                TempMax = s.TempMax;
                PeakDate = date;
            }
        }
    }

    /// <summary>按展示类别汇总窗口内的每日统计，只取报告口径那一侧（R7）。</summary>
    private static List<BandWindowStat> AggregateBands(
        AppSettings settings, List<DailyTrend> days, bool onAc)
    {
        var catCount = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);
        var result = new List<BandWindowStat>();

        for (int cat = 0; cat < catCount; cat++)
        {
            var band = new BandWindowStat { Name = settings.CategoryName(cat) };
            bool any = false;
            foreach (var day in days)
            {
                var stat = day.Get(cat, onAc);
                if (stat is null) continue;
                any = true;
                band.Add(day.Date, stat);
            }
            if (any) result.Add(band);
        }

        return result;
    }

    /// <summary>
    /// 单窗口健康度：ΔT 首末变化、超 85℃ 累计时长、区间峰值温度各出一个信号，
    /// 取最差的一档；没有信号就是平稳。样本少不影响判定，只在总结与可信度里标注弱结论。
    /// </summary>
    private static HealthLevel JudgeSingleWindowHealth(
        double rise, double over85Hours, double peakTemp)
    {
        var signals = new List<HealthLevel>();

        if (!double.IsNaN(rise))
        {
            if (rise >= DeltaTWarn) signals.Add(HealthLevel.Warning);
            else if (rise >= DeltaTSignificant) signals.Add(HealthLevel.Watch);
            else if (rise <= -DeltaTSignificant) signals.Add(HealthLevel.Good);
        }

        if (over85Hours >= Over85WatchHours) signals.Add(HealthLevel.Watch);

        if (!double.IsNaN(peakTemp))
        {
            if (peakTemp >= OverTempCriticalC) signals.Add(HealthLevel.Warning);
            else if (peakTemp >= OverTempWarnC) signals.Add(HealthLevel.Watch);
        }

        if (signals.Count == 0) return HealthLevel.Stable;

        // Worst 以 Stable 为下限，单独的 Good 会被吞掉；单窗口这里要能明确报"变凉"。
        var worst = Worst(signals);
        return worst == HealthLevel.Stable && signals.Contains(HealthLevel.Good)
            ? HealthLevel.Good
            : worst;
    }

    private static string BuildSingleWindowSummary(
        BandWindowStat primary, int daysWithData, int coverageDays,
        HealthLevel health, double rise, double over85Hours, BandWindowStat? peak)
    {
        var weak = daysWithData < MinDaysForWeakConclusion
            ? $"样本仅 {daysWithData} 天，结论偏弱，仅供参考。"
            : "";
        var span = $"{daysWithData} 天（覆盖 {coverageDays} 天）";
        var first = T(primary.FirstDate is null ? (double?)null : primary.FirstDeltaT, "0.0");
        var last = T(primary.LastDate is null ? (double?)null : primary.LastDeltaT, "0.0");
        var change = double.IsNaN(rise) ? "—" : Signed(rise, "0.0", "℃");

        if (health is HealthLevel.Warning or HealthLevel.Watch)
        {
            if (rise >= DeltaTSignificant)
                return weak + $"自上次清灰以来 {span}，「{primary.Name}」ΔT 由首日 {first}℃ 升至最近 {last}℃"
                    + $"（{change}），需留意散热回升。";
            if (over85Hours >= Over85WatchHours)
                return weak + $"自上次清灰以来 {span}，超 85℃ 累计 {over85Hours:0.0}h，"
                    + $"峰值 {T(peak?.TempMax, "0.0")}℃{PeakWhen(peak)}，散热余量偏紧。";
            return weak + $"自上次清灰以来 {span}，峰值 {T(peak?.TempMax, "0.0")}℃{PeakWhen(peak)}，"
                + "出现值得留意的信号。";
        }

        if (health == HealthLevel.Good)
            return weak + $"自上次清灰以来 {span}，「{primary.Name}」ΔT 由首日 {first}℃ 降至最近 {last}℃"
                + $"（{change}），清灰后散热改善。";

        if (double.IsNaN(rise))
            return weak + $"自上次清灰以来 {span}，主类别 ΔT 数据不足，暂按现有记录给出整体判断。";

        return weak + $"自上次清灰以来 {span}，「{primary.Name}」ΔT 首日 {first}℃、最近 {last}℃"
            + $"（{change}），整体平稳。";
    }

    private static string PeakWhen(BandWindowStat? peak) =>
        peak?.PeakDate is { } date ? $"（{date:yyyy-MM-dd}）" : "";

    private static List<EvidenceItem> BuildSingleWindowEvidence(
        List<BandWindowStat> bands, DateOnly clean, int coverageDays, int daysWithData,
        double over85Hours, double totalHours)
    {
        var items = new List<EvidenceItem>
        {
            new("", "清灰", "",
                $"{clean:yyyy-MM-dd} 起，覆盖 {coverageDays} 天（有数据 {daysWithData} 天）")
        };

        foreach (var b in bands.OrderByDescending(b => b.Samples))
        {
            var first = b.FirstDate is null ? "—" : $"{b.FirstDeltaT:0.0}℃";
            var last = b.LastDate is null ? "—" : $"{b.LastDeltaT:0.0}℃";
            var change = b.FirstDate is null || b.LastDate is null
                ? "—"
                : Signed(b.LastDeltaT - b.FirstDeltaT, "0.0", "℃");
            var hours = b.Hours > 0 ? $"{b.Hours:0.0}h" : "—";
            items.Add(new EvidenceItem(b.Name, "档位", "",
                $"ΔT {first} → {last}（{change}）；均温 {T(b.TempMean, "0.0")}℃；"
                + $"时长 {hours}；超 85℃ {T(b.Over85Pct, "0.0")}%"));
        }

        if (over85Hours > 0)
        {
            var share = totalHours > 0 ? over85Hours / totalHours : double.NaN;
            var pct = double.IsNaN(share) ? "—" : $"{share:0.0%}";
            items.Add(new EvidenceItem("", "超85时长", "",
                $"{over85Hours:0.0}h（窗口共 {totalHours:0.0}h，占 {pct}）"));
        }

        return items;
    }

    private static List<AttributionItem> BuildSingleWindowAttributions(
        BandWindowStat primary, int daysWithData, double rise,
        double over85Hours, BandWindowStat? peak)
    {
        var items = new List<AttributionItem>();
        var pair = primary.FirstDate is not null && primary.LastDate is not null
            ? $"「{primary.Name}」ΔT 首日 {primary.FirstDeltaT:0.0}℃ → 最近 {primary.LastDeltaT:0.0}℃"
              + $"（{Signed(primary.LastDeltaT - primary.FirstDeltaT, "0.0", "℃")}）"
            : null;

        if (pair is not null && rise >= CleanRiseSlight)
        {
            items.Add(new AttributionItem(
                "清灰后仍有升温：灰尘重新堆积、摆放遮挡或硅脂接触退化（现有数据无法区分）",
                pair,
                daysWithData >= MinDaysForWeakConclusion ? "中" : "低",
                "先检查进/排风口遮挡；持续升温再考虑复查清灰与硅脂"));
        }
        else if (pair is not null && rise <= -CleanRiseSlight)
        {
            items.Add(new AttributionItem(
                "清灰见效：散热路径恢复（灰尘与积热减少）",
                pair,
                daysWithData >= MinDaysForWeakConclusion ? "中" : "低",
                "继续记录；若 ΔT 维持低位即可确认"));
        }

        var peakTemp = peak?.TempMax ?? double.NaN;
        if (peakTemp >= OverTempWarnC || over85Hours >= Over85WatchHours)
        {
            items.Add(new AttributionItem(
                "高负载集中或室温偏高导致短时高温（不等于灰尘问题）",
                $"峰值 {T(peak?.TempMax, "0.0")}℃{PeakWhen(peak)}，超 85℃ 累计 {over85Hours:0.0}h",
                "中",
                "对照室温与重载时长；改善通风或适度限频后看是否仍触 85℃"));
        }

        return items
            .OrderByDescending(a => a.Confidence switch { "高" => 3, "中" => 2, "低" => 1, _ => 0 })
            .ToList();
    }

    private static List<string> BuildSingleWindowSuggestions(
        double rise, double over85Hours, BandWindowStat? peak)
    {
        var items = new List<string>();

        if (rise >= CleanRiseSlight)
            items.Add("先检查进/排风口是否被遮挡并垫高机身（零成本）；无改善再考虑复查清灰与硅脂。");
        if (over85Hours >= Over85WatchHours || (peak?.TempMax ?? double.NaN) >= OverTempCriticalC)
            items.Add("重负载频繁触 85℃，可清理排风口或适度限频。");
        if (rise <= -CleanRiseSlight)
            items.Add("清灰后散热已改善，保持当前摆放与记录节奏即可。");
        if (items.Count == 0)
            items.Add("保持记录节奏；每次清灰后在设置页更新基准日，便于回看这一段的 ΔT 与超温。");

        items.Add(Disclaimer);
        if (items.Count > MaxSuggestions)
            items = items.Take(MaxSuggestions - 1).Append(Disclaimer).ToList();
        return items;
    }

    /// <summary>清灰基准层：日期、距今天数、主类别 ΔT 首末变化，以及区间最高温与最热的一天。</summary>
    private static CleanServiceStat BuildSingleWindowClean(
        BandWindowStat primary, DateOnly clean, DateOnly last, BandWindowStat? peak)
    {
        var iso = clean.ToString("yyyy-MM-dd");
        var daysSince = last.DayNumber - clean.DayNumber;
        var first = primary.FirstDate is null ? (double?)null : primary.FirstDeltaT;
        var now = primary.LastDate is null ? (double?)null : primary.LastDeltaT;
        var rise = first is not null && now is not null ? now - first : null;
        var perMonth = rise is { } r && daysSince >= CleanWatchDays
            ? r / daysSince * DaysPerMonth
            : (double?)null;
        var verdict = rise is { } rr
            ? rr <= -CleanRiseSlight ? "散热改善"
              : rr >= CleanRiseWatch ? "衰减明显"
              : rr >= CleanRiseSlight ? "轻微衰减"
              : "基本无衰减"
            : "数据不足，无法评估 ΔT 变化";
        var riseText = rise is { } rt
            ? rt >= 0 ? $"上升 {rt:0.0}℃" : $"下降 {-rt:0.0}℃"
            : null;

        return new CleanServiceStat(
            iso, daysSince, first, now, rise, perMonth, verdict,
            peak is null || double.IsNaN(peak.TempMax) ? null : peak.TempMax,
            peak?.PeakDate?.ToString("yyyy-MM-dd"),
            peak?.Name,
            riseText);
    }

    private static string BuildSingleWindowNote(
        TrendReport report, int coverageDays, int daysWithData, BandWindowStat primary)
    {
        var parts = new List<string>
        {
            $"窗口 {coverageDays} 天（有数据 {daysWithData} 天）",
            daysWithData < MinDaysForWeakConclusion
                ? "样本不足 7 天，结论偏弱，仅供参考"
                : $"主类别「{primary.Name}」样本 {primary.Samples}",
            report.CaliberWarnings.Count > 0
                ? "口径：检测到变更记录，跨口径数值需谨慎"
                : "口径：无变更"
        };
        return string.Join("；", parts) + "。";
    }

    private static string BuildSingleWindowDigest(
        TrendReport report, TrendWindow win,
        DateOnly clean, DateOnly last, int coverageDays, int daysWithData,
        HealthLevel health,
        List<EvidenceItem> evidence,
        List<AttributionItem> attributions,
        string cleanDigestLine,
        string note)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ThermalTrace 趋势数据摘要（供 AI 解读）");
        sb.AppendLine($"- 数据范围：{RangeText(report)}（{report.DaysWithData} 个有效天数）");
        sb.AppendLine($"- 解读窗口：{win.Label}（{clean:yyyy-MM-dd} ~ {last:yyyy-MM-dd}，"
            + $"覆盖 {coverageDays} 天，有数据 {daysWithData} 天）");
        sb.AppendLine("- 口径说明：ΔT = 该负载类别均温 − 当天空闲档最低温（已消室温影响）；K/W = 均温 ÷ 平均功耗");
        sb.AppendLine($"- 健康度：{health.Text()}");
        sb.AppendLine("- 证据：");
        foreach (var e in evidence) sb.AppendLine($"  - {e.Text}");
        sb.AppendLine("- 归因（含验证方法）：");
        if (attributions.Count == 0) sb.AppendLine("  - 无明显异常模式");
        else foreach (var a in attributions) sb.AppendLine($"  - {a.Text}");
        sb.AppendLine(cleanDigestLine);
        sb.AppendLine($"- 口径状态：{(report.CaliberWarnings.Count > 0 ? "有变更（跨口径对比不可信）" : "无变更")}");
        sb.AppendLine($"- 数据可信度：{note}");
        sb.AppendLine("请基于以上数据解读这段时间的散热状况与可能原因，并给出行动建议。");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    // 健康度：取所有信号中最差的一档，没有"加分抵消"
    // ------------------------------------------------------------------

    private static HealthLevel JudgeHealth(
        List<CategoryComparison> comparable,
        CategoryComparison? primary,
        CleanServiceStat? clean)
    {
        var signals = new List<HealthLevel>();

        // 2. 主类别 ΔT 差
        HealthLevel? dtLevel = null;
        if (primary is not null)
        {
            dtLevel = primary.DeltaTDiff!.Value switch
            {
                >= DeltaTWarn => HealthLevel.Warning,
                >= DeltaTSignificant => HealthLevel.Watch,
                <= -DeltaTSignificant => HealthLevel.Good,
                _ => HealthLevel.Stable
            };
            signals.Add(dtLevel.Value);
        }

        // 3. K/W 差（与 ΔT 同一个主类别）：恶化时升一档，改善时单独可为 Good。
        //    最终仍取各信号最差者——K/W 改善不能抵消 ΔT 恶化。
        if (primary is not null && primary.KPerWDiff is { } kw)
        {
            var dt = primary.DeltaTDiff!.Value;
            if (kw >= KPerWEscalate && dt >= 0)
                signals.Add(Escalate(dtLevel ?? HealthLevel.Stable));
            else if (kw <= -KPerWEscalate && dt <= 0)
                signals.Add(HealthLevel.Good);
        }

        // 4. 超 85℃ 时长差（取上升最多的可比类别）
        var over = comparable
            .Where(c => c.CurOver85Hours is not null && c.PrevOver85Hours is not null)
            .MaxBy(c => c.CurOver85Hours!.Value - c.PrevOver85Hours!.Value);
        if (over is not null && over.CurOver85Hours!.Value - over.PrevOver85Hours!.Value >= Over85WatchHours)
            signals.Add(HealthLevel.Watch);

        // 5. 清灰衰减：距清灰上升 ≥ 1.5℃ 且 > 30 天
        if (clean is { Rise: { } rise, DaysSinceClean: { } days } && rise >= CleanRiseWatch && days > CleanWatchDays)
            signals.Add(HealthLevel.Watch);

        return Worst(signals);
    }

    /// <summary>升一档。Good 不会走到这里（ΔT 差 ≥ 0 与 Good 互斥）；Warning 已封顶。</summary>
    private static HealthLevel Escalate(HealthLevel level) => level switch
    {
        HealthLevel.Stable => HealthLevel.Watch,
        HealthLevel.Watch => HealthLevel.Warning,
        _ => level
    };

    /// <summary>取最差的一档。手写而不依赖 LINQ：枚举的 Max 重载在框架间有差异，且语义要显式。</summary>
    private static HealthLevel Worst(IEnumerable<HealthLevel> levels)
    {
        var worst = HealthLevel.Stable;
        foreach (var l in levels)
            if ((int)l > (int)worst) worst = l;
        return worst;
    }

    // ------------------------------------------------------------------
    // 总结（一句话）
    // ------------------------------------------------------------------

    private static string BuildSummary(
        TrendReport report, TrendWindow win,
        CategoryComparison? primary, HealthLevel health, bool insufficient)
    {
        if (insufficient)
        {
            return report.DaysWithData < MinDaysForJudgment
                ? $"目前只有 {report.DaysWithData} 天数据，还差 {MinDaysForJudgment - report.DaysWithData} 天才够解读；"
                  + "继续记录即可，暂不给趋势判定。"
                : $"已有 {report.DaysWithData} 天数据，但同一负载类别还没有同时覆盖前后两个窗口，暂无法对比；继续记录即可。";
        }

        var dt = primary?.DeltaTDiff;
        var cat = primary?.Category ?? "主要类别";

        return health switch
        {
            HealthLevel.Warning or HealthLevel.Watch when dt is >= DeltaTSignificant
                => $"散热状态{health.Text()}：「{cat}」ΔT 上升 {dt:0.0}℃（{win.Label}）。",
            HealthLevel.Warning or HealthLevel.Watch
                => $"散热状态{health.Text()}：{win.Label}内出现值得留意的信号，详见下方证据。",
            HealthLevel.Good when dt is <= -DeltaTSignificant
                => $"散热状态良好：「{cat}」ΔT 下降 {-dt:0.0}℃（{win.Label}）。",
            HealthLevel.Good => "散热状态良好。",
            _ => "散热状态平稳，各指标变化均在显著阈值以内。"
        };
    }

    // ------------------------------------------------------------------
    // 证据链
    // ------------------------------------------------------------------

    private static List<EvidenceItem> BuildEvidence(
        AppSettings settings, TrendReport report,
        List<CategoryComparison> comparable, CategoryComparison? primary,
        double curAmb, double prevAmb, double ambDiff,
        HardwareTrendReport? hardware)
    {
        var items = new List<EvidenceItem>();

        // ΔT 对比：可比且 |差| ≥ 0.5℃，按 |差| 降序取前 2。
        // 【为什么是 2 而不是 3】证据上限 4 条、指标有 5 种。取 3 条 ΔT 会把
        // K/W、超 85、斜率全挤出去——而那几条往往才是"该做什么"的依据
        // （超 85 时长对应"清理排风口/适度限频"那条建议）。2 条 ΔT 足以说清
        // "哪些类别在变热"，剩下的位置留给不同量纲的信号。
        foreach (var c in comparable
            .Where(c => c.DeltaTDiff is not null && Math.Abs(c.DeltaTDiff.Value) >= DeltaTSignificant)
            .OrderByDescending(c => Math.Abs(c.DeltaTDiff!.Value))
            .Take(2))
        {
            items.Add(new EvidenceItem(
                c.Category, "ΔT",
                Signed(c.DeltaTDiff!.Value, "0.0", "℃"),
                $"{T(c.CurDeltaT, "0.0")}℃ ← {T(c.PrevDeltaT, "0.0")}℃"));
        }

        // GPU 使用时长：ΔT 显著上升时的混杂因素证据。
        // 【为什么排在 K/W 前面】证据上限 4 条，而 2 条 ΔT 常常就把位置占掉一半。
        // 这条要回答的是"升温是不是显卡带来的"——它不在场，读者就无法在
        // 「灰堵了」和「更常用显卡」之间做判断，归因区只能二选一地猜。
        // 【为什么只在 ΔT 上升时出】没有需要解释的升温时，显卡时长与结论无关，
        // 列出来只是噪声。时长没涨也出——那是在帮读者排除这条干扰。
        // 【口径护栏】2026-10-16 起时长 = 负载>0 时长；跨部署日期两侧口径不同，
        // 差值主要是"开机时长 vs 真实用量"的落差，不是使用习惯——两侧都有负载数据才出。
        if (hardware is not null
            && primary is { DeltaTDiff: { } pdt } && pdt >= DeltaTSignificant
            && hardware.GpuDaysPrevCount > 0 && hardware.GpuDays.Count > 0
            && hardware.GpuLoadDaysPrevCount > 0 && hardware.GpuLoadDaysCount > 0)
        {
            items.Add(new EvidenceItem("", "GPU使用",
                Signed(hardware.GpuHours - hardware.GpuHoursPrev, "0.0", "h"),
                $"{hardware.GpuHoursPrev:0.0}h → {hardware.GpuHours:0.0}h"));
        }

        // K/W：优先取主类别的每瓦温升（与健康度信号、归因假设同一个类别），
        // 主类别不显著时才退化为"|差| 最大的一条"。
        // 理由：健康度说「重度」、归因也说「重度」，证据却列「中等」的 K/W，
        // 读者会以为是三台不同的机器。
        var significantK = comparable
            .Where(c => c.KPerWDiff is not null && Math.Abs(c.KPerWDiff.Value) >= KPerWSignificant)
            .OrderByDescending(c => Math.Abs(c.KPerWDiff!.Value))
            .ToList();
        var k = primary is { KPerWDiff: { } pkw } && Math.Abs(pkw) >= KPerWSignificant
            ? primary
            : significantK.FirstOrDefault();
        if (k is not null)
        {
            items.Add(new EvidenceItem(
                k.Category, "K/W",
                Signed(k.KPerWDiff!.Value, "0.00"),
                $"{T(k.CurKPerW, "0.00")} → {T(k.PrevKPerW, "0.00")}"));
        }

        // 超 85℃ 累计：可比类别求和；任一侧完全无数据就不出这条——
        // "没采到"不能渲染成"没超温"。
        var cur85Days = comparable.Count(c => c.CurOver85Hours is not null);
        var prev85Days = comparable.Count(c => c.PrevOver85Hours is not null);
        if (cur85Days > 0 && prev85Days > 0)
        {
            var cur85 = comparable.Sum(c => c.CurOver85Hours ?? 0);
            var prev85 = comparable.Sum(c => c.PrevOver85Hours ?? 0);
            if (Math.Abs(cur85 - prev85) >= Over85Significant)
                items.Add(new EvidenceItem("", "超85时长", Signed(cur85 - prev85, "0.0", "h"),
                    $"{cur85:0.0}h ← {prev85:0.0}h"));
        }

        // 斜率：复用 TrendAnalyzer.PrimarySlope 的选取规则，不另写一套
        // （否则页脚、HTML、卡片会各挑一条不同的斜率，三个结论互相打脸）。
        // 这里再判一次 |PerDay| ≥ 0.02 与点数 ≥ 3，是为了让阈值在用到它的地方可见，
        // 而不是依赖 PrimarySlope 那头的隐式保证。
        var slope = TrendAnalyzer.PrimarySlope(report);
        if (slope is not null
            && slope.Value.Slope.HasTrend
            && Math.Abs(slope.Value.Slope.PerDay) >= SlopeSignificant)
        {
            var (cat, s) = slope.Value;
            items.Add(new EvidenceItem(
                settings.CategoryName(cat), "斜率",
                Signed(s.PerDay, "0.00", "℃/天"),
                $"{s.Points} 个日点"));
        }

        // 环境气温：仅本期比上期高 ≥ 2℃ 时入证据（归因辅助判据，不是健康度信号）
        if (!double.IsNaN(ambDiff) && ambDiff >= AmbientRise)
        {
            items.Add(new EvidenceItem("", "环境气温", Signed(ambDiff, "0.0", "℃"),
                $"{T(curAmb, "0.0")}℃ ← {T(prevAmb, "0.0")}℃"));
        }

        // 上限 4 条；优先级即上面的加入顺序（ΔT > K/W > 超85 > 斜率 > 环境），
        // ΔT 内部按 |差| 降序。
        return items.Take(MaxEvidence).ToList();
    }

    // ------------------------------------------------------------------
    // 归因矩阵：每条 = 假设 + 证据 + 置信度 + 验证方法
    // ------------------------------------------------------------------

    private static List<AttributionItem> BuildAttributions(
        AppSettings settings, TrendReport report,
        CategoryComparison? primary,
        List<CategoryComparison> comparable,
        double ambDiff, bool ambientRose,
        bool insufficient, TrendWindow win,
        HardwareTrendReport? hardware)
    {
        // A6：数据不足 → 归因区整体不渲染（空数组）
        if (insufficient) return new List<AttributionItem>();

        // A5：口径变更 → 显式拒判，其余归因一律不输出
        if (report.CaliberWarnings.Count > 0)
        {
            return new List<AttributionItem>
            {
                // 不把警告原文塞进 Support：那条原文已经完整出现在结论区前面的口径警告块里，
                // 这里再贴一遍只会让"验证方法"读起来像复读。
                new("对比窗口跨了口径，不做归因", "", "—", "以口径变化日为界分段查看")
            };
        }

        var items = new List<AttributionItem>();

        // 归因的 ΔT / K/W 信号都取主类别（ΔT 差最大者），与健康度判定同一类别：
        // 不能判定说「重度」、归因说「满载」。
        if (primary is { DeltaTDiff: { } dt, KPerWDiff: { } kw })
        {
            var kwRose = kw >= KPerWSignificant;
            var dtRose = dt >= DeltaTSignificant;

            // A1：K/W↑ 且 ΔT↑ 且环境气温未升
            if (kwRose && dtRose && !ambientRose)
            {
                var days = WindowDays(win, report);
                items.Add(new AttributionItem(
                    "灰尘堆积 / 硅脂老化（散热路径退化）",
                    $"「{primary.Category}」每瓦温升 {Signed(kw, "0.00")}，ΔT {Signed(dt, "0.0", "℃")}",
                    days >= MinDaysForJudgment ? "中" : "低",
                    "清灰换硅脂后 K/W 回落即可确认"));
            }

            // A2：K/W↑ 且环境气温平（与 A1 用现有数据无法区分）
            if (kwRose && !ambientRose)
            {
                items.Add(new AttributionItem(
                    "摆放 / 进风口遮挡（与灰尘因素用现有数据无法区分）",
                    $"「{primary.Category}」每瓦温升 {Signed(kw, "0.00")}",
                    "中",
                    "先垫高、清遮挡（零成本），K/W 回落则排除灰尘因素"));
            }
        }

        // A3：环境气温本期比上期 ≥ 2℃
        if (ambientRose)
        {
            items.Add(new AttributionItem(
                "室温贡献（ΔT 已消基线，影响有限）",
                $"环境气温 {Signed(ambDiff, "0.0", "℃")}",
                "低",
                "看 K/W：气温驱动时 K/W 不同步升"));
        }

        // A4：重载类别时长占比明显上升（使用强度 ≠ 散热退化）
        var (sharePrev, shareCur) = HeavyShare(settings, comparable);
        if (!double.IsNaN(sharePrev) && !double.IsNaN(shareCur) && shareCur - sharePrev >= HeavyShareRise)
        {
            items.Add(new AttributionItem(
                "使用强度变化——发热总量增加 ≠ 散热退化",
                $"重载类别时长占比 {sharePrev:0.0%} → {shareCur:0.0%}",
                "中",
                "分类别 ΔT 是否同步上升；不同步即强度因素"));
        }

        // A7：GPU 使用时长上升（显卡与 CPU 共用散热模组，
        // GPU 发热会抬高同等 CPU 负载下的温度，是 ΔT 上升的混杂因素）。
        // 与 A1/A2 并列而非取代：现有数据无法区分"灰堵了"和"更常用显卡"，
        // 所以这里只做"不能排除"的提示 + 可操作的验证方法，绝不下结论。
        // 【口径护栏】2026-10-16 起使用时长改为"负载>0 时长"。跨部署日期的窗口里，
        // 上期还是旧口径（≈开机时长），"时长涨了"是口径差不是习惯差——两侧都有负载数据才比。
        if (hardware is not null
            && primary is { DeltaTDiff: { } dt7 } && dt7 >= DeltaTSignificant
            && hardware.GpuDaysPrevCount > 0 && hardware.GpuDays.Count > 0
            && hardware.GpuLoadDaysPrevCount > 0 && hardware.GpuLoadDaysCount > 0
            && hardware.GpuHours - hardware.GpuHoursPrev >= GpuHoursRise)
        {
            items.Add(new AttributionItem(
                "显卡使用增多（与 CPU 共用散热模组，不能排除）",
                $"GPU 使用时长 {hardware.GpuHoursPrev:0.0}h → {hardware.GpuHours:0.0}h",
                "低",
                "挑 GPU 时长相近的两段日期再比 ΔT；或看少用显卡时段的 ΔT"));
        }

        // 同时命中多条全部列出，按置信度降序（OrderByDescending 稳定，同档保持加入顺序）
        return items
            .OrderByDescending(a => a.Confidence switch { "高" => 3, "中" => 2, "低" => 1, _ => 0 })
            .ToList();
    }

    /// <summary>
    /// 重载类别时长占比（上期 → 本期）。
    ///
    /// 【重载类别怎么定】阶梯最上的三档（默认 5 档即 中等 / 重度 / 满载，
    /// 与 HtmlTrendReport 结论区的优先类别一致）。按下标映射成名字后再匹配，
    /// 与 HtmlTrendReport 结论区的优先类别一致）。按下标映射成名字后再匹配，
    /// 用户改类别名或桶边界后依然跟着变，不像写死中文那样改个名就失效。
    ///
    /// 【为什么只用可比类别】占比要的是"两期的构成对比"；一侧没数据的类别
    /// 会把占比算歪（上期只落了空闲档 → 重载占比 0 → 看起来暴涨）。
    /// 任一侧完全没有时长数据时返回 NaN，调用方据此跳过 A4。
    /// </summary>
    private static (double Prev, double Cur) HeavyShare(
        AppSettings settings, List<CategoryComparison> comparable)
    {
        if (comparable.Count == 0) return (double.NaN, double.NaN);

        var catCount = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);
        var heavy = new HashSet<string>();
        for (int i = Math.Max(0, catCount - 3); i < catCount; i++)
            heavy.Add(settings.CategoryName(i));

        double heavyPrev = 0, totalPrev = 0, heavyCur = 0, totalCur = 0;
        foreach (var c in comparable)
        {
            var isHeavy = heavy.Contains(c.Category);
            if (c.PrevHours is { } ph) { totalPrev += ph; if (isHeavy) heavyPrev += ph; }
            if (c.CurHours is { } ch) { totalCur += ch; if (isHeavy) heavyCur += ch; }
        }

        return (totalPrev > 0 ? heavyPrev / totalPrev : double.NaN,
                totalCur > 0 ? heavyCur / totalCur : double.NaN);
    }

    // ------------------------------------------------------------------
    // 建议映射
    // ------------------------------------------------------------------

    private static List<string> BuildSuggestions(
        TrendReport report,
        CategoryComparison? primary,
        HealthLevel health, bool insufficient)
    {
        var items = new List<string>();

        if (insufficient)
        {
            items.Add(report.DaysWithData < MinDaysForJudgment
                ? $"继续记录，窗口满 {MinDaysForJudgment} 天后再看解读（当前 {report.DaysWithData} 天）。"
                : $"继续记录：同一负载类别需要前后两个窗口都有数据才能对比（当前 {report.DaysWithData} 天）。");
        }
        else
        {
            if (report.CaliberWarnings.Count > 0)
                items.Add("跨口径数据请以口径变化日为界分段查看，暂不建议归因。");

            var kwRose = primary is { KPerWDiff: { } kw } && kw >= KPerWSignificant;
            var dtRose = primary is { DeltaTDiff: { } dt } && dt >= DeltaTSignificant;

            if (health is HealthLevel.Watch or HealthLevel.Warning && kwRose)
            {
                // ① 零成本先做，② 有成本后做——表里的编号就是成本顺序
                items.Add("先查摆放：进/排风口勿被桌面床铺遮挡，垫高机身（零成本）。");
                items.Add("摆放无异常则安排清灰 + 换硅脂；完成后在设置页更新清灰基准日，两周后回看每瓦温升是否回落。");
            }
            else if (dtRose && !kwRose)
            {
                // "K/W 平"按"未同步上升"执行：持平与下降都意味着散热效率没恶化，都不必急着清灰
                items.Add("同功耗温升增加但每瓦温升未同步——优先排查室温与摆放，不必急着清灰。");
            }

            if (Over85Rose(report))
                items.Add("重负载频繁触 85℃，可清理排风口或适度限频。");

            if (health is HealthLevel.Good or HealthLevel.Stable)
                items.Add($"散热状态{health.Text()}，保持记录节奏即可。");
        }

        // 总是最后一条（口径声明，单窗口路径共用同一句）
        items.Add(Disclaimer);

        // 上限 4 条：超长时保留前 3 条 + 恒末条的口径声明
        if (items.Count > MaxSuggestions)
            items = items.Take(MaxSuggestions - 1).Append(Disclaimer).ToList();

        return items;
    }

    /// <summary>超 85℃ 时长是否显著上升（取上升最多的可比类别，≥ 0.1h）。</summary>
    private static bool Over85Rose(TrendReport report)
    {
        var over = report.Comparison
            .Where(c => c.Comparable && c.CurOver85Hours is not null && c.PrevOver85Hours is not null)
            .MaxBy(c => c.CurOver85Hours!.Value - c.PrevOver85Hours!.Value);
        return over is not null
            && over.CurOver85Hours!.Value - over.PrevOver85Hours!.Value >= Over85Significant;
    }

    // ------------------------------------------------------------------
    // 可信度说明
    // ------------------------------------------------------------------

    private static string BuildConfidenceNote(TrendReport report, List<CategoryComparison> comparable)
    {
        var parts = new List<string>
        {
            $"数据 {report.DaysWithData} 天"
            + (report.DaysWithData < MinDaysForJudgment ? "（不足 14 天）" : ""),
            $"两窗口可比类别 {comparable.Count} 个",
            report.CaliberWarnings.Count > 0
                ? "口径：对比窗口内有变更，跨口径不可比"
                : "口径：无变更"
        };

        var approx = report.Days.Count(d => d.IdleApproximated);
        if (approx > 0) parts.Add($"空闲基线近似 {approx} 天（ΔT 口径略宽）");

        return string.Join("；", parts) + "。";
    }

    // ------------------------------------------------------------------
    // 清灰基准层
    // ------------------------------------------------------------------

    private static CleanServiceStat? BuildClean(
        AppSettings settings, TrendReport report, List<CategoryComparison> comparable)
    {
        var clean = settings.CleanDate;

        // 未设基准日 → 分区不渲染；一整天数据都没有 → 同样无从评估
        if (clean is null || report.LastDay is not { } last) return null;

        var daysSince = last.DayNumber - clean.Value.DayNumber;
        var iso = clean.Value.ToString("yyyy-MM-dd");

        if (daysSince < 0)
            return new CleanServiceStat(iso, null, null, null, null, null, "清灰基准日晚于最新数据，暂不评估");

        // 主类别 = 两窗口可比类别中本期样本数最多者（最稳的参照）；
        // 无比可比类别时取全期样本最多者。
        var primaryCat = PrimaryCategory(settings, report, comparable);
        if (primaryCat < 0)
            return new CleanServiceStat(iso, daysSince, null, null, null, null, "基准日前后无数据，无法评估");

        // ΔT 与趋势页折线 / 斜率同源，只取报告口径那一侧（R7）。
        // 基准日（含）之后第一个有该类别数据的日
        double? afterClean = null;
        foreach (var d in report.Days)
        {
            if (d.Date < clean.Value) continue;
            var v = DeltaTOf(d, primaryCat, report.OnAc);
            if (!double.IsNaN(v)) { afterClean = v; break; }
        }

        // 最后一天缺该类别时回退到最近一个有数据日：宁可给一个真实的近期值，
        // 也不把"今天恰好没采到"渲染成"没有近期数据"。
        double? now = null;
        for (int i = report.Days.Count - 1; i >= 0; i--)
        {
            var v = DeltaTOf(report.Days[i], primaryCat, report.OnAc);
            if (!double.IsNaN(v)) { now = v; break; }
        }

        if (afterClean is null || now is null)
            return new CleanServiceStat(iso, daysSince, null, null, null, null, "基准日前后无数据，无法评估");

        var rise = now.Value - afterClean.Value;

        // 不足 30 天不外推月均：一期都不到，"每月"是无据之名
        var perMonth = daysSince >= CleanWatchDays ? rise / daysSince * DaysPerMonth : (double?)null;

        var verdict = rise >= CleanRiseWatch ? "衰减明显"
            : rise >= CleanRiseSlight ? "轻微衰减"
            : "基本无衰减";

        return new CleanServiceStat(iso, daysSince, afterClean, now, rise, perMonth, verdict);
    }

    /// <summary>清灰层主类别的下标；找不到返回 -1。</summary>
    private static int PrimaryCategory(
        AppSettings settings, TrendReport report, List<CategoryComparison> comparable)
    {
        var catCount = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);

        if (comparable.Count > 0)
        {
            var bestName = comparable.MaxBy(c => c.CurSamples ?? 0)!.Category;
            for (int i = 0; i < catCount; i++)
                if (settings.CategoryName(i) == bestName) return i;
        }

        // 全期样本最多者（AC + DC 合并）
        var all = new Dictionary<int, long>();
        foreach (var d in report.Days)
        {
            foreach (var kv in d.Ac) all[kv.Key] = all.GetValueOrDefault(kv.Key) + kv.Value.Samples;
            foreach (var kv in d.Dc) all[kv.Key] = all.GetValueOrDefault(kv.Key) + kv.Value.Samples;
        }
        return all.Count > 0 ? all.MaxBy(kv => kv.Value).Key : -1;
    }

    /// <summary>与趋势页折线同源，只取报告口径那一侧。</summary>
    private static double DeltaTOf(DailyTrend d, int category, bool onAc) =>
        d.Get(category, onAc)?.DeltaT ?? double.NaN;

    // ------------------------------------------------------------------
    // AI 摘要
    // ------------------------------------------------------------------

    private static string BuildDigest(
        TrendReport report, TrendWindow win,
        HealthLevel health,
        List<EvidenceItem> evidence,
        List<AttributionItem> attributions,
        string cleanDigestLine,
        string note,
        bool insufficient,
        double curAmb, double prevAmb)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ThermalTrace 趋势数据摘要（供 AI 解读）");
        sb.AppendLine($"- 数据范围：{RangeText(report)}（{report.DaysWithData} 个有效天数）");
        sb.AppendLine($"- 对比窗口：{win.Label}（{WindowRanges(report, win)}）");
        sb.AppendLine("- 口径说明：ΔT = 该负载类别均温 − 当天空闲档最低温（已消室温影响）；K/W = 均温 ÷ 平均功耗");
        sb.AppendLine($"- 环境气温：本期 {T(curAmb, "0.0")}℃ / 上期 {T(prevAmb, "0.0")}℃");
        sb.AppendLine($"- 健康度：{health.Text()}");
        sb.AppendLine("- 证据：");
        if (evidence.Count == 0) sb.AppendLine("  - （无显著变化）");
        else foreach (var e in evidence) sb.AppendLine($"  - {e.Text}");

        sb.AppendLine("- 归因（含验证方法）：");
        if (insufficient) sb.AppendLine("  - 数据不足，不做归因");
        else if (attributions.Count == 0) sb.AppendLine("  - 无明显异常模式");
        else foreach (var a in attributions) sb.AppendLine($"  - {a.Text}");

        sb.AppendLine(cleanDigestLine);

        sb.AppendLine($"- 口径状态：{(report.CaliberWarnings.Count > 0 ? "有变更（跨口径对比不可信）" : "无变更")}");
        sb.AppendLine($"- 数据可信度：{note}");
        sb.AppendLine("请基于以上数据解读散热趋势与可能原因，并给出行动建议。");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    // 取数小工具
    // ------------------------------------------------------------------

    /// <summary>本期 / 上期的环境气温均值；任一侧无数据为 NaN。</summary>
    private static (double Cur, double Prev) AmbientMeans(TrendReport report, TrendWindow win)
    {
        if (report.FirstDay is not { } first || report.LastDay is not { } last)
            return (double.NaN, double.NaN);

        var (cf, ct, pf, pt) = win.Resolve(first, last);
        return (Mean(report.Days, cf, ct), Mean(report.Days, pf, pt));

        static double Mean(List<DailyTrend> days, DateOnly from, DateOnly to)
        {
            var vals = days.Where(d => d.Date >= from && d.Date <= to)
                .Select(d => d.AmbientMean)
                .Where(v => !double.IsNaN(v))
                .ToList();
            return vals.Count > 0 ? vals.Average() : double.NaN;
        }
    }

    private static int WindowDays(TrendWindow win, TrendReport report)
    {
        if (win.IsAll) return report.DaysWithData;
        if (report.FirstDay is not { } first || report.LastDay is not { } last)
            return 0;
        var (cf, ct, _, _) = win.Resolve(first, last);
        return Math.Max(1, ct.DayNumber - cf.DayNumber + 1);
    }

    private static string RangeText(TrendReport report) =>
        report.FirstDay is { } f && report.LastDay is { } l
            ? $"{f:yyyy-MM-dd} ~ {l:yyyy-MM-dd}"
            : "无数据";

    private static string WindowRanges(TrendReport report, TrendWindow win)
    {
        if (report.FirstDay is not { } first || report.LastDay is not { } last)
            return "本期 — vs 上期 —";
        var (cf, ct, pf, pt) = win.Resolve(first, last);
        return $"本期 {cf:yyyy-MM-dd}~{ct:yyyy-MM-dd} vs 上期 {pf:yyyy-MM-dd}~{pt:yyyy-MM-dd}";
    }

    /// <summary>
    /// 缺失渲染为「—」，绝不渲染成 0（0 会被读成"没变化"）。
    /// NaN 同样按缺失处理——"NaN" 三个字母出现在给 AI 的摘要里，
    /// 既不专业又可能被模型当成某种信号去解读。
    /// </summary>
    private static string T(double? v, string fmt) =>
        v is null || double.IsNaN(v.Value)
            ? "—"
            : v.Value.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>带符号的变化量；负号由 ToString 给出，正号手动补（与 HtmlTrendReport.Diff 一致）。</summary>
    private static string Signed(double v, string fmt, string unit = "")
    {
        var s = v.ToString(fmt, CultureInfo.InvariantCulture);
        return (v > 0 ? "+" : "") + s + unit;
    }
}
