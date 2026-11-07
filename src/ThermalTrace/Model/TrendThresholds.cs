namespace ThermalTrace.Model;

/// <summary>
/// 趋势结论的阈值与换算系数唯一来源（G12）。
///
/// GUI 卡片、--trend 报告与 --interpret 三处共用这一份数值：
/// 改阈值只改这里，同一个负载类别不会在 GUI 说"恶化"、在报告说"持平"。
/// </summary>
public static class TrendThresholds
{
    /// <summary>超温告警阈值（℃）：落盘的超阈计数与解读的"触 85℃"判定共用。</summary>
    public const int OverTempWarnC = 85;

    /// <summary>超温危险阈值（℃）：区间峰值达到此值判 <see cref="HealthLevel.Warning"/>。</summary>
    public const int OverTempCriticalC = 90;

    /// <summary>ΔT 显著阈值：0.5℃。</summary>
    public const double DeltaTSignificant = 0.5;

    /// <summary>K/W 显著阈值：0.05。</summary>
    public const double KPerWSignificant = 0.05;

    /// <summary>超 85℃ 时长显著阈值：0.1 小时。</summary>
    public const double Over85Significant = 0.1;

    /// <summary>斜率有效阈值：0.02℃/天（点数 ≥ 3 由 <see cref="TrendSlope.HasTrend"/> 保证）。</summary>
    public const double SlopeSignificant = 0.02;

    /// <summary>健康度判定所需的最少数据天数。</summary>
    public const int MinDaysForJudgment = 14;

    /// <summary>单窗口解读（自上次清灰）的弱结论门槛：不足 7 天仍给解读，但标明仅供参考。</summary>
    public const int MinDaysForWeakConclusion = 7;

    /// <summary>ΔT 差达到此值直接判 <see cref="HealthLevel.Warning"/>。</summary>
    public const double DeltaTWarn = 1.0;

    /// <summary>K/W 差达到此值参与健康度强化/改善。</summary>
    public const double KPerWEscalate = 0.10;

    /// <summary>超 85℃ 时长差达到此值至少 <see cref="HealthLevel.Watch"/>。</summary>
    public const double Over85WatchHours = 1.0;

    /// <summary>距清灰 ΔT 上升达到此值至少 <see cref="HealthLevel.Watch"/>。</summary>
    public const double CleanRiseWatch = 1.5;

    /// <summary>距清灰天数门槛：衰减判定用 &gt; 30 天，月均外推展示用 ≥ 30 天。</summary>
    public const int CleanWatchDays = 30;

    /// <summary>环境气温本期比上期高此值才入证据 / 触发 A3 归因。</summary>
    public const double AmbientRise = 2.0;

    /// <summary>重载类别时长占比上升此百分点触发 A4（计划未给数值，取 5 个百分点并在此备案）。</summary>
    public const double HeavyShareRise = 0.05;

    /// <summary>GPU 使用时长本期比上期高此值触发 A7 归因（3.4）。</summary>
    public const double GpuHoursRise = 0.5;

    /// <summary>证据条数上限。</summary>
    public const int MaxEvidence = 4;

    /// <summary>建议条数上限。</summary>
    public const int MaxSuggestions = 4;

    /// <summary>清灰衰减判定的轻微档：Rise ≥ 0.5℃。</summary>
    public const double CleanRiseSlight = 0.5;

    /// <summary>月度外推的每月天数：与日历月无关，固定 30 天。</summary>
    public const double DaysPerMonth = 30.0;
}
