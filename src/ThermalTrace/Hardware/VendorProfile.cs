using ThermalTrace.Model;

namespace ThermalTrace.Hardware;

/// <summary>CPU 厂商。只用于给自检结论选对文案，不参与绑定与统计。</summary>
internal enum CpuVendor { Intel, Amd, Other }

/// <summary>
/// 厂商识别。数据源只用已枚举到的硬件信息（CPU 名称、显卡节点的 HardwareType / HardwareName），
/// 不额外查询 WMI，避免自检多付一次 1~2 秒的探测成本。
/// </summary>
internal static class VendorProfile
{
    public static CpuVendor CpuVendorOf(IEnumerable<SensorDescriptor> sensors) =>
        CpuVendorOf(sensors.FirstOrDefault(s => s.IsCpu)?.HardwareName);

    public static CpuVendor CpuVendorOf(string? cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName)) return CpuVendor.Other;

        if (cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)
            || cpuName.Contains("EPYC", StringComparison.OrdinalIgnoreCase)
            || cpuName.Contains("Athlon", StringComparison.OrdinalIgnoreCase)
            || cpuName.Contains("Threadripper", StringComparison.OrdinalIgnoreCase))
            return CpuVendor.Amd;

        if (cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || cpuName.Contains("Core(TM)", StringComparison.OrdinalIgnoreCase)
            || cpuName.Contains("Xeon", StringComparison.OrdinalIgnoreCase))
            return CpuVendor.Intel;

        return CpuVendor.Other;
    }

    public static string CpuVendorName(CpuVendor vendor) => vendor switch
    {
        CpuVendor.Intel => "Intel",
        CpuVendor.Amd => "AMD",
        _ => "未知厂商"
    };

    public static string GpuVendorText(string hardwareType, string hardwareName)
    {
        if (hardwareType.Contains("Nvidia", StringComparison.OrdinalIgnoreCase)
            || hardwareName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return "NVIDIA";
        if (hardwareType.Contains("Amd", StringComparison.OrdinalIgnoreCase)
            || hardwareName.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || hardwareName.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return "AMD";
        if (hardwareType.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || hardwareName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return "Intel";
        return "未知厂商";
    }

    /// <summary>
    /// 是否集成显卡。Intel 的显卡节点按集显处理；AMD 需排除 RX / Radeon Pro 这类独显命名；
    /// NVIDIA 默认按独显处理。只用于选择自检文案，不参与温度口径。
    /// </summary>
    public static bool IsIntegratedGpu(string hardwareType, string hardwareName)
    {
        var vendor = GpuVendorText(hardwareType, hardwareName);
        return vendor switch
        {
            "Intel" => true,
            "AMD" => !hardwareName.Contains("RX", StringComparison.OrdinalIgnoreCase)
                     && !hardwareName.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
