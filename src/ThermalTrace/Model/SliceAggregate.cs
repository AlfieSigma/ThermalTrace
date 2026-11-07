using System.Globalization;

namespace ThermalTrace.Model;

/// <summary>
/// 单个时间片内、某个负载档位的聚合结果。这是整个程序唯一落盘的数据单元。
///
/// 【为什么在线聚合而不是存原始样本】
/// 原始逐秒样本里 90% 以上是"低负载 + 温度等于环境温度"的无信息量数据。
/// 真正需要的是"在某个负载档下，温度分布是什么样"。这个结论可以在线累加得到，
/// 不需要保存每一条原始样本。落盘量因此从每天 5.5 MB 降到约 20 KB。
///
/// 【内存里保留原始温度值的唯一目的】
/// 为了算<精确>的 P50/P95（而不是近似）。一个时间片 30 分钟、5秒采样 = 最多 360 个值，
/// 内存占用可忽略。时间片结束算出统计后立即丢弃这些值。
/// </summary>
public sealed class SliceAggregate
{
    public DateTimeOffset SliceStart { get; init; }
    public LoadBand Band { get; init; }
    public bool OnAcPower { get; set; }

    public int Count { get; private set; }
    public double TempSum { get; private set; }
    public double PowerSum { get; private set; }
    public double CoreMaxSum { get; private set; }
    public double UtilSum { get; private set; }

    /// <summary>
    /// GPU 温度累加。与 CPU 分开设累加器，是因为 GPU 读数可能整片缺失
    /// （没用显卡的时间片里独显温度经常整个拿不到），
    /// 直接拿 CPU 的 Count 当分母会把它算成 0℃——那是"凉快"，不是"没采到"。
    /// </summary>
    public double GpuTempSum { get; private set; }
    private int _gpuCount;

    /// <summary>GPU 负载分档数：0%（空闲）/ 1–25 / 26–50 / 51–75 / 76–100。</summary>
    public const int GpuBucketCount = 5;

    /// <summary>
    /// 各负载档的 GPU 温度累加（下标见 <see cref="GpuBucketOf"/>）。
    /// 【为什么按负载分档】温度必须绑定负载才有意义：空闲 45℃ 与满载 78℃
    /// 平均出来的"61℃"两个问题都回答不了。CPU 侧"按负载档看温度"是同一思想。
    /// 【进档条件】温度与负载<b>同时</b>读到的样本才进档——缺一个就无法归因。
    /// </summary>
    private readonly double[] _gpuBucketTempSum = new double[GpuBucketCount];

    /// <summary>各负载档的样本数。日级加权用它，不再拿整片 n 近似。</summary>
    private readonly long[] _gpuBucketN = new long[GpuBucketCount];

    /// <summary>GPU 负载累加（有负载读数的样本，与温度是否可读无关）。</summary>
    private double _gpuUtilSum;
    private long _gpuUtilN;

    /// <summary>负载值 → 档位下标：0=0%（空闲）、1=1–25、2=26–50、3=51–75、4=76–100。</summary>
    public static int GpuBucketOf(double loadPct) => loadPct switch
    {
        <= 0 => 0,
        <= 25 => 1,
        <= 50 => 2,
        <= 75 => 3,
        _ => 4
    };

    public double TempMin { get; private set; } = double.NaN;
    public double TempMax { get; private set; } = double.NaN;
    public double TjmaxMin { get; private set; } = double.NaN;

    /// <summary>超阈计数。用计数而非占比，避免分母随时间片长度变化。</summary>
    public int Over85Count { get; private set; }
    public int Over90Count { get; private set; }

    private readonly List<double> _temps = new(512);

    public void Add(Sample s, double threshold85 = TrendThresholds.OverTempWarnC,
        double threshold90 = TrendThresholds.OverTempCriticalC)
    {
        var t = s.PrimaryCpuC;
        if (double.IsNaN(t)) return;   // 温度缺失的采样不计入，避免拉低统计

        Count++;
        TempSum += t;
        _temps.Add(t);

        if (double.IsNaN(TempMin) || t < TempMin) TempMin = t;
        if (double.IsNaN(TempMax) || t > TempMax) TempMax = t;

        if (t >= threshold85) Over85Count++;
        if (t >= threshold90) Over90Count++;

        if (!double.IsNaN(s.PackagePowerW)) PowerSum += s.PackagePowerW;
        if (!double.IsNaN(s.MaxCoreUtilPct)) CoreMaxSum += s.MaxCoreUtilPct;
        if (!double.IsNaN(s.UtilPct)) UtilSum += s.UtilPct;

        // ---- GPU：温度与负载分两条线累加（2026-10-16 口径修正）----
        // GpuTempMean 保持原口径：有 GPU 温度的全部样本均温（含空闲），
        // 与 2026-10-13 起的历史数据可比；"用没用显卡"由负载分档回答。
        var gpuTemp = s.PrimaryGpuC;
        var gpuLoad = s.PrimaryGpuLoadPct;
        if (!double.IsNaN(gpuTemp)) { GpuTempSum += gpuTemp; _gpuCount++; }
        if (!double.IsNaN(gpuLoad))
        {
            _gpuUtilSum += gpuLoad;
            _gpuUtilN++;
            // 只有温度也读到了才进档：负载单独可读对"该档温度"没有贡献
            if (!double.IsNaN(gpuTemp))
            {
                var b = GpuBucketOf(gpuLoad);
                _gpuBucketTempSum[b] += gpuTemp;
                _gpuBucketN[b]++;
            }
        }

        if (!double.IsNaN(s.TjMaxDistanceC) &&
            (double.IsNaN(TjmaxMin) || s.TjMaxDistanceC < TjmaxMin))
            TjmaxMin = s.TjMaxDistanceC;
    }

    public bool HasData => Count > 0;
    public double TempMean => Count == 0 ? double.NaN : TempSum / Count;
    public double PowerMean => Count == 0 ? double.NaN : PowerSum / Count;
    public double CoreMaxMean => Count == 0 ? double.NaN : CoreMaxSum / Count;
    public double UtilMean => Count == 0 ? double.NaN : UtilSum / Count;
    /// <summary>GPU 温度均值。分母是"真正读到 GPU 的样本数"，不是整片样本数。</summary>
    public double GpuTempMean => _gpuCount == 0 ? double.NaN : GpuTempSum / _gpuCount;

    /// <summary>片内 GPU 负载均值（%）。整片没读到负载时为 NaN——那是"没采到"，不是 0%。</summary>
    public double GpuUtilMean => _gpuUtilN == 0 ? double.NaN : _gpuUtilSum / _gpuUtilN;

    /// <summary>某负载档的 GPU 均温。该档本片无样本为 NaN（缺失，不是 0℃）。</summary>
    public double GpuBucketTemp(int bucket) =>
        bucket < 0 || bucket >= GpuBucketCount || _gpuBucketN[bucket] == 0
            ? double.NaN
            : _gpuBucketTempSum[bucket] / _gpuBucketN[bucket];

    /// <summary>某负载档的样本数（温度与负载同时读到的）。日级按档加权用。</summary>
    public long GpuBucketN(int bucket) =>
        bucket < 0 || bucket >= GpuBucketCount ? 0 : _gpuBucketN[bucket];

    public double Over85Pct => Count == 0 ? double.NaN : 100.0 * Over85Count / Count;
    public double Over90Pct => Count == 0 ? double.NaN : 100.0 * Over90Count / Count;

    /// <summary>每瓦温升 K/W。★跨天/跨季可比的核心指标（纯物理量）。</summary>
    public double TempPerWatt
    {
        get
        {
            var p = PowerMean;
            if (double.IsNaN(p) || p < 0.5 || Count == 0) return double.NaN;
            return TempMean / p;
        }
    }

    public double Percentile(double q)
    {
        if (_temps.Count == 0) return double.NaN;
        var sorted = _temps.OrderBy(x => x).ToArray();
        return PercentileOf(sorted, q);
    }

    /// <summary>线性插值百分位，与 Excel PERCENTILE.INC 一致。</summary>
    internal static double PercentileOf(double[] sorted, double q)
    {
        if (sorted.Length == 0) return double.NaN;
        if (sorted.Length == 1) return sorted[0];
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    /// <summary>序列化成 CSV 行。样本数过少时标记低置信度。</summary>
    /// <remarks>
    /// 第 18 列起为 2026-10-16 GPU 负载口径：<c>gpu_util_mean</c> + 5 组
    /// （档均温, 档样本数）。旧文件没有这些列，解析时缺列填 NaN / 0，不丢行。
    /// </remarks>
    public string ToCsv(int minSamplesForConfidence = 30)
    {
        var c = CultureInfo.InvariantCulture;
        string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.##", c);
        string flag = Count < minSamplesForConfidence ? "low_n" : "";

        return string.Join(',',
            SliceStart.ToString("yyyy-MM-ddTHH:mm", c),
            Band.Key(),
            OnAcPower ? "AC" : "DC",
            Count.ToString(c),
            F(TempMean), F(Percentile(0.95)), F(TempMax), F(TempMin),
            F(PowerMean), F(TempPerWatt),
            F(Over85Pct), F(Over90Pct),
            F(TjmaxMin), F(CoreMaxMean), F(UtilMean),
            flag, F(GpuTempMean),
            F(GpuUtilMean),
            F(GpuBucketTemp(0)), GpuBucketN(0).ToString(c),
            F(GpuBucketTemp(1)), GpuBucketN(1).ToString(c),
            F(GpuBucketTemp(2)), GpuBucketN(2).ToString(c),
            F(GpuBucketTemp(3)), GpuBucketN(3).ToString(c),
            F(GpuBucketTemp(4)), GpuBucketN(4).ToString(c));
    }

    /// <summary>导出成工作快照状态。含片内温度列表，重启后 P95 仍是精确值。</summary>
    public SliceAggregateState Export() => new()
    {
        SliceStart = SliceStart.ToString("o", CultureInfo.InvariantCulture),
        Band = (int)Band,
        OnAcPower = OnAcPower,
        Count = Count,
        TempSum = TempSum,
        PowerSum = PowerSum,
        CoreMaxSum = CoreMaxSum,
        UtilSum = UtilSum,
        GpuTempSum = GpuTempSum,
        GpuCount = _gpuCount,
        GpuBucketTempSum = (double[])_gpuBucketTempSum.Clone(),
        GpuBucketN = (long[])_gpuBucketN.Clone(),
        GpuUtilSum = _gpuUtilSum,
        GpuUtilN = _gpuUtilN,
        TempMin = TempMin,
        TempMax = TempMax,
        TjmaxMin = TjmaxMin,
        Over85Count = Over85Count,
        Over90Count = Over90Count,
        Temps = new List<double>(_temps)
    };

    /// <summary>从工作快照无损还原。字段逐一回填，不重放样本、不改统计口径。</summary>
    public static SliceAggregate FromState(SliceAggregateState st)
    {
        var agg = new SliceAggregate
        {
            SliceStart = DateTimeOffset.ParseExact(st.SliceStart, "o",
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Band = (LoadBand)Math.Clamp(st.Band, 0, LoadBandExtensions.Count - 1),
            OnAcPower = st.OnAcPower
        };

        agg.Count = st.Count;
        agg.TempSum = st.TempSum;
        agg.PowerSum = st.PowerSum;
        agg.CoreMaxSum = st.CoreMaxSum;
        agg.UtilSum = st.UtilSum;
        agg.GpuTempSum = st.GpuTempSum;
        agg._gpuCount = st.GpuCount;
        agg._gpuUtilSum = st.GpuUtilSum;
        agg._gpuUtilN = st.GpuUtilN;
        agg.TempMin = st.TempMin;
        agg.TempMax = st.TempMax;
        agg.TjmaxMin = st.TjmaxMin;
        agg.Over85Count = st.Over85Count;
        agg.Over90Count = st.Over90Count;

        if (st.GpuBucketTempSum is not null)
            Array.Copy(st.GpuBucketTempSum, agg._gpuBucketTempSum,
                Math.Min(st.GpuBucketTempSum.Length, GpuBucketCount));
        if (st.GpuBucketN is not null)
            Array.Copy(st.GpuBucketN, agg._gpuBucketN,
                Math.Min(st.GpuBucketN.Length, GpuBucketCount));
        if (st.Temps is not null) agg._temps.AddRange(st.Temps);

        return agg;
    }
}