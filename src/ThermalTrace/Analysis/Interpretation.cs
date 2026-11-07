namespace ThermalTrace.Analysis;

/// <summary>
/// 趋势解读的数据模型（纯数据，零逻辑、零 IO、零 UI 依赖）。
///
/// 【为什么独立成文件】解读结果要同时喂给三个展示位——趋势页卡片、
/// HTML 报告结论区、CLI <c>--interpret</c>——模型只有一份，
/// 三个展示位复用同一份 <see cref="InterpretationReport"/>，
/// 从结构上保证"三处同源同值"（三处同源同值的口径要求）。
///
/// 【缺失纪律（全项目铁律）】缺失一律 <c>null</c> / 「—」，绝不用 0 代替：
/// 0℃ 会被读成"凉快"，0 K/W 会被读成"效率零恶化"，都与"没采到"是两回事。
/// </summary>

/// <summary>
/// 健康度等级（用等级不用分数——分数诱导过度解读）。
/// 数值顺序即严重程度顺序，可直接比较大小取"最差"的一档。
/// </summary>
public enum HealthLevel
{
    /// <summary>数据不足：不给任何趋势判定，总结只讲"还差几天"。</summary>
    Insufficient,

    /// <summary>良好：同等负载下温升明显下降（ΔT 差 ≤ −0.5℃）。</summary>
    Good,

    /// <summary>平稳：变化在显著阈值以内。</summary>
    Stable,

    /// <summary>关注：出现值得留意的恶化信号（ΔT 差 ≥ +0.5℃ 等）。</summary>
    Watch,

    /// <summary>注意：恶化信号明确（ΔT 差 ≥ +1.0℃ 等）。</summary>
    Warning
}

/// <summary>健康度等级的中文展示名。卡片 chip、CLI、HTML 三处共用，避免各写各的。</summary>
public static class HealthLevelExtensions
{
    public static string Text(this HealthLevel level) => level switch
    {
        HealthLevel.Insufficient => "数据不足",
        HealthLevel.Good => "良好",
        HealthLevel.Stable => "平稳",
        HealthLevel.Watch => "关注",
        HealthLevel.Warning => "注意",
        _ => "未知"
    };
}

/// <summary>
/// 一条证据（描述层）：前后数值 + 变化量，不带任何判断。
///
/// 【字段口径】
///  - <see cref="Category"/>：负载类别名；跨类别汇总（超 85 累计）与环境气温为空串。
///  - <see cref="Metric"/>："ΔT" / "K/W" / "超85时长" / "斜率" / "环境气温" 之一。
///  - <see cref="Change"/>：带符号的变化量，如 "+1.2℃"；无变化量时为 ""。
///  - <see cref="Detail"/>：按模板排好方向的前后数值，如 "59.5℃ ← 58.3℃"；
///    缺失为 ""（由 <see cref="Text"/> 渲染为「—」，绝不填 0）。
/// </summary>
public sealed record EvidenceItem(string Category, string Metric, string Change, string Detail)
{
    /// <summary>
    /// 按模板拼出完整句子。做成计算属性而不是让三个展示位各拼一遍：
    /// 模板漂移等于三处结论打脸。数字与箭头方向由 <see cref="Interpreter"/> 填好。
    /// </summary>
    public string Text => Metric switch
    {
        "ΔT" => $"{Prefix}ΔT {Or(Detail)}（{Change}）",
        "K/W" => $"{Prefix}每瓦温升 {Or(Detail)} K/W（{Change}）",
        "超85时长" => $"超 85℃ 累计 {Or(Detail)}",
        "斜率" => $"{Prefix}ΔT 斜率 {Change}（{Detail}）",
        "环境气温" => $"环境气温 {Or(Detail)}",
        // GPU 使用时长：ΔT 上升时的混杂因素证据（显卡与 CPU 共用散热模组）。
        // 它是事实不是判断——时长没涨同样要显示，那是在帮读者排除这条干扰。
        "GPU使用" => $"GPU 使用时长 {Or(Detail)}（{Change}）",
        // 单窗口（自上次清灰）用的两种证据：清灰窗口本身、以及各负载档的汇总。
        // 单窗口没有"上期"可比，前后数值取"清灰后首日 → 最近一天"。
        "清灰" => $"清灰基准 {Or(Detail)}",
        "档位" => $"{Prefix}{Detail}",
        _ => $"{Prefix}{Metric} {Or(Detail)}（{Change}）"
    };

    private string Prefix => Category.Length > 0 ? $"「{Category}」" : "";

    private static string Or(string s) => s.Length == 0 ? "—" : s;
}

/// <summary>
/// 一条归因（假设层）：假设 + 支撑证据 + 置信度 + 验证方法。
///
/// 【诚实性硬约束】归因是假设不是结论，必须可证伪：
/// <see cref="Verification"/> 为 null 的归因不允许输出（由 <see cref="Interpreter"/> 保证）。
/// </summary>
public sealed record AttributionItem(string Hypothesis, string Support, string Confidence, string? Verification)
{
    /// <summary>一行式文本（CLI / HTML 用）。卡片按字段分区渲染，不用这个。</summary>
    public string Text
    {
        get
        {
            var s = Hypothesis;
            if (Confidence.Length > 0) s += $"（置信度：{Confidence}）";
            if (Support.Length > 0) s += $"，依据：{Support}";
            if (Verification is { Length: > 0 }) s += $"；验证：{Verification}";
            return s;
        }
    }
}

/// <summary>
/// 清灰基准层：距上次清灰以来主类别 ΔT 的变化。
/// 它是"基准日事实"，不是归因——口径变更窗口内也照常渲染（验收标准第 4 条）。
/// </summary>
public sealed record CleanServiceStat(
    string? CleanDateIso, int? DaysSinceClean,
    double? DeltaTAfterClean, double? DeltaTNow, double? Rise,
    double? RisePerMonth, string Verdict,
    double? PeakTemp = null, string? PeakDateIso = null, string? PeakCategory = null,
    string? RiseText = null)
{
    /// <summary>完整展示句（CLI / HTML / 卡片共用）。缺失数字渲染为「—」，不填 0。</summary>
    public string Text
    {
        get
        {
            if (CleanDateIso is null) return Verdict;
            var days = DaysSinceClean is { } d ? $"{d} 天前" : "日期待核";
            var riseText = RiseText ?? $"上升 {F(Rise)}℃";
            var core = $"基准日 {CleanDateIso}（{days}），之后 ΔT {F(DeltaTAfterClean)}℃ → {F(DeltaTNow)}℃，"
                + $"{riseText}{PerMonth}；判定：{Verdict}";
            if (PeakTemp is not { } peak) return core;

            // 单窗口解读（G8 起）：清灰这段时间的最高温与最热的一天。
            var where = PeakDateIso is { Length: > 0 } date
                ? $"（{date}{(PeakCategory is { Length: > 0 } cat ? $"，{cat}" : "")}）"
                : "";
            return core + $"；区间最高温 {peak:0.0}℃{where}";
        }
    }

    /// <summary>不足 30 天不外推月均：数字给 null，文案层就不会出现"约…仅供参考"。</summary>
    private string PerMonth => RisePerMonth is { } rpm ? $"（约 {rpm:0.0}℃/月，仅供参考）" : "";

    private static string F(double? v) => v is null ? "—" : v.Value.ToString("0.0");
}

/// <summary>
/// 一份完整的趋势解读。所有数字来自同一份 <see cref="TrendReport"/>，
/// 由 <see cref="Interpreter.Build"/> 一次算出，三个展示位直接取用。
/// </summary>
public sealed record InterpretationReport(
    HealthLevel Health,
    string Summary,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<AttributionItem> Attributions,
    IReadOnlyList<string> Suggestions,
    CleanServiceStat? Clean,
    string ConfidenceNote,
    string AiDigest);
