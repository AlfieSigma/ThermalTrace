namespace ThermalTrace.Model;

/// <summary>
/// 同一语义在 GUI 与 HTML 报告各自的实体色值。
/// 语义名共用，视觉不强行统一——两套界面本来就是两种观感。
/// </summary>
public sealed record ToneColors(string GuiLight, string GuiDark, string Report);

/// <summary>
/// 语义 → 颜色的唯一登记处（G12）。
///
/// GUI 的 Cool/Warm/Hot 是温度与健康度语义，读 <see cref="ToneColors.GuiLight"/> /
/// <see cref="ToneColors.GuiDark"/>；HTML 报告读 <see cref="ToneColors.Report"/>。
/// 报告调色板不允许反过来定义 GUI 的冷/温/热，否则一次"统一配色"就会让
/// 趋势页的温度语义变味。
/// </summary>
public static class VisualTones
{
    public static readonly ToneColors Accent = new("#0078D4", "#4CC2FF", "#2f7c78");
    public static readonly ToneColors Hot = new("#C4314B", "#FF6B7A", "#c0504d");
    public static readonly ToneColors Warm = new("#D98324", "#F0A93C", "#d98a2b");
    public static readonly ToneColors Cool = new("#2E8B84", "#4EC9B0", "#2f7c78");

    /// <summary>HTML「关注」徽标沿用既有深橙；与折线暖色不是同一个值，按报告原样保留。</summary>
    public const string ReportWatchBadge = "#d9832b";

    /// <summary>HTML「数据不足」徽标灰。</summary>
    public const string ReportNeutral = "#9aa1ab";

    /// <summary>HTML 类别折线的辅助色（蓝 / 紫 / 绿，不承载温度语义）。</summary>
    public const string ReportSeriesBlue = "#3f6db5";
    public const string ReportSeriesPurple = "#7a6aa0";
    public const string ReportSeriesGreen = "#5a8f3c";

    /// <summary>HTML 报告类别折线调色板：顺序即类别下标的取色顺序。</summary>
    public static readonly string[] ReportSeries =
        [Accent.Report, Warm.Report, ReportSeriesBlue, Hot.Report, ReportSeriesPurple, ReportSeriesGreen];
}
