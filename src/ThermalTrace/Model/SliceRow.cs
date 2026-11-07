using System.Globalization;

namespace ThermalTrace.Model;

/// <summary>
/// 从每日 CSV 读回的一行聚合数据。字段与 DailyStore.Fields 一一对应。
/// 所有可能缺失的数值字段用 double.NaN 表示，绝不用 0 代替。
/// </summary>
public sealed record SliceRow(
    DateTimeOffset SliceStart,
    string BandKey,
    bool OnAcPower,
    int SampleCount,
    double TempMean,
    double TempP95,
    double TempMax,
    double TempMin,
    double PowerW,
    double TempPerWatt,
    double Over85Pct,
    double Over90Pct,
    double TjmaxMin,
    double CoreMaxMean,
    double UtilMean,
    string Flag,
    // GPU 温度均值 —— 第 17 列，2026-10-13 才加进来的。
    // 旧文件里没有这一列，Parse 一律填 NaN（缺失），绝不因此丢行。
    // 口径与 CPU 温度一致：按样本数 n 加权；主 GPU 取独显，独显没绑才取核显。
    double GpuTempMean,
    // ---- 2026-10-16 GPU 负载口径（第 18–28 列）----
    // 背景：旧"使用时长"= 采到 GPU 温度即计入，而独显待机也报温度 → 等于开机时长。
    // 新版：使用时长 = 负载 > 0 的累计时长；温度按负载分 5 档记录（0 / 1–25 / 26–50 / 51–75 / 76–100）。
    // 旧文件缺这些列 → 温度 NaN、计数 0（该档"没有样本"，与"温度为 0"是两回事）。
    double GpuUtilMean,
    double[] GpuBucketTemps,
    long[] GpuBucketCounts)
{
    public bool IsLowConfidence =>
        Flag.Contains("low_n", StringComparison.Ordinal) || SampleCount < 30;

    /// <summary>解析一行 CSV。任何字段异常都返回 null（跳过该行），不抛异常。</summary>
    public static SliceRow? Parse(string line)
    {
        try
        {
            var f = line.Split(',');
            if (f.Length < 16) return null;

            return new SliceRow(
                SliceStart: DateTimeOffset.Parse(f[0], CultureInfo.InvariantCulture),
                BandKey: f[1],
                OnAcPower: f[2] == "AC",
                SampleCount: int.Parse(f[3], CultureInfo.InvariantCulture),
                TempMean: P(f[4]), TempP95: P(f[5]), TempMax: P(f[6]), TempMin: P(f[7]),
                PowerW: P(f[8]), TempPerWatt: P(f[9]),
                Over85Pct: P(f[10]), Over90Pct: P(f[11]),
                TjmaxMin: P(f[12]), CoreMaxMean: P(f[13]), UtilMean: P(f[14]),
                Flag: f.Length > 15 ? f[15] : "",
                // 第 17 列（GPU 温度）2026-10-13 才加进来：旧行没有这一列 → 填 NaN 表示缺失。
                // 绝不能因为缺列就 return null —— 那会让改版之前的历史数据凭空消失。
                GpuTempMean: f.Length > 16 ? P(f[16]) : double.NaN,
                // 第 18–28 列（GPU 负载口径）2026-10-16 才加进来，同样缺列即缺失。
                GpuUtilMean: f.Length > 17 ? P(f[17]) : double.NaN,
                GpuBucketTemps: ReadBuckets(f, 18),
                GpuBucketCounts: ReadBucketCounts(f, 19));
        }
        catch
        {
            // 半截行、列数不足、格式错误 —— 全部跳过
            return null;
        }
    }

    /// <summary>
    /// 读 5 个负载档的均温（第 18 列起，每组占 2 列：均温、样本数）。
    /// 缺列 → NaN。绝不填 0：0℃ 会被读成"凉快"，NaN 才会渲染成「—」。
    /// </summary>
    private static double[] ReadBuckets(string[] f, int start)
    {
        var a = new double[SliceAggregate.GpuBucketCount];
        for (int i = 0; i < a.Length; i++)
            a[i] = f.Length > start + i * 2 ? P(f[start + i * 2]) : double.NaN;
        return a;
    }

    /// <summary>读 5 个负载档的样本数。缺列 / 非法 → 0（"该档没有样本"）。</summary>
    private static long[] ReadBucketCounts(string[] f, int start)
    {
        var a = new long[SliceAggregate.GpuBucketCount];
        for (int i = 0; i < a.Length; i++)
            a[i] = f.Length > start + i * 2
                   && long.TryParse(f[start + i * 2], NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out var v)
                ? v
                : 0;
        return a;
    }

    /// <summary>空串 / 非法数值 -> NaN。这是"缺失"与"0"的根本区别。</summary>
    private static double P(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    public DateOnly Date => DateOnly.FromDateTime(SliceStart.LocalDateTime);
}