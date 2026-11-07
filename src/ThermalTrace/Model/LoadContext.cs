namespace ThermalTrace.Model;

/// <summary>
/// 负载档位（按总 CPU 利用率划分）。
/// 划分依据是伴随记录场景下的真实使用形态，而不是实验室工况：
/// 空闲 / 轻度 / 中等(编译、解压) / 重度(编译、转码) / 满载(渲染、游戏)。
/// </summary>
public enum WorkloadClass
{
    /// <summary>&lt;8%：真正的空闲。温度反映散热模组的被动散热能力。</summary>
    Idle,

    /// <summary>8~35%：轻度（浏览器、文档、IDE 编辑）。</summary>
    Light,

    /// <summary>35~70%：中等负载（增量编译、解压、部分转码）。</summary>
    Moderate,

    /// <summary>70~92%：重度（多项目编译、视频导出）。</summary>
    Heavy,

    /// <summary>&gt;92%：满载（游戏、渲染、全量编译）。</summary>
    Full
}

public static class WorkloadClassExtensions
{
    public static string DisplayName(this WorkloadClass c) => c switch
    {
        WorkloadClass.Idle => "空闲",
        WorkloadClass.Light => "轻度",
        WorkloadClass.Moderate => "中等",
        WorkloadClass.Heavy => "重度",
        WorkloadClass.Full => "满载",
        _ => "未知"
    };

    /// <summary>分析时用于分桶的键，保证跨会话一致。</summary>
    public static string BucketKey(this WorkloadClass c) => c.ToString();

    public static WorkloadClass FromUtilization(double totalUtilPct)
    {
        if (double.IsNaN(totalUtilPct)) return WorkloadClass.Idle;
        return totalUtilPct switch
        {
            < 8 => WorkloadClass.Idle,
            < 35 => WorkloadClass.Light,
            < 70 => WorkloadClass.Moderate,
            < 92 => WorkloadClass.Heavy,
            _ => WorkloadClass.Full
        };
    }
}

/// <summary>
/// 采样瞬间的负载上下文。伴随记录模式下，<b>上下文是解释温度的唯一依据</b>——
/// 脱离负载谈温度没有意义，所以它必须与温度同时刻落盘。
/// </summary>
public sealed record LoadContext(
    WorkloadClass Class,
    /// <summary>全 CPU 总利用率（%），对应任务管理器"总计"列。</summary>
    double TotalCpuUtilPct,
    /// <summary>
    /// 单个物理核心的最高利用率（%）。★伴随记录最关键的字段。
    /// 4 核 8 线程的 Intel CPU 上，任务管理器显示 50% 极可能是 2 核跑满。
    /// 只记总利用率会把"双核满载"误判为"中等负载"，清灰效果永远解释不清。
    /// </summary>
    double MaxCoreUtilPct,
    /// <summary>各物理核心利用率快照（用于识别"N 核并行"形态）。</summary>
    IReadOnlyList<double> PerCoreUtilPct,
    /// <summary>主 GPU 利用率（%）。NaN 表示未绑定。</summary>
    double GpuUtilPct,
    /// <summary>核显利用率（%）。轻负载发热常常来自这里。</summary>
    double Gpu2UtilPct,
    /// <summary>是否接通交流电。DC 供电时功耗墙更紧，跨状态不可比。</summary>
    bool OnAcPower,
    /// <summary>电池电量（%），仅 DC 时有意义。</summary>
    double BatteryPct)
{
    public static LoadContext Empty { get; } = new(
        WorkloadClass.Idle, double.NaN, double.NaN, Array.Empty<double>(),
        double.NaN, double.NaN, true, double.NaN);

    /// <summary>该上下文是否可用于"同工况对比"（要求利用率数据完整）。</summary>
    public bool IsComparable =>
        !double.IsNaN(TotalCpuUtilPct) && !double.IsNaN(MaxCoreUtilPct);
}

/// <summary>
/// 伴随记录时需要记录的环境 / 系统事件。事件会打断连续时间序列，
/// 分析时必须在这些点断开，否则会把"合盖睡眠后的瞬时升温"当成连续趋势。
/// </summary>
public enum SessionEventKind
{
    /// <summary>会话开始</summary>
    Start,

    /// <summary>从睡眠 / 休眠恢复（此前的数据序列不可连续比较）</summary>
    Resume,

    /// <summary>进入睡眠</summary>
    Suspend,

    /// <summary>电源适配器接通</summary>
    AcConnected,

    /// <summary>电源适配器断开</summary>
    AcDisconnected,

    /// <summary>用户手动打标记（如"开始剪视频"）</summary>
    Marker,

    /// <summary>探测到其他硬件监控程序在运行，可能争抢 MSR</summary>
    ForeignToolDetected
}

public sealed record SessionEvent(DateTimeOffset At, SessionEventKind Kind, string? Note = null);