namespace ThermalTrace.Model;

/// <summary>
/// 单个采样点（1Hz 一条宽表记录）。
/// 所有温度单位 ℃，功耗单位 W，转速 RPM，频率 MHz，利用率 %。
/// 数值缺失一律用 double.NaN —— 绝不用 0 代替，否则统计均值会被静默拉低。
/// </summary>
public sealed record Sample
{
    public required DateTimeOffset Timestamp { get; init; }
    public required int ElapsedSeconds { get; init; }

    /// <summary>采样瞬间是否接通交流电。DC 与 AC 的功耗墙不同，不可混同统计。</summary>
    public bool OnAcPower { get; init; } = true;

    // ---- 温度 ----
    /// <summary>CPU 封装温度（MSR 主口径，作为主指标）。</summary>
    public double CpuPackageC { get; init; } = double.NaN;
    /// <summary>Tdie / Tctl，取决于平台；缺失时回退到 CpuPackageC。</summary>
    public double CpuTdieC { get; init; } = double.NaN;
    /// <summary>单核最高温度，用于发现核心间散热不均。</summary>
    public double CpuCoreMaxC { get; init; } = double.NaN;
    /// <summary>全核平均温度。</summary>
    public double CpuCoreAvgC { get; init; } = double.NaN;
    /// <summary>
    /// 【实测】"Distance to TjMax"：距离 100℃ 降频线的余量（℃）。
    /// 这是回答"我会不会降频""离危险还有多远"最直接的指标——
    /// 90℃ 时它约为 10℃，降到个位数就意味着马上要降频。
    /// 注意它的方向与温度相反：<b>越小越危险</b>。
    /// </summary>
    public double TjMaxDistanceC { get; init; } = double.NaN;
    /// <summary>主 GPU 温度（默认绑独显）。</summary>
    public double GpuC { get; init; } = double.NaN;
    /// <summary>
    /// 第二 GPU 温度（核显）。
    /// 笔记本双显卡必须分别记录：轻负载时热量实际产生在核显上，
    /// 只记独显会系统性低估发热，导致"清灰无效"的错误结论。
    /// </summary>
    public double GpuC2 { get; init; } = double.NaN;
    /// <summary>主 GPU 负载（%）。与 <see cref="GpuC"/> 同一张卡（独显）。</summary>
    public double GpuLoadPct { get; init; } = double.NaN;
    /// <summary>第二 GPU 负载（%）。与 <see cref="GpuC2"/> 同一张卡（核显）。</summary>
    public double Gpu2LoadPct { get; init; } = double.NaN;
    /// <summary>NVMe/SSD 温度。</summary>
    public double SsdC { get; init; } = double.NaN;
    /// <summary>主板温度：最低稳定值 ≈ 环境温度 + 3~5℃，用作环境代理指标。</summary>
    public double MainboardC { get; init; } = double.NaN;

    // ---- 散热与功耗 ----
    public double FanRpm { get; init; } = double.NaN;
    /// <summary>CPU Package 实际功耗。区分"散热差"与"撞功耗墙"的决定性指标。</summary>
    public double PackagePowerW { get; init; } = double.NaN;
    /// <summary>CPU Package Clamp（等效 PL 上限）。与 PackagePowerW 比较可知是否被限。</summary>
    public double ClampW { get; init; } = double.NaN;
    public double CpuClockMhz { get; init; } = double.NaN;
    public double UtilPct { get; init; } = double.NaN;
    /// <summary>
    /// 单核最高利用率（%）。★伴随记录最关键的字段。
    /// 任务管理器显示"总计 50%"时，实际可能是 2 个核心各跑满（单核峰值 100%）。
    /// 这两种情况产生的热量与风扇需求完全不同，只记总利用率会把两者混为一谈。
    /// </summary>
    public double MaxCoreUtilPct { get; init; } = double.NaN;

    // ---- 派生量 ----
    /// <summary>相对本会话基线段最低温的温升，抵消环境温度差异，跨天比较的核心量。</summary>
    public double DeltaC { get; init; } = double.NaN;

    /// <summary>主指标温度：优先 Package，其次 Tdie，再次 CoreMax。</summary>
    public double PrimaryCpuC
    {
        get
        {
            if (!double.IsNaN(CpuPackageC)) return CpuPackageC;
            if (!double.IsNaN(CpuTdieC)) return CpuTdieC;
            if (!double.IsNaN(CpuCoreMaxC)) return CpuCoreMaxC;
            return double.NaN;
        }
    }

    /// <summary>
    /// 主 GPU 温度：优先独显（<see cref="GpuC"/>），独显没绑或读不到时取核显（<see cref="GpuC2"/>）。
    ///
    /// 【为什么不取两张卡的最大值】最大值会在两个传感器之间来回跳，而这个值是要落盘做
    /// 纵向对比的——口径随样本漂移，前后数字就不可比了，与"必须锁死主温度链路"是同一条纪律。
    /// 固定顺序取值：缺哪个由绑定决定，不随时间片抖动。
    /// </summary>
    public double PrimaryGpuC => !double.IsNaN(GpuC) ? GpuC : GpuC2;

    /// <summary>
    /// 主 GPU 负载：与 <see cref="PrimaryGpuC"/> 同一条优先级链（独显优先，读不到取核显）。
    ///
    /// 【为什么必须记负载】2026-10-16 之前的"GPU 使用时长"是"采到 GPU 温度即计入"，
    /// 而独显待机也报温度——那个数字等于开机时长，回答不了"到底用没用显卡"。
    /// 负载 > 0 才叫使用，与 LiteMonitor 的「0% / 非 0」是同一口径。
    /// 负载读不到时为 NaN（缺失），由聚合层决定该样本进不进档——绝不填 0。
    /// </summary>
    public double PrimaryGpuLoadPct => !double.IsNaN(GpuLoadPct) ? GpuLoadPct : Gpu2LoadPct;
}