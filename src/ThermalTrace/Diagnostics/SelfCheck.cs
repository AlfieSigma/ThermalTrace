using ThermalTrace.Core;
using ThermalTrace.Hardware;
using ThermalTrace.Model;

namespace ThermalTrace.Diagnostics;

internal sealed record SelfCheckReport(DateTimeOffset At, IReadOnlyList<DiagItem> Items)
{
    public IEnumerable<DiagItem> Errors => Items.Where(i => i.Level == DiagLevel.Error);
    public IEnumerable<DiagItem> Warnings => Items.Where(i => i.Level == DiagLevel.Warn);
    public bool Passed => !Errors.Any();
}

/// <summary>
/// 传感器自检（--doctor 与开机自检 --self-check 共用同一套逻辑）。
///
/// 这套检查存在的现实原因：LiteMonitor 这类第三方工具"能显示温度"不代表读数可信。
/// 常见机型为 Intel + 笔记本 EC 方案，常见坑有：
///  - WinRing0 驱动被安全软件拦截，导致 Package 温度全空，只有 EC 的粗值
///  - 绑定到了 Tctl 而非 Tdie，与 LiteMonitor 显示口径不同（能差 3~8℃）
///  - 核显与独显双显卡，选错 GPU 温度源
///  - 风扇转速传感器在某些 EC 固件下恒为 0（并非风扇没转）
///  - 温度量程离谱（>120℃ 或恒 0）——通常是绑定到了非温度传感器
/// </summary>
internal static class SelfCheck
{
    /// <summary>完整自检：环境 → 传感器枚举 → 绑定校验 → 短时连续采样稳定性。hw 必须已 Open。</summary>
    public static SelfCheckReport Run(AppSettings settings, HardwareMonitor hw)
    {
        var items = new List<DiagItem>();
        var now = DateTimeOffset.Now;

        // ---------- 1. 环境 ----------
        if (SystemProbe.IsAdministrator())
            items.Add(new DiagItem(DiagLevel.Ok, "权限", "已以管理员身份运行", "MSR 读取权限正常"));
        else
            items.Add(new DiagItem(DiagLevel.Error, "权限", "当前不是管理员权限",
                "读取 CPU MSR 必须提权。请右键『以管理员身份运行』"));

        var foreign = SystemProbe.DetectForeignMonitors();
        if (foreign.Count > 0)
            items.Add(new DiagItem(DiagLevel.Warn, "冲突",
                $"检测到其他硬件监控程序：{string.Join("、", foreign)}",
                "它们会争抢 MSR 独占访问，可能导致本程序读数间歇为空或跳变。"
                + "建议测量期间关闭；LiteMonitor 可在最终核对时短暂打开"));

        var (onAc, battery) = SystemProbe.ReadPowerState();
        if (onAc)
            items.Add(new DiagItem(DiagLevel.Ok, "电源", "已接通交流电", ""));
        else
            items.Add(new DiagItem(DiagLevel.Warn, "电源",
                double.IsNaN(battery) ? "当前使用电池" : $"当前使用电池（{battery:0}%）",
                "电池供电时 CPU 功耗墙更紧，测得温度偏低且与交流电状态不可比"));

        // ---------- 2. 传感器枚举 ----------
        IReadOnlyList<SensorDescriptor> sensors;
        try
        {
            sensors = hw.EnumerateSensors();
        }
        catch (Exception ex)
        {
            items.Add(new DiagItem(DiagLevel.Error, "枚举", $"枚举传感器失败: {ex.Message}", ""));
            return new SelfCheckReport(now, items);
        }

        // ---------- 3. 绑定：缺失则自动推断一份供检查 ----------
        var binding = settings.Sensors;
        if (!binding.IsUsable)
        {
            items.Add(new DiagItem(DiagLevel.Warn, "绑定", "尚未绑定传感器，正在使用自动推断结果检查",
                "点『今日概览』页的『重新绑定传感器』即可固化"));
            binding = SensorCatalog.AutoBind(sensors).Binding;
        }
        items.AddRange(SensorCatalog.Validate(sensors, binding));

        // ---------- 4. 硬件拓扑（核对自动绑定是否正确）----------
        items.AddRange(SensorStabilityProbe.DescribeTopology(sensors));

        // ---------- 5. 短时连续采样稳定性 ----------
        // 厂商只用于选择结论文案（AMD 的 SMU 温度 / 功耗与 Intel 的 MSR 链路是两套口径）
        items.AddRange(SensorStabilityProbe.Probe(hw, binding, VendorProfile.CpuVendorOf(sensors)));

        // ---------- 6. 硬件指纹与工况条件 ----------
        var probe = hw.Probe();
        items.Add(new DiagItem(DiagLevel.Info, "硬件",
            $"{probe.CpuName}（{probe.PhysicalCores} 物理核 / {probe.LogicalCores} 线程）", ""));
        items.Add(new DiagItem(DiagLevel.Info, "硬件", $"BIOS: {probe.BiosVersion}", ""));
        items.Add(new DiagItem(DiagLevel.Info, "硬件", $"GPU: {probe.GpuList}", ""));
        items.Add(new DiagItem(DiagLevel.Info, "环境", $"电源方案: {SystemProbe.ReadPowerScheme()}", ""));

        return new SelfCheckReport(now, items);
    }

    /// <summary>把诊断结果格式化为控制台可读文本。</summary>
    public static string Format(SelfCheckReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("════════════════════════════════════════════════════════════");
        sb.AppendLine($"  ThermalTrace 传感器自检 — {report.At:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("════════════════════════════════════════════════════════════");

        foreach (var group in report.Items.GroupBy(i => i.Category))
        {
            sb.AppendLine();
            sb.AppendLine($"【{group.Key}】");
            foreach (var item in group)
            {
                var tag = item.Level switch
                {
                    DiagLevel.Ok => "[ OK ]",
                    DiagLevel.Warn => "[WARN]",
                    DiagLevel.Error => "[FAIL]",
                    _ => "[INFO]"
                };
                sb.AppendLine($"  {tag} {item.Message}");
                if (!string.IsNullOrEmpty(item.Hint))
                    sb.AppendLine($"         → {item.Hint}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("────────────────────────────────────────────────────────────");
        sb.AppendLine(report.Passed
            ? $"结论：自检通过（{report.Warnings.Count()} 项警告，不影响测量）"
            : $"结论：自检未通过（{report.Errors.Count()} 项错误，必须先解决）");
        if (report.Passed && report.Warnings.Any())
            sb.AppendLine("提示：警告项会降低归因能力，详见上方说明。");
        sb.AppendLine("════════════════════════════════════════════════════════════");
        return sb.ToString();
    }
}