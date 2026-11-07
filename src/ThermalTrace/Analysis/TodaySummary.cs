using System.Globalization;
using ThermalTrace.Core;
using ThermalTrace.Model;
using ThermalTrace.Storage;

namespace ThermalTrace.Analysis;

/// <summary>
/// 单日、单个展示类别的卡片数据（今日概览的一张大卡片）。
///
/// 【缺失一律是 NaN】展示层据此渲染「—」。绝不能把缺失填成 0——
/// 「0℃」会被读成"凉快"，「—」才是"没采到"（取值规则）。
/// </summary>
internal sealed record TodayCardData(
    int Category,
    string Name,
    bool HasData,
    double TempMean,
    double TempMax,
    double DeltaT,
    double Hours,
    double PowerMean,
    double Over85Pct,
    double Over85Minutes,
    long Samples,
    int Slices)
{
    /// <summary>缺失的统一文案。展示层别自己写"—"，用这个，免得两处漂移。</summary>
    public const string Dash = "—";

    public bool HasDeltaT => !double.IsNaN(DeltaT);

    /// <summary>均温。无数据显示「—」，绝不显示 0℃。</summary>
    public string TempText => double.IsNaN(TempMean) ? Dash : $"{TempMean:0.0}℃";

    /// <summary>ΔT（带正负号与单位）。无数据显示「—」。</summary>
    public string DeltaTText => HasDeltaT
        ? (DeltaT >= 0 ? "+" : "") + DeltaT.ToString("0.0", CultureInfo.InvariantCulture) + "℃"
        : Dash;

    /// <summary>
    /// 时长。不足 0.1h 改用分钟——0.027h 印成「0.0h」会被读成"没干活"，
    /// 实际那是 20 个样本、约 2 分钟。
    /// </summary>
    public string HoursText => Hours <= 0 ? "0h"
        : Hours < 0.1 ? $"{Math.Max(1, (long)Math.Round(Hours * 60))}m"
        : $"{Hours:0.#}h";

    public string PowerText => double.IsNaN(PowerMean) ? Dash : $"{PowerMean:0.0}W";

    public string Over85Text => double.IsNaN(Over85Pct) ? Dash : $"超85 {Over85Pct:0.#}%";

    /// <summary>样本数。与 CSV 的 <c>n</c> 同一口径；无样本显示「—」，不印 0。</summary>
    public string SamplesText => Samples > 0 ? $"样本 {Samples}" : Dash;
}

/// <summary>
/// 今日概览一整页的数据。GUI 卡片页与 CLI <c>--today</c> 共用同一份，
/// 两边不许各算各的（验收总表：「--today / --trend 与 GUI 显示同源同值」）。
/// </summary>
internal sealed record TodaySummaryData(
    DateOnly Date,
    bool OnAc,
    bool HasAnyData,
    double AmbientC,
    // 气温是"空闲档最低温 + 5℃"的代理值，而不是实测 / 在线值（G4.1）。
    bool AmbientIsProxy,
    double IdleBaseline,
    bool IdleApproximated,
    string IdleBaselineBand,
    long SampleCount,
    int SliceCount,
    double RecordedHours,
    double MaxTempC,
    DateTimeOffset? MaxTempAt,
    string MaxTempCategory,
    double Over85Minutes,
    long AcSamples,
    long DcSamples,
    // 按 slice 去重后的片数（同一片内 AC/DC 各有多行，只算一片）。
    // 空态要写"电池今日 0 片"，不能靠行数或样本数反推。
    int AcSlices,
    int DcSlices,
    // GPU / SSD 当前值（noncpu_signals_gui.md 3.2）。缺失一律 NaN / null，
    // 由展示层渲染「—」——绝不填 0（0℃ 会被读成"凉快"）。
    double GpuTempMean,
    double GpuHours,
    double GpuUtilMean,
    double GpuIdleTempMean,
    double GpuActiveTempMean,
    int GpuSlices,
    DriveHealth? SsdLatest,
    IReadOnlyList<TodayCardData> Cards)
{
    public bool HasAcData => AcSamples > 0;
    public bool HasDcData => DcSamples > 0;

    /// <summary>今日有没有采到 GPU 温度（今天没用显卡 = 没有，不是 0℃）。</summary>
    public bool HasGpu => !double.IsNaN(GpuTempMean);

    /// <summary>今日有没有采到 GPU 负载（2026-10-16 新口径）。没有 = 旧构建 / 传感器读不到。</summary>
    public bool HasGpuLoad => !double.IsNaN(GpuIdleTempMean) || !double.IsNaN(GpuActiveTempMean);

    /// <summary>GPU 行文案。GUI 页脚与 CLI <c>--today</c> 打印同一串（G4 同源同值）。</summary>
    /// <remarks>
    /// 2026-10-16 口径修正：「使用」从此是<b>负载 &gt; 0</b> 的时长（与 LiteMonitor 的
    /// 「0% / 非 0」同一口径），不再是"采到温度即计入"——后者等于开机时长。
    /// 负载完全没采到时明说，不把"没采到"演成"使用 0h"。
    /// </remarks>
    public string GpuText
    {
        get
        {
            if (!HasGpu) return "GPU 今日 —";
            // 负载完全没采到（旧构建 / 传感器读不到）时只说温度，不给时长——
            // 时长在这一天是 NaN（没量到），印成 0h 会被读成"没用显卡"。
            if (!HasGpuLoad)
                return $"GPU 今日 {GpuTempMean:0.0}℃ · 负载未采到";

            var s = $"GPU 今日 使用 {HoursText(GpuHours)}";
            if (!double.IsNaN(GpuUtilMean)) s += $"（均负载 {GpuUtilMean:0.#}%）";
            if (!double.IsNaN(GpuIdleTempMean)) s += $" · 闲时 {GpuIdleTempMean:0.0}℃";
            if (!double.IsNaN(GpuActiveTempMean)) s += $" · 忙时 {GpuActiveTempMean:0.0}℃";
            return s;
        }
    }

    /// <summary>
    /// SSD 行文案：取 <c>health\ssd.csv</c> 最后一行。
    /// 程序没跑的日子没有行，所以最后一行可能不是今天——标注日期，
    /// 免得把上周的记录当成今天的现状。
    /// </summary>
    public string SsdText
    {
        get
        {
            var h = SsdLatest;
            if (h is null || h.IsEmpty) return "SSD 寿命 — · 累计写入 —";

            var life = double.IsNaN(h.Life) ? "—" : $"{h.Life:0}%";
            var used = double.IsNaN(h.PercentageUsed) ? "—" : $"{h.PercentageUsed:0}%";
            var written = double.IsNaN(h.DataWrittenGb) ? "—" : $"{h.DataWrittenGb:0} GB";
            var hours = double.IsNaN(h.PowerOnHours) ? "—" : $"{h.PowerOnHours:0} h";
            var stale = h.Date != Date ? $"（{h.Date:MM-dd} 记）" : "";
            return $"SSD 寿命 {life} · 已用 {used} · 累计写入 {written} · 通电 {hours}{stale}";
        }
    }

    /// <summary>小时数的紧凑格式（1h20m）。</summary>
    public static string HoursText(double hours)
    {
        var m = (long)Math.Round(hours * 60);
        return $"{m / 60}h{m % 60}m";
    }
}

/// <summary>
/// 今日概览的计算。
///
/// 【为什么不自己写加权】这里直接复用 <see cref="CatDayStat"/>（<c>--trend</c> / HTML 报告
/// 用的就是它），因为加权口径必须与趋势报告完全一致：
/// 同一个人在卡片页看到 55.8℃、在趋势报告里看到 54.9℃，数据可信度就没了
/// （口径约定第 1 条）。CatDayStat.Add 是按 <see cref="SliceRow.SampleCount"/> 加权的，
/// 遗留的 MainWindowHost.BuildTodaySummary 是每行等权平均——那正是要修的 bug。
///
/// 【不做的事】不碰 CSV、不缓存、不做实时刷新。每次调用都重读当日文件，
/// 保证"设置页改了桶边界 → 切回来立即重算"。
/// </summary>
internal static class TodaySummary
{
    public static TodaySummaryData Build(DateOnly day, bool onAc) =>
        Build(SettingsStore.Load(), day, onAc);

    public static TodaySummaryData Build(AppSettings settings, DateOnly day, bool onAc)
    {
        var rows = DailyStore.ReadRange(day, day).Where(r => r.Date == day).ToList();

        var (baseline, approximated, baselineBand) = Aggregator.DailyIdleBaseline(rows, day);
        var interval = Math.Max(1, settings.Sampler.IntervalSeconds);

        // 联网气温缺失时，按设计降级链回退到"空闲档最低温 +5℃"代理，而非直接留空。
        // 该代理只用于表头展示，不影响 ΔT（ΔT 直接用 baseline 计算）。
        // 当连基线也缺失（当天完全无数据）时仍保持 NaN → 表头显示"不可用"。
        var rawAmbient = AmbientLookup.At(DateTimeOffset.Now);
        var ambientProxy = double.IsNaN(rawAmbient) && !double.IsNaN(baseline);
        var ambient = ambientProxy ? baseline + 5.0 : rawAmbient;

        // ---- 按展示类别归并（权重 = SampleCount，由 CatDayStat 负责）----
        var stats = new Dictionary<int, CatDayStat>();
        var slicesPerCat = new Dictionary<int, int>();
        var sliceStarts = new HashSet<DateTimeOffset>();
        var acSlices = new HashSet<DateTimeOffset>();
        var dcSlices = new HashSet<DateTimeOffset>();
        long acSamples = 0, dcSamples = 0;

        foreach (var r in rows)
        {
            if (r.OnAcPower)
            {
                acSamples += r.SampleCount;
                acSlices.Add(r.SliceStart);
            }
            else
            {
                dcSamples += r.SampleCount;
                dcSlices.Add(r.SliceStart);
            }

            // 供电切换是整页级的：非选中供电的行只用来判断"另一侧有没有数据"
            if (r.OnAcPower != onAc) continue;

            sliceStarts.Add(r.SliceStart);

            var dash = r.BandKey.IndexOf('-');
            if (dash < 1 || !int.TryParse(r.BandKey[..dash], out var lower)) continue;

            var cat = settings.CategoryOf(lower);
            if (!stats.TryGetValue(cat, out var stat))
            {
                stat = new CatDayStat { Category = cat, OnAc = onAc };
                stats[cat] = stat;
            }
            stat.Add(r);
            slicesPerCat[cat] = slicesPerCat.GetValueOrDefault(cat) + 1;
        }

        // Finalize 之前 TempMean / DeltaT / HoursActive 都是未计算的
        foreach (var stat in stats.Values) stat.Finalize(baseline, interval);

        // ---- 今日最高（含发生在哪一档）----
        double maxTemp = double.NaN;
        DateTimeOffset? maxAt = null;
        var maxCategory = "";
        foreach (var r in rows)
        {
            if (r.OnAcPower != onAc || double.IsNaN(r.TempMax)) continue;
            if (!double.IsNaN(maxTemp) && r.TempMax <= maxTemp) continue;

            maxTemp = r.TempMax;
            maxAt = r.SliceStart;
            var dash = r.BandKey.IndexOf('-');
            maxCategory = dash >= 1 && int.TryParse(r.BandKey[..dash], out var lo)
                ? settings.CategoryName(settings.CategoryOf(lo))
                : "";
        }

        // ---- 卡片：类别序号 0..N-1 全部出齐，没数据的那一类显示「—」 ----
        var bounds = settings.CategoryLowerBounds;
        int count = bounds is null || bounds.Length == 0 ? 1 : bounds.Length;

        var cards = new List<TodayCardData>(count);
        for (int i = 0; i < count; i++)
        {
            stats.TryGetValue(i, out var stat);
            cards.Add(new TodayCardData(
                Category: i,
                Name: settings.CategoryName(i),
                HasData: stat is not null && !double.IsNaN(stat.TempMean),
                TempMean: stat?.TempMean ?? double.NaN,
                TempMax: stat?.TempMax ?? double.NaN,
                DeltaT: stat?.DeltaT ?? double.NaN,
                Hours: stat?.HoursActive ?? 0,
                PowerMean: stat?.PowerMean ?? double.NaN,
                Over85Pct: stat?.Over85Pct ?? double.NaN,
                Over85Minutes: (stat?.HoursOver85 ?? 0) * 60.0,
                Samples: stat?.Samples ?? 0,
                Slices: slicesPerCat.GetValueOrDefault(i)));
        }

        // ---- GPU / SSD 当前值（noncpu_signals_gui.md 3.2）----
        // GPU 只统计当前选中供电的行（R7：AC/DC 不混算），口径与上面的卡片一致
        var gpu = HardwareTrend.GpuDay(rows.Where(r => r.OnAcPower == onAc).ToList(), day, interval);
        // SSD 是一天一行、与供电无关的累计量，取最后一条记录即可
        var ssdLatest = DriveHealthStore.ReadAll().LastOrDefault();

        return new TodaySummaryData(
            Date: day,
            OnAc: onAc,
            HasAnyData: rows.Count > 0,
            AmbientC: ambient,
            AmbientIsProxy: ambientProxy,
            IdleBaseline: baseline,
            IdleApproximated: approximated,
            IdleBaselineBand: baselineBand,
            SampleCount: stats.Values.Sum(s => s.Samples),
            SliceCount: sliceStarts.Count,
            RecordedHours: stats.Values.Sum(s => s.HoursActive),
            MaxTempC: maxTemp,
            MaxTempAt: maxAt,
            MaxTempCategory: maxCategory,
            Over85Minutes: stats.Values.Sum(s => s.HoursOver85) * 60.0,
            AcSamples: acSamples,
            DcSamples: dcSamples,
            AcSlices: acSlices.Count,
            DcSlices: dcSlices.Count,
            GpuTempMean: gpu.TempMean,
            GpuHours: gpu.HoursUsed,
            GpuUtilMean: gpu.UtilMean,
            GpuIdleTempMean: gpu.IdleTempMean,
            GpuActiveTempMean: gpu.ActiveTempMean,
            GpuSlices: gpu.SlicesWithGpu,
            SsdLatest: ssdLatest,
            Cards: cards);
    }
}
