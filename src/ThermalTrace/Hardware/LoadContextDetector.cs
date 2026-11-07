using ThermalTrace.Model;

namespace ThermalTrace.Hardware;

/// <summary>
/// 单核峰值检测。这是伴随记录模式最关键的指标。
///
/// 【为什么必须看单核峰值而不是总利用率】
/// 本方案面向 4 核 8 线程的 Intel CPU。实测静置时总利用率 13% 但单核峰值 47%。
/// 用户症状是"占用不到 50% 却 90℃+"——总利用率 50% 极可能是 2 个核心各跑满，
/// 而产生热量的是那个跑满的核心。只记总利用率会把"双核满载"错归为"中等负载"，
/// 清灰效果永远解释不清。
/// </summary>
internal static class LoadContextDetector
{
    /// <summary>
    /// 单核最高利用率（%）。优先用 LHM 现成的 "CPU Core Max"（官方口径），
    /// 它是所有核心利用率的官方最大值，比手工遍历逐线程更可靠。
    /// </summary>
    public static double PeakCoreUtil(HardwareMonitor hw, SensorBinding binding)
    {
        var direct = hw.ReadById(binding.CpuCoreMaxUtilId);
        if (direct is not null && !double.IsNaN(direct.Value)) return direct.Value;

        // 兜底：手工遍历逐核心传感器
        if (binding.CpuCoreUtilIds.Count == 0) return double.NaN;

        double peak = double.NaN;
        foreach (var id in binding.CpuCoreUtilIds)
        {
            var v = hw.ReadById(id);
            if (v is null || double.IsNaN(v.Value)) continue;
            if (double.IsNaN(peak) || v.Value > peak) peak = v.Value;
        }
        return peak;
    }

    /// <summary>
    /// 有效负载档位：总利用率与单核峰值取<b>更高者</b>。
    ///
    /// 为什么要取 max：只按总利用率分档的话，"双核跑满"会被归入中等档，
    /// 分析时得出"中等负载下温度不高"的错误结论。
    /// </summary>
    public static LoadBand EffectiveBand(HardwareMonitor hw, SensorBinding binding,
                                        double totalCpuUtilPct)
    {
        var peak = PeakCoreUtil(hw, binding);
        double basis = totalCpuUtilPct;
        if (!double.IsNaN(peak) && peak > basis) basis = peak;
        return LoadBandExtensions.FromMaxCoreUtil(basis);
    }
}
