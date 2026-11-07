using ThermalTrace.Model;

namespace ThermalTrace.Hardware;

/// <summary>
/// 传感器自动绑定与校验。
///
/// 为什么必须自动：在 4 核 8 线程的 Intel CPU 上，LHM 通常暴露 "CPU Package"、"CPU Package (Tdie/Tctl)"、
/// "Core Max"、"Core Average"、"CPU Package Power"、"CPU Package Clamp"、"Core #N Clock"、
/// "CPU Total" 等十余个条目，而且每台机器、每个显卡驱动版本暴露的集合都不一样。
/// 手选一次太痛苦，换显卡驱动后 Identifier 变化还会读到 null。
///
/// 策略：按优先级做名称启发式匹配，并把推断结果展示给用户确认，而不是默默选一个。
/// </summary>
internal static class SensorCatalog
{
    /// <summary>一个绑定角色的候选与最终选择。</summary>
    public sealed record Role(string Key, string Label, string Description,
                              IReadOnlyList<SensorDescriptor> Candidates, string? ChosenId);

    public sealed record BindResult(SensorBinding Binding, IReadOnlyList<Role> Roles);

    /// <summary>
    /// 执行自动绑定。温度优先级 Package &gt; Tdie/Tctl &gt; Core Max；
    /// 显卡以独显（NVIDIA/AMD）为主 GPU，Intel 核显为第二 GPU。
    /// </summary>
    public static BindResult AutoBind(IReadOnlyList<SensorDescriptor> sensors)
    {
        var b = new SensorBinding();

        // ---- CPU 温度 ----
        var cpuTemps = Of(sensors, "Temperature", isCpu: true);
        b.CpuPackageId = Pick(cpuTemps, n =>
            n.Contains("package", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("tdie", StringComparison.OrdinalIgnoreCase))?.Identifier ?? "";

        b.CpuTdieId = Pick(cpuTemps, n =>
            n.Contains("tdie", StringComparison.OrdinalIgnoreCase)
            || n.Contains("tctl", StringComparison.OrdinalIgnoreCase))?.Identifier ?? "";

        b.CpuCoreMaxId = Pick(cpuTemps, n =>
            n.Contains("core max", StringComparison.OrdinalIgnoreCase))?.Identifier ?? "";

        b.CpuCoreAvgId = Pick(cpuTemps, n =>
            n.Contains("core avg", StringComparison.OrdinalIgnoreCase))?.Identifier ?? "";

        // ---- CPU 功耗 ----
        var cpuPowers = Of(sensors, "Power", isCpu: true);
        b.CpuPackagePowerId = Pick(cpuPowers, n =>
            n.Contains("package", StringComparison.OrdinalIgnoreCase))?.Identifier ?? "";

        b.CpuClampId = Pick(cpuPowers, n =>
            n.Contains("clamp", StringComparison.OrdinalIgnoreCase))?.Identifier
            ?? Pick(cpuPowers, n =>
                n.Contains("total", StringComparison.OrdinalIgnoreCase))?.Identifier ?? "";

        // ---- CPU 频率 ----
        var cpuClocks = Of(sensors, "Clock", isCpu: true);
        b.CpuClockId = Pick(cpuClocks, n =>
                n.Contains("#0", StringComparison.Ordinal)
                || n.Contains("core 0", StringComparison.OrdinalIgnoreCase))?.Identifier
            ?? cpuClocks.FirstOrDefault()?.Identifier ?? "";

        // ---- CPU 利用率 ----
        var cpuLoads = Of(sensors, "Load", isCpu: true);
        b.CpuUtilId = Pick(cpuLoads, n =>
                n.Contains("total", StringComparison.OrdinalIgnoreCase)
                || n.Contains("overall", StringComparison.OrdinalIgnoreCase))?.Identifier
            ?? cpuLoads.FirstOrDefault()?.Identifier ?? "";

        // 【实测】"CPU Core Max" 是现成的单核峰值，优先用它
        b.CpuCoreMaxUtilId = Pick(cpuLoads, n =>
            n.Contains("core max", StringComparison.OrdinalIgnoreCase))?.Identifier ?? "";

        // 逐核心/逐线程利用率：作为"CPU Core Max"缺失时的兜底
        b.CpuCoreUtilIds = cpuLoads
            .Where(s => CoreIndexOf(s.Name) is not null)
            .GroupBy(s => CoreIndexOf(s.Name)!.Value)
            .OrderBy(g => g.Key)
            .Select(g => g.First().Identifier)
            .ToList();

        // 【实测】"Distance to TjMax"：Intel 降频余量指示器
        b.CpuDistanceToTjMaxId = Pick(
            Of(sensors, "Temperature", isCpu: true),
            n => n.Contains("tjmax", StringComparison.OrdinalIgnoreCase))?.Identifier ?? "";

        // ---- 风扇 / 主板 / SSD ----
        b.FanId = Of(sensors, "Fan", any: true).FirstOrDefault()?.Identifier ?? "";
        b.MainboardTempId = Of(sensors, "Temperature", isMotherboard: true).FirstOrDefault()?.Identifier ?? "";
        b.SsdTempId = Of(sensors, "Temperature", isStorage: true).FirstOrDefault()?.Identifier ?? "";

        // ---- 双 GPU：独显为主，Intel 核显为第二 ----
        var gpuTemps = Of(sensors, "Temperature", isGpu: true);
        var mainGpu = gpuTemps.FirstOrDefault(s => IsDiscreteGpu(s.HardwareName))
                      ?? gpuTemps.FirstOrDefault(s => s.HardwareName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                      ?? gpuTemps.FirstOrDefault();

        SensorDescriptor? secondGpu = null;
        if (mainGpu is not null)
        {
            bool mainIsIntegrated = mainGpu.HardwareName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
            secondGpu = mainIsIntegrated
                ? gpuTemps.FirstOrDefault(s => s.HardwareName != mainGpu.HardwareName)
                : gpuTemps.FirstOrDefault(s => s.HardwareName.Contains("Intel", StringComparison.OrdinalIgnoreCase));
        }

        b.GpuTempId = mainGpu?.Identifier ?? "";
        b.Gpu2TempId = secondGpu?.Identifier ?? "";

        var gpuLoads = Of(sensors, "Load", isGpu: true);
        b.GpuUtilId = MatchGpu(gpuLoads, mainGpu?.HardwareName)?.Identifier ?? "";
        b.Gpu2UtilId = MatchGpu(gpuLoads, secondGpu?.HardwareName)?.Identifier ?? "";

        foreach (var d in sensors)
            if (!string.IsNullOrEmpty(d.Identifier))
                b.DisplayNames[d.Identifier] = $"{d.HardwareName} / {d.Name}";

        return new BindResult(b, BuildRoles(sensors, b));
    }

    private static bool IsDiscreteGpu(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);

    private static SensorDescriptor? MatchGpu(IReadOnlyList<SensorDescriptor> loads, string? hardwareName)
    {
        if (string.IsNullOrEmpty(hardwareName)) return null;
        return loads.FirstOrDefault(l => l.HardwareName.Equals(hardwareName, StringComparison.OrdinalIgnoreCase))
            ?? loads.FirstOrDefault();
    }

    private static IReadOnlyList<Role> BuildRoles(
        IReadOnlyList<SensorDescriptor> s, SensorBinding b) =>
    [
        new Role("CpuPackageId", "CPU 封装温度", "主指标，Intel MSR 直读的最准口径",
            Of(s, "Temperature", isCpu: true).Where(x => IsPackageLike(x.Name)).ToList(), b.CpuPackageId),
        new Role("CpuTdieId", "CPU Tdie/Tctl", "Intel 平台顶盖温度，可能与 Package 差几度",
            Of(s, "Temperature", isCpu: true).Where(x => x.Identifier == b.CpuTdieId).ToList(), b.CpuTdieId),
        new Role("CpuPackagePowerId", "CPU 封装功耗", "★区分『散热差』与『撞功耗墙』的决定性指标",
            Of(s, "Power", isCpu: true), b.CpuPackagePowerId),
        new Role("CpuClampId", "CPU 功耗上限 Clamp", "与实际功耗比较可知是否被限；缺失时无法区分限功率",
            Of(s, "Power", isCpu: true).Where(x => x.Name.Contains("clamp", StringComparison.OrdinalIgnoreCase)).ToList(),
            b.CpuClampId),
        new Role("CpuUtilId", "CPU 总利用率", "任务管理器『总计』列，是负载分桶的基准",
            Of(s, "Load", isCpu: true).Where(x => CoreIndexOf(x.Name) is null).ToList(), b.CpuUtilId),
        new Role("CpuCoreMaxUtilId", "单核峰值利用率",
            "★伴随记录最关键字段。CPU 全部核心中的最高利用率，"
            + "用来识别『任务管理器显示 50% 但实际 2 核跑满』的情况",
            Of(s, "Load", isCpu: true).Where(x => x.Name.Contains("max", StringComparison.OrdinalIgnoreCase)).ToList(),
            b.CpuCoreMaxUtilId),
        new Role("CpuDistanceToTjMaxId", "距降频线余量",
            "Distance to TjMax：距 100℃ thermal throttling 线的余量，越小越危险",
            Of(s, "Temperature", isCpu: true).Where(x => x.Name.Contains("tjmax", StringComparison.OrdinalIgnoreCase)).ToList(),
            b.CpuDistanceToTjMaxId),
        new Role("CpuCoreUtilIds", "各核心利用率",
            $"CPU Core Max 缺失时的兜底（已自动匹配 {b.CpuCoreUtilIds.Count} 个）",
            Of(s, "Load", isCpu: true).Where(x => CoreIndexOf(x.Name) is not null).ToList(),
            b.CpuCoreUtilIds.Count > 0 ? b.CpuCoreUtilIds[0] : ""),
        new Role("FanId", "风扇转速", "同温度下转速偏低 → 风扇轴承可能老化",
            Of(s, "Fan", any: true), b.FanId),
        new Role("MainboardTempId", "主板温度", "最低稳定值 ≈ 环境温度 + 3~5℃，用作环境代理",
            Of(s, "Temperature", isMotherboard: true), b.MainboardTempId),
        new Role("GpuTempId", "主 GPU 温度", "默认独显",
            Of(s, "Temperature", isGpu: true), b.GpuTempId),
        new Role("Gpu2TempId", "核显温度", "轻负载发热常来自核显；与独显分开记录才不失真",
            Of(s, "Temperature", isGpu: true), b.Gpu2TempId)
    ];

    private static bool IsPackageLike(string name) =>
        name.Contains("package", StringComparison.OrdinalIgnoreCase)
        || name.Contains("core max", StringComparison.OrdinalIgnoreCase);

    // ----------------筛选辅助 ----------------

    /// <summary>
    /// 从传感器名里解析核心序号。
    /// 【实测】逐线程利用率通常命名为 "CPU Core #1 Thread #2"，
    /// 直接取 '#' 后整段会因" 1 Thread #2"而解析失败；
    /// 必须只取'#' 紧跟的连续数字。同时兼容 "Core 0" / "CPU Core 0" 等写法。
    /// 返回 null 表示不是逐核心传感器（如 "CPU Total"、"CPU Core Max"）。
    /// </summary>
    private static int? CoreIndexOf(string name)
    {
        if (!name.Contains("core", StringComparison.OrdinalIgnoreCase)) return null;

        var hash = name.IndexOf('#');
        if (hash >= 0)
        {
            // 注意：TakeWhile 只能用于 IEnumerable<char>，不能直接用于 ReadOnlySpan<char>
            var digits = new string(name[(hash + 1)..].TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var n1)) return n1;
        }

        var last = name.LastIndexOf("core", StringComparison.OrdinalIgnoreCase);
        if (last < 0) return null;
        var tail = name[(last + 4)..];
        var num = new string(tail.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(num, out var n2) ? n2 : null;
    }

    private static SensorDescriptor? Pick(
        IReadOnlyList<SensorDescriptor> pool, Func<string, bool> nameMatch) =>
        pool.FirstOrDefault(s => nameMatch(s.Name));

    internal static IReadOnlyList<SensorDescriptor> Of(
        IReadOnlyList<SensorDescriptor> all, string type,
        bool isCpu = false, bool isGpu = false,
        bool isMotherboard = false, bool isStorage = false, bool any = false)
    {
        var result = new List<SensorDescriptor>();
        foreach (var s in all)
        {
            if (!s.SensorType.Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
            if (any) { result.Add(s); continue; }

            // 【实测经验】必须按 HardwareType 判定硬件类别，绝不能靠 HardwareName 关键词。
            // CPU 的 HardwareName 常含 "Intel" 字样（例如 "11th Gen Intel(R) Core(TM) i5"），
            // 含 "Intel"——若用名称匹配判定显卡，会把 CPU 的 Core Max 温度
            // 当成"核显温度"，污染整套数据。
            if (isCpu && s.IsCpu) result.Add(s);
            else if (isGpu && s.IsGpu) result.Add(s);
            else if (isMotherboard && IsMotherboard(s.HardwareType, s.HardwareName)) result.Add(s);
            else if (isStorage && IsStorage(s.HardwareType, s.HardwareName)) result.Add(s);
        }
        return result;
    }

    private static bool IsMotherboard(string hwType, string hwName) =>
        hwType.Equals("motherboard", StringComparison.OrdinalIgnoreCase)
        || hwType.Equals("superIO", StringComparison.OrdinalIgnoreCase)
        || hwName.Contains("mainboard", StringComparison.OrdinalIgnoreCase);

    private static bool IsStorage(string hwType, string hwName) =>
        hwType.Equals("storage", StringComparison.OrdinalIgnoreCase)
        || hwName.Contains("nvme", StringComparison.OrdinalIgnoreCase)
        || hwName.Contains("ssd", StringComparison.OrdinalIgnoreCase);

    // ---------------- 校验 ----------------

    /// <summary>
    /// 校验绑定与传感器集合，输出分级诊断项。
    /// 关键：区分「阻断级」（不能测量）与「降级级」（能测但归因能力下降）。
    /// </summary>
    public static IReadOnlyList<DiagItem> Validate(IReadOnlyList<SensorDescriptor> sensors, SensorBinding binding)
    {
        var items = new List<DiagItem>();
        var temps = Of(sensors, "Temperature", any: true);
        var powers = Of(sensors, "Power", any: true);
        var fans = Of(sensors, "Fan", any: true);
        var loads = Of(sensors, "Load", any: true);

        if (!binding.IsUsable)
        {
            items.Add(new DiagItem(DiagLevel.Error, "温度", "未绑定任何 CPU 主温度传感器",
                "点『重新绑定传感器』重新绑定；若仍失败说明 MSR 不可读，需管理员权限且未被安全软件拦截（见 README 的常见问题）"));
            return items;
        }

        var mainId = binding.PrimaryTemperatureChain.First();
        var main = temps.FirstOrDefault(s => s.Identifier == mainId);
        if (main is not null)
        {
            if (string.IsNullOrEmpty(main.ValuePreview) || main.ValuePreview == "—")
                items.Add(new DiagItem(DiagLevel.Error, "温度",
                    $"主温度传感器『{main.Name}』当前无读数",
                    "MSR 读取失败，检查管理员权限与安全软件拦截记录"));
            else if (double.TryParse(main.ValuePreview, out var v) && (v <= 0 || v > 120))
                items.Add(new DiagItem(DiagLevel.Warn, "温度",
                    $"主温度读数 {main.ValuePreview}℃ 超出合理范围",
                    "可能绑定到非温度传感器或 EC 数据异常，建议点『重新绑定传感器』"));
        }

        if (binding.CpuPackagePowerId.Length == 0)
            items.Add(new DiagItem(DiagLevel.Warn, "功耗",
                $"未找到 CPU 封装功耗传感器（当前可见 {powers.Count} 个 Power 传感器）",
                "缺少功耗数据时，温度高无法区分『散热不足』与『撞上功耗墙』"));
        if (binding.CpuClampId.Length == 0)
            items.Add(new DiagItem(DiagLevel.Warn, "功耗", "未找到功耗上限（Clamp）传感器",
                "该传感器在部分 Intel 核显固件上不暴露；缺失时改用 MaxPower 近似判断"));

        if (fans.Count == 0)
            items.Add(new DiagItem(DiagLevel.Warn, "散热", "未检测到风扇转速传感器",
                "部分笔记本 EC 固件不暴露转速；缺失时无法判断风扇轴承是否老化"));
        else if (binding.FanId.Length == 0)
            items.Add(new DiagItem(DiagLevel.Warn, "散热", "风扇传感器存在但未绑定",
                "可在『今日概览』或『设置』页点『重新绑定传感器』"));

        if (binding.CpuUtilId.Length == 0)
            items.Add(new DiagItem(DiagLevel.Error, "负载", "未绑定 CPU 总利用率传感器",
                "没有利用率就无法按负载分桶，温度将失去意义（伴随记录模式不可用）"));
        if (binding.CpuCoreMaxUtilId.Length == 0 && binding.CpuCoreUtilIds.Count == 0)
            items.Add(new DiagItem(DiagLevel.Warn, "负载",
                "未找到单核峰值传感器（CPU Core Max）与各核心利用率",
                "★将无法计算单核峰值。任务管理器显示 50% 极可能是 2 核跑满；"
                + "缺此项会把双核满载误判为中等负载，且无法复现你描述的症状"));

        if (binding.CpuDistanceToTjMaxId.Length == 0)
            items.Add(new DiagItem(DiagLevel.Info, "温度", "未找到 Distance to TjMax 传感器",
                "该指标表示距 100℃ 降频线的余量，缺失时只能靠温度绝对值间接判断降频风险"));

        if (binding.GpuTempId.Length == 0 && binding.Gpu2TempId.Length == 0)
            items.Add(new DiagItem(DiagLevel.Info, "显卡", "未找到 GPU 温度传感器",
                "集成显卡有时不暴露温度传感器"));
        else if (binding.GpuTempId.Length > 0 && binding.Gpu2TempId.Length > 0)
            items.Add(new DiagItem(DiagLevel.Info, "显卡",
                $"已分别绑定两块 GPU：{LookName(binding, binding.GpuTempId)} / {LookName(binding, binding.Gpu2TempId)}",
                "轻负载发热主要来自核显，两块分开记录才不失真"));

        items.Add(new DiagItem(DiagLevel.Info, "传感器",
            $"可见传感器 {sensors.Count} 个：温度 {temps.Count}、功耗 {powers.Count}、" +
            $"转速 {fans.Count}、利用率 {loads.Count}", ""));

        return items;
    }

    private static string LookName(SensorBinding b, string id) =>
        b.DisplayNames.TryGetValue(id, out var n) ? n : id;
}