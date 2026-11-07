using ThermalTrace.Hardware;
using ThermalTrace.Model;

namespace ThermalTrace.Diagnostics;

/// <summary>
/// 传感器连续采样稳定性探针。
///
/// 存在的理由：很多机器上「能显示温度数值」但数据完全不可信——恒为 0、剧烈跳变、
/// 或间歇性读不到。肉眼在托盘面板上看不出来，只有连续采样才能暴露。
/// 这也是区分「风扇转速传感器无效」与「风扇真的不转」的唯一手段。
/// </summary>
internal static class SensorStabilityProbe
{
    public const int WarmupTicks = 2;

    /// <summary>总采样次数，含预热。实际参与统计的是 <see cref="EffectiveTicks"/> 次。</summary>
    public const int DefaultTicks = 10;
    public const int IntervalMs = 1000;

    /// <summary>剔除预热后实际参与统计的采样次数。</summary>
    public const int EffectiveTicks = DefaultTicks - WarmupTicks;

    /// <summary>
    /// 【S1 实测踩坑】Open() 之后的第一次读取可能返回无效值——
    /// 首次 --doctor 测到 "6.0 ~ 64.0C"，其中 6℃ 物理上不可能（低于环境温度），
    /// 说明 MSR 在首次 Update 后尚未稳定。因此前 <see cref="WarmupTicks"/> 个采样点必须丢弃，
    /// 否则会污染最小值、均值与波动幅度判定。
    /// </summary>
    public static IReadOnlyList<DiagItem> Probe(
        HardwareMonitor hw, SensorBinding binding, CpuVendor cpuVendor = CpuVendor.Other)
    {
        var items = new List<DiagItem>();
        var temps = new List<double>();
        var powers = new List<double>();
        var fanRpm = new List<double>();
        var utils = new List<double>();
        var corePeaks = new List<double>();
        var tjmaxGaps = new List<double>();
        var discarded = new List<double>();
        int missingTemp = 0;

        for (int i = 0; i < DefaultTicks; i++)
        {
            try
            {
                hw.Update();
                var s = hw.ReadSample(binding, i);

                // 预热阶段的读数只记录不参与统计
                bool warming = i < WarmupTicks;

                if (double.IsNaN(s.PrimaryCpuC))
                {
                    if (!warming) missingTemp++;
                }
                else if (warming)
                {
                    discarded.Add(s.PrimaryCpuC);
                }
                else
                {
                    temps.Add(s.PrimaryCpuC);
                }

                if (!warming)
                {
                    if (!double.IsNaN(s.PackagePowerW)) powers.Add(s.PackagePowerW);
                    if (!double.IsNaN(s.FanRpm)) fanRpm.Add(s.FanRpm);
                    if (!double.IsNaN(s.UtilPct)) utils.Add(s.UtilPct);
                    if (!double.IsNaN(s.TjMaxDistanceC)) tjmaxGaps.Add(s.TjMaxDistanceC);
                    var peak = LoadContextDetector.PeakCoreUtil(hw, binding);
                    if (!double.IsNaN(peak)) corePeaks.Add(peak);
                }
            }
            catch (Exception ex)
            {
                items.Add(new DiagItem(DiagLevel.Error, "采样",
                    $"第 {i + 1} 次刷新失败: {ex.Message}",
                    "驱动可能被其他程序占用，或被安全软件拦截"));
                return items;
            }
            if (i < DefaultTicks - 1) Thread.Sleep(IntervalMs);
        }

        if (discarded.Count > 0 && discarded.Any(v => v < 20))
            items.Add(new DiagItem(DiagLevel.Info, "采样",
                $"已丢弃前 {discarded.Count} 个预热采样（读数: {string.Join("℃、", discarded)}）",
                "Open() 后首次 MSR 读取可能返回无效值。曾实测到 6℃ 这种低于环境温度的读数，"
                + "属正常现象，不影响测量结果"));

        items.AddRange(EvaluateTemperature(temps, missingTemp, utils, tjmaxGaps, cpuVendor));
        items.AddRange(EvaluatePower(powers, cpuVendor));
        items.AddRange(EvaluateFan(fanRpm));
        items.AddRange(EvaluateUtilization(utils, corePeaks, tjmaxGaps));
        return items;
    }

    /// <summary>
    /// 打印硬件节点分类统计。这不是诊断，而是**核对自动绑定是否正确**的手段——
    /// 【S1踩坑】曾因CPU 名称含 "Intel" 而被误判为显卡，
    /// 只有把每个硬件节点的类型与传感器数量摊开，才能一眼看出归类错误。
    /// </summary>
    public static IReadOnlyList<DiagItem> DescribeTopology(IReadOnlyList<SensorDescriptor> sensors)
    {
        var items = new List<DiagItem>();
        var groups = sensors
            .GroupBy(s => new { s.HardwareType, s.HardwareName })
            .OrderBy(g => g.Key.HardwareType);

        foreach (var g in groups)
        {
            var temps = g.Count(s => s.SensorType.Equals("Temperature", StringComparison.OrdinalIgnoreCase));
            var loads = g.Count(s => s.SensorType.Equals("Load", StringComparison.OrdinalIgnoreCase));
            var powers = g.Count(s => s.SensorType.Equals("Power", StringComparison.OrdinalIgnoreCase));
            var fans = g.Count(s => s.SensorType.Equals("Fan", StringComparison.OrdinalIgnoreCase));
            items.Add(new DiagItem(DiagLevel.Info, "硬件节点",
                $"[{g.Key.HardwareType,-10}] {g.Key.HardwareName}",
                $"温度 {temps}、功耗 {powers}、转速 {fans}、利用率 {loads}"));
        }

        // 显卡节点数量按实际枚举结果给结论，不再断言「本机应为 2 个」：
        // 单核显笔记本、双显卡笔记本、无独显驱动都是合法形态。
        var gpuNodes = groups
            .Where(g => g.Key.HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var gpuHint = gpuNodes.Count switch
        {
            0 => "未识别到显卡硬件节点。若本机确有独显，可能是驱动未安装或 LHM 未枚举；不影响 CPU 温度记录",
            1 when VendorProfile.IsIntegratedGpu(gpuNodes[0].Key.HardwareType, gpuNodes[0].Key.HardwareName)
                => "单显卡节点（集成显卡），按实际形态识别，无需第二块",
            1 => "单显卡节点（独立显卡），按实际形态识别，无需第二块",
            _ => "多显卡节点已分别识别"
        };
        items.Add(new DiagItem(gpuNodes.Count == 0 ? DiagLevel.Info : DiagLevel.Ok, "硬件节点",
            $"识别到 {gpuNodes.Count} 个显卡硬件节点", gpuHint));

        // 显卡温度缺失：核显通常不暴露，属正常；独显缺失更可能是驱动问题，需要提示。
        foreach (var g in gpuNodes)
        {
            var gpuTemps = g.Count(s =>
                s.SensorType.Equals("Temperature", StringComparison.OrdinalIgnoreCase));
            if (gpuTemps > 0) continue;

            var vendor = VendorProfile.GpuVendorText(g.Key.HardwareType, g.Key.HardwareName);
            var integrated = VendorProfile.IsIntegratedGpu(g.Key.HardwareType, g.Key.HardwareName);
            var hint = integrated
                ? vendor switch
                {
                    "Intel" => "这是 Intel 核显的常见情况。轻负载发热主要体现在 CPU 封装温度上，"
                               + "核显温度留空属正常，不影响归因",
                    "AMD" => "AMD APU / Radeon 核显通常不通过 LHM 暴露独立温度，"
                             + "CPU 封装温度仍能反映整机发热；核显温度留空属正常",
                    _ => "集成显卡有时不暴露温度传感器；轻负载发热主要体现在 CPU 封装温度上"
                }
                : $"{vendor} 独显通常能读取温度；读不到多半是驱动未装好，或被其他监控程序占用，"
                  + "建议检查显卡驱动后再自检";

            items.Add(new DiagItem(integrated ? DiagLevel.Info : DiagLevel.Warn, "硬件节点",
                $"{(integrated ? "核显" : "显卡")}（{g.Key.HardwareName}）未暴露温度传感器", hint));
        }

        return items;
    }
    private static IReadOnlyList<DiagItem> EvaluateTemperature(
        List<double> temps, int missingTemp, List<double> utils, List<double> tjmaxGaps,
        CpuVendor cpuVendor)
    {
        var items = new List<DiagItem>();

        if (missingTemp == EffectiveTicks)
        {
            items.Add(new DiagItem(DiagLevel.Error, "温度",
                $"连续 {EffectiveTicks} 次有效采样全部读不到 CPU 温度",
                "MSR 完全不可用。确认管理员权限 + 检查安全软件是否拦截 WinRing0 驱动"));
            return items;
        }

        if (missingTemp > 0)
            items.Add(new DiagItem(DiagLevel.Warn, "温度",
                $"{EffectiveTicks} 次有效采样中 {missingTemp} 次读不到 CPU 温度",
                "间歇性读取失败，常见于与其他监控程序争抢 MSR"));

        if (temps.Count == 0) return items;

        // 0℃ 不可能是运行中 CPU 的真实温度：既不能算进均值，也不能说成「可读」。
        // 与采样侧的主温度判据共用同一个零带，保证自检结论与落盘口径一致。
        var zeros = temps.Count(PrimaryTemperatureGuard.IsZeroReading);
        var valid = temps.Where(v => !PrimaryTemperatureGuard.IsZeroReading(v)).ToList();

        if (valid.Count == 0)
        {
            var platform = cpuVendor switch
            {
                CpuVendor.Amd => "AMD Ryzen 的 SMU 温度在当前 LHM 版本下未被支持，返回 0 而不是缺失",
                CpuVendor.Intel => "Intel MSR 温度链路返回 0，通常是绑定到了无效传感器",
                _ => "该平台的温度传感器未被 LHM 正确暴露"
            };
            items.Add(new DiagItem(DiagLevel.Error, "温度",
                $"连续 {temps.Count} 次有效采样的主温度均为 0℃",
                $"{platform}。本程序按不可用处理：不写 CSV、不参与空闲基线与 ΔT，"
                + "界面显示『温度不可用』。可在设置页换绑其他温度传感器（Tctl / Core Max 等）后重新自检；"
                + "若全部温度传感器都返回 0，可换用 LiteMonitor 同一时刻读数对照，确认是否为平台限制"));
            return items;
        }

        if (zeros > 0)
            items.Add(new DiagItem(DiagLevel.Warn, "温度",
                $"{EffectiveTicks} 次有效采样中 {zeros} 次主温度返回 0℃（已按不可用剔除）",
                "0℃ 不是有效的 CPU 温度。偶发出现多为争抢 MSR；持续出现请换绑其他温度传感器"));

        var min = valid.Min();
        var max = valid.Max();
        var mean = valid.Average();

        items.Add(new DiagItem(DiagLevel.Ok, "温度",
            $"CPU 温度可读：{mean:0.0}C（{min:0.0} ~ {max:0.0}C，传感器粒度 1C"
            + (zeros > 0 ? $"，已剔除 {zeros} 次 0℃" : "") + "）", ""));

        if (max - min > 3 && utils.Count > 0 && utils.Average() < 10)
            items.Add(new DiagItem(DiagLevel.Warn, "温度",
                $"静置状态下 {EffectiveTicks} 秒内波动 {max - min:0.0}C，幅度偏大",
                "传感器可能绑定到 EC 粗值或受干扰。建议与 LiteMonitor 同一时刻读数对比，"
                + "偏差超过 3C 说明两者口径不同"));

        // --- 降频余量：回答"离 thermal throttling 还有多远" ---
        if (tjmaxGaps.Count > 0)
        {
            var gap = tjmaxGaps.Min();
            items.Add(new DiagItem(DiagLevel.Ok, "降频余量",
                $"距 100℃ 降频线余量 {gap:0}C（当前温度 {max:0.0}C）",
                gap <= 0
                    ? "★已经触发降频。这说明温度不是『偏热』而是『已影响性能』，"
                      + "属于必须解决的散热问题"
                    : gap < 8
                        ? "余量不足 8C，轻负载下就可能撞上降频线，机器会明显卡顿"
                        : "降频风险较低"));
        }

        return items;
    }

    private static IReadOnlyList<DiagItem> EvaluatePower(List<double> powers, CpuVendor cpuVendor)
    {
        var items = new List<DiagItem>();
        if (powers.Count == 0)
        {
            var hint = cpuVendor switch
            {
                CpuVendor.Amd => "AMD 平台的封装功耗在部分机型上走 SMU，LHM 可能读不到；"
                                 + "缺失可接受，但无法区分『散热差』与『撞功耗墙』",
                CpuVendor.Intel => "该 Intel 平台通常可读；若确实缺失可接受，但归因能力会下降",
                _ => "无法区分『散热差』与『撞功耗墙』；若确实缺失可接受，但归因能力会下降"
            };
            items.Add(new DiagItem(DiagLevel.Warn, "功耗", "读不到 CPU 封装功耗", hint));
            return items;
        }

        var mean = powers.Average();
        if (mean <= 0.5)
        {
            var hint = cpuVendor switch
            {
                CpuVendor.Amd => "若主温度也恒为 0℃，基本可确认该 AMD 平台的 SMU 传感器未被 LHM 支持，"
                                 + "而不是权限或绑定问题；若温度正常，则更像功耗绑定有误",
                _ => "若主温度也异常低（<30C），基本可确认绑定错误"
            };
            items.Add(new DiagItem(DiagLevel.Warn, "功耗",
                $"CPU 封装功耗恒为 {mean:0.0} W", hint));
            return items;
        }

        items.Add(new DiagItem(DiagLevel.Ok, "功耗", $"CPU 封装功耗可读：{mean:0.0} W", ""));
        return items;
    }
    private static IReadOnlyList<DiagItem> EvaluateFan(List<double> fanRpm)
    {
        var items = new List<DiagItem>();
        if (fanRpm.Count == 0)
        {
            items.Add(new DiagItem(DiagLevel.Warn, "散热", "读不到风扇转速",
                "部分 HP EC 固件不暴露有效转速值。缺失时无法判断风扇轴承是否老化"));
            return items;
        }

        var rpm = fanRpm.Average();
        if (rpm <= 0.5)
            items.Add(new DiagItem(DiagLevel.Warn, "散热", "风扇转速恒为 0",
                "需区分两种情况：(a) EC 固件未暴露有效转速值；(b) 风扇确实未转。"
                + "若 CPU 温度同时偏高，更可能是 (a) 传感器无效而非风扇故障"));
        else
            items.Add(new DiagItem(DiagLevel.Ok, "散热",
                $"风扇转速可读：{rpm:0} RPM（静置状态）", ""));

        return items;
    }

    private static IReadOnlyList<DiagItem> EvaluateUtilization(
        List<double> utils, List<double> corePeaks, List<double> tjmaxGaps)
    {
        var items = new List<DiagItem>();

        items.Add(new DiagItem(DiagLevel.Info, "负载",
            $"静置基线: 总利用率 {Avg(utils):0.0}%、单核峰值 {Max(corePeaks):0.0}%",
            "这两个值构成后续负载分桶的参照点"));

        if (utils.Count == 0)
        {
            items.Add(new DiagItem(DiagLevel.Error, "负载", "读不到 CPU 总利用率",
                "伴随记录模式不可用：没有利用率都无法按负载分桶"));
        }
        else
        {
            items.Add(new DiagItem(DiagLevel.Ok, "负载",
                $"CPU 总利用率可读：静置 {Avg(utils):0.0}%、峰值 {Max(utils):0.0}%", ""));
        }

        if (corePeaks.Count == 0)
        {
            items.Add(new DiagItem(DiagLevel.Warn, "负载", "读不到单核峰值",
                "★将无法计算单核峰值。任务管理器显示 50% 极可能是 2 核跑满；"
                + "缺此项会把双核满载误判为中等负载，且无法复现你描述的症状"));
        }
        else
        {
            items.Add(new DiagItem(DiagLevel.Ok, "负载",
                $"单核峰值可读：静置 {Avg(corePeaks):0.0}%、峰值 {Max(corePeaks):0.0}%", ""));
        }

        return items;
    }

    private static double Avg(List<double> xs) =>
        xs.Count == 0 ? double.NaN : xs.Average();

    private static double Max(List<double> xs) =>
        xs.Count == 0 ? double.NaN : xs.Max();
}