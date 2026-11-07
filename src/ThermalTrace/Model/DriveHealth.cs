using System.Globalization;

namespace ThermalTrace.Model;

/// <summary>
/// SSD 寿命计数器的一行（一天一行）。见非 CPU 信号说明。
///
/// 【为什么只记这些、不记温度】SSD 温度答不了散热也答不了趋势——它的主因是磁盘 I/O，
/// 与"CPU 单核峰值分档"根本不是同一个驱动量，拿它做纵向趋势读出来的是"这个月拷了多少文件"。
/// 下面这几个是<b>累计量</b>，直接回答"这块盘还剩多少寿命"，一天记一次就够了。
///
/// 【缺失口径】任一字段拿不到就是 NaN，绝不用 0 代替（0 会被读成"全新"或"没数据"两种相反含义）。
/// </summary>
public sealed record DriveHealth(
    DateOnly Date,
    double PercentageUsed,     // Percentage Used：已耗尽寿命百分比（NVMe 规范字段）
    double AvailableSpare,     // Available Spare：剩余备用块百分比
    double DataWrittenGb,      // Data Written：累计写入量
    double PowerOnHours,       // Power On Hours：通电小时
    double Life)               // Life：LHM 合成的剩余寿命百分比
{
    /// <summary>五个计数器一个都没读到。调用方据此决定记不记这一行。</summary>
    public bool IsEmpty =>
        double.IsNaN(PercentageUsed) && double.IsNaN(AvailableSpare)
        && double.IsNaN(DataWrittenGb) && double.IsNaN(PowerOnHours) && double.IsNaN(Life);

    public static DriveHealth? Parse(string line)
    {
        try
        {
            var f = line.Split(',');
            if (f.Length < 6) return null;
            if (!DateOnly.TryParseExact(f[0].Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;

            return new DriveHealth(d, P(f[1]), P(f[2]), P(f[3]), P(f[4]), P(f[5]));
        }
        catch
        {
            return null;   // 半截行 / 格式错误 —— 跳过
        }
    }

    public string ToCsv()
    {
        var c = CultureInfo.InvariantCulture;
        string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.##", c);
        return string.Join(',',
            Date.ToString("yyyy-MM-dd", c),
            F(PercentageUsed), F(AvailableSpare), F(DataWrittenGb), F(PowerOnHours), F(Life));
    }

    /// <summary>空串 / 非法数值 -> NaN。与 <see cref="SliceRow"/> 同一条纪律。</summary>
    private static double P(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
}
