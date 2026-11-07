using ThermalTrace.Model;

namespace ThermalTrace.Hardware;

/// <summary>
/// 主温度零读数判据。
///
/// 【存在理由】LHM 在不支持的平台（如部分 AMD Ryzen 的 SMU 温度）会返回 0 而不是 NaN。
/// 只区分「读不到」与「读到数值」会把 0 当作真实温度写进 CSV，进而污染空闲基线、ΔT
/// 与超温统计 —— 用户最终只看到一个 0℃。
///
/// 【判据】只认精确零带（|v| ≤ <see cref="ZeroBandC"/>）：运行中的 CPU 不可能真实处于 0℃。
/// 不设「低于某温度就判无效」的上限，避免误杀冬季冷启动等真实低温场景。
/// 命中零带的那一拍立即把对应温度字段置为 NaN，由聚合层按既有规则丢弃，
/// 因此 CSV 不会出现伪造的 0，也不存在「先写几行 0 再停」的窗口。
///
/// 【连续阈值】连续 <see cref="ConfirmSamples"/> 拍都没有可用主温度，才把传感器标成
/// 「恒返回 0」。该状态只用于界面与日志口径，不改变单拍 0 是否入库。
///
/// 【自愈】任一拍出现非零有效读数即恢复；<see cref="Reset"/> 供换绑传感器时无条件清零。
/// 判据不落盘，进程重启后从可用态重新累计。
/// </summary>
internal sealed class PrimaryTemperatureGuard
{
    /// <summary>零带半宽（℃）。传感器粒度为 1℃，0.05 足以覆盖 0.0 的显示与序列化误差。</summary>
    public const double ZeroBandC = 0.05;

    /// <summary>连续多少拍零读数才把原因标成「传感器恒返回 0」。</summary>
    public const int ConfirmSamples = 3;

    private int _zeroStreak;

    /// <summary>传感器是否已被判定为恒返回 0（仅影响界面与日志口径）。</summary>
    public bool Unavailable { get; private set; }

    /// <summary>当前连续零读数拍数，供排查使用。</summary>
    public int ZeroStreak => _zeroStreak;

    public static bool IsZeroReading(double celsius) =>
        !double.IsNaN(celsius) && Math.Abs(celsius) <= ZeroBandC;

    /// <summary>
    /// 清洗一拍样本并更新判据状态。主温度本身缺失（NaN）时原样返回，沿用既有缺失路径。
    /// </summary>
    public Sample Apply(Sample s)
    {
        var primary = s.PrimaryCpuC;
        if (double.IsNaN(primary)) return s;   // 既有「读不到」路径，不参与零读数判定

        if (!IsZeroReading(primary))
        {
            // 出现有效读数即自愈，哪怕之前已判定不可用
            _zeroStreak = 0;
            Unavailable = false;
            return s;
        }

        var cleaned = s with
        {
            CpuPackageC = DropZero(s.CpuPackageC),
            CpuTdieC = DropZero(s.CpuTdieC),
            CpuCoreMaxC = DropZero(s.CpuCoreMaxC),
            CpuCoreAvgC = DropZero(s.CpuCoreAvgC)
        };

        // 主链路的前一级返回 0，但后一级有有效读数（例如 Package 坏、Tdie 正常）：
        // 清洗后主温度仍可用，按可用处理。
        if (!double.IsNaN(cleaned.PrimaryCpuC))
        {
            _zeroStreak = 0;
            Unavailable = false;
            return cleaned;
        }

        _zeroStreak++;
        if (_zeroStreak >= ConfirmSamples) Unavailable = true;
        return cleaned;
    }

    /// <summary>换绑传感器或需要重新评估时无条件恢复可用态。</summary>
    public void Reset()
    {
        _zeroStreak = 0;
        Unavailable = false;
    }

    private static double DropZero(double celsius) =>
        IsZeroReading(celsius) ? double.NaN : celsius;
}
