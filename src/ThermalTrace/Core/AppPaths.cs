namespace ThermalTrace.Core;

/// <summary>
/// 数据目录布局。核心原则：<b>数据量必须小到用户不在意</b>。
/// 每个时间片每档一行，一天约 100~200 行。2026-10-16 加了 GPU 负载分档的 11 列后
/// 一行从 17 列涨到 28 列、约 35 KB/天，一年约 12 MB（此前 17 列时是 20 KB/天、约 7 MB）。
/// </summary>
internal static class AppPaths
{
    private const string AppDirName = "ThermalTrace";

    /// <summary>
    /// 数据根目录。设置 <c>THERMALTRACE_DATA_DIR</c> 时改用它——仅用于临时目录冒烟
    /// （如验证普通版不碰真实 daily\reports）；不设置时行为与旧版完全一致。
    /// </summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("THERMALTRACE_DATA_DIR") is { Length: > 0 } dir
        ? Path.GetFullPath(dir)
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDirName);

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// 当前时间片的工作快照（运行期临时文件，片落盘成功后删除）。
    /// 放在 Root 而不是 daily\ 或 logs\：前者会被切片解析，后者会被日志轮转。
    /// </summary>
    public static string SliceSnapshotFile => Path.Combine(Root, "slice-snapshot.json");

    /// <summary>
    /// 正式安装位置（固定路径）。
    /// 【为什么必须迁移出工作区】exe 原先放在带日期的工作目录下，
    /// 目录名带日期，一旦新开工作区或清理，自启指向的路径就静默失效且毫无提示。
    /// </summary>
    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", AppDirName);

    public static string InstalledExePath => Path.Combine(InstallDir, "ThermalTrace.exe");

    public static string DailyDir => Path.Combine(Root, "daily");

    public static string WeatherDir => Path.Combine(Root, "weather");

    public static string ReportsDir => Path.Combine(Root, "reports");

    /// <summary>
    /// 墨水屏快照（400×300 黑白 PNG）与"无数据"模板图的缓存目录。
    /// 模板图按天数缓存成 <c>nodata-{N}.png</c>，天数没变就直接复用。
    /// </summary>
    public static string EinkDir => Path.Combine(Root, "eink");

    /// <summary>
    /// 硬件健康累计量（目前只有 SSD 寿命计数器 <c>ssd.csv</c>，一天一行）。
    /// 【为什么不并进 daily\】daily\ 下是"每 30 分钟 × 每负载档"的切片行，
    /// 且 <see cref="Storage.DailyStore.ReadAll"/> 会把该目录所有 *.csv 当切片解析；
    /// 寿命是"每天一行"的另一种粒度，混进去要到处写兼容分支，单独一个目录最省心。
    /// </summary>
    public static string HealthDir => Path.Combine(Root, "health");

    public static string LogsDir => Path.Combine(Root, "logs");

#if PUSH_ENABLED
    /// <summary>
    /// 推送幂等状态：<c>{"lastPushedDate","lastResult","lastError"}</c>。
    /// 业务日期（不是推送时刻）是幂等键，避免一天内重复推、跨天后补推。
    /// </summary>
    public static string PushStateFile => Path.Combine(Root, "push-state.json");

    /// <summary>推送失败日志（时间 + 原因）。绝不影响采样主循环，但每次失败都留痕。</summary>
    public static string PushLogFile => Path.Combine(LogsDir, "push.txt");
#endif

    /// <summary>硬件指纹/传感器绑定口径历史（caliber.json）。</summary>
    public static string CaliberFile => Path.Combine(Root, "caliber.json");

    public static string DailyFile(DateOnly date) =>
        Path.Combine(DailyDir, $"{date:yyyy-MM-dd}.csv");

    public static string AmbientFile(DateOnly date) =>
        Path.Combine(WeatherDir, $"ambient-{date:yyyy-MM}.csv");

    public static string LogFile(string name) => Path.Combine(LogsDir, name);

    public static void EnsureAll()
    {
        foreach (var dir in new[] { Root, DailyDir, WeatherDir, ReportsDir, LogsDir, EinkDir, HealthDir })
            Directory.CreateDirectory(dir);
    }
}