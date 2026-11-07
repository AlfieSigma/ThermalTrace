using System.Text.Json.Serialization;

namespace ThermalTrace.Model;

/// <summary>
/// 传感器绑定表。把 LHM 的 <c>/intelcpu/0/temperature/0</c> 这类 Identifier
/// 固化到配置里，保证每次会话都读同一口径——否则跨会话对比毫无意义。
/// </summary>
public sealed class SensorBinding
{
    // ---- CPU 温度 ----
    public string CpuPackageId { get; set; } = string.Empty;
    public string CpuTdieId { get; set; } = string.Empty;
    public string CpuCoreMaxId { get; set; } = string.Empty;
    public string CpuCoreAvgId { get; set; } = string.Empty;

    // ---- CPU 功耗与频率 ----
    public string CpuPackagePowerId { get; set; } = string.Empty;
    public string CpuClampId { get; set; } = string.Empty;
    public string CpuClockId { get; set; } = string.Empty;
    public string CpuUtilId { get; set; } = string.Empty;

    /// <summary>
    /// 【实测】LHM 直接提供 "CPU Core Max"（/intelcpu/0/load/1），即所有核心中的最高利用率。
    /// 这是单核峰值的官方口径，比手工遍历逐核心更可靠，优先使用。
    /// </summary>
    public string CpuCoreMaxUtilId { get; set; } = string.Empty;

    /// <summary>
    /// 【实测】"CPU Core #N Distance to TjMax"（/intelcpu/0/temperature/7..10）。
    /// Intel 的降频余量指示器：接近 0 表示即将触发 thermal throttling。
    /// 对"90℃ 会不会降频"这类问题，比温度绝对值更直接。
    /// </summary>
    public string CpuDistanceToTjMaxId { get; set; } = string.Empty;

    // ---- 散热与环境 ----
    public string FanId { get; set; } = string.Empty;
    public string MainboardTempId { get; set; } = string.Empty;
    public string SsdTempId { get; set; } = string.Empty;
    public string GpuTempId { get; set; } = string.Empty;

    /// <summary>第二 GPU（核显）的温度传感器。双显卡必须分别记录。</summary>
    public string Gpu2TempId { get; set; } = string.Empty;

    /// <summary>GPU 利用率传感器：主 GPU / 核显 / 全部 LHM Load 传感器。</summary>
    public string GpuUtilId { get; set; } = string.Empty;
    public string Gpu2UtilId { get; set; } = string.Empty;

    /// <summary>
    /// 各物理核心的利用率传感器 Identifier，索引与物理核号对应。
    /// 用于计算单核峰值——伴随记录场景下最关键的指标。
    /// </summary>
    public List<string> CpuCoreUtilIds { get; set; } = new();

    /// <summary>绑定时记下传感器显示名，报告里展示用。</summary>
    public Dictionary<string, string> DisplayNames { get; set; } = new(StringComparer.Ordinal);

    /// <summary>主温度指标的取值顺序：Package → Tdie → CoreMax。</summary>
    [JsonIgnore]
    public IReadOnlyList<string> PrimaryTemperatureChain =>
        new[] { CpuPackageId, CpuTdieId, CpuCoreMaxId }.Where(s => !string.IsNullOrEmpty(s)).ToArray();

    [JsonIgnore]
    public string PrimarySensorName =>
        DisplayNames.TryGetValue(PrimaryTemperatureChain.FirstOrDefault() ?? string.Empty, out var n)
            ? n
            : "(未绑定)";

    /// <summary>返回缺失项描述；为空表示绑定可用于测量。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (PrimaryTemperatureChain.Count == 0)
            problems.Add("缺少 CPU 主温度传感器（Package/Tdie/CoreMax 至少绑定一个）");
        if (string.IsNullOrEmpty(CpuPackagePowerId))
            problems.Add("缺少 CPU Package 功耗传感器（无法区分散热差与功耗墙，建议绑定）");
        if (string.IsNullOrEmpty(FanId))
            problems.Add("缺少风扇转速传感器（无法判断风扇轴承是否老化，建议绑定）");
        return problems;
    }

    /// <summary>阻止降级的必需项（缺了就无法产出可信数据）。</summary>
    [JsonIgnore]
    public bool IsUsable => PrimaryTemperatureChain.Count > 0;

    [JsonIgnore]
    public bool IsComplete => Validate().Count == 0;

    /// <summary>该 Identifier 是否已被绑定到任何角色。--list-sensors 用它打星号。</summary>
    public bool IsBound(string identifier) =>
        !string.IsNullOrEmpty(identifier)
        && (AllFields.Contains(identifier) || CpuCoreUtilIds.Contains(identifier, StringComparer.OrdinalIgnoreCase));

    /// <summary>除各核心利用率之外的全部单值绑定字段。</summary>
    private IEnumerable<string> AllFields =>
    [
        CpuPackageId, CpuTdieId, CpuCoreMaxId, CpuCoreAvgId,
        CpuPackagePowerId, CpuClampId, CpuClockId, CpuUtilId,
        CpuCoreMaxUtilId, CpuDistanceToTjMaxId,
        FanId, MainboardTempId, SsdTempId, GpuTempId, Gpu2TempId,
        GpuUtilId, Gpu2UtilId
    ];
}

/// <summary>一次传感器枚举结果，供绑定向导与 --doctor 展示。</summary>
/// <remarks>
/// HardwareType 必须带上：CPU 的 HardwareName 通常也含 "Intel" 字样，
/// 若靠名称判定显卡会把它误认成核显——
/// 这会导致"核显温度"列实际记录的是 CPU 核心温度。
/// </remarks>
public sealed record SensorDescriptor(
    string Identifier,
    string Name,
    string HardwareName,
    string HardwareType,
    string SensorType,
    string? Unit,
    string? ValuePreview)
{
    /// <summary>是否来自独立/集成显卡硬件节点。</summary>
    public bool IsGpu =>
        // 【实测】LHM 0.9.6 的显卡节点 HardwareType 是 "GpuIntel" 与 "GpuNvidia"，
        // 并不存在 "gpu" 这个取值（写成 HardwareType.Gpu 会编译失败）。
        // 因此必须用前缀匹配，否则会漏掉全部显卡传感器。
        HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否来自 CPU 硬件节点。</summary>
    public bool IsCpu => HardwareType.Equals("Cpu", StringComparison.OrdinalIgnoreCase);
}

/// <summary>--doctor 的诊断结论。</summary>
public enum DiagLevel { Ok, Warn, Error, Info }

public sealed record DiagItem(DiagLevel Level, string Category, string Message, string? Hint = null);