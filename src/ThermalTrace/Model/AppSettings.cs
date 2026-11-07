using System.Text.Json.Serialization;

namespace ThermalTrace.Model;

public sealed class AppSettings
{
    public string CleanDateIso { get; set; } = string.Empty;
    public SensorBinding Sensors { get; set; } = new();
    public SamplerOptions Sampler { get; set; } = new();
    public AmbientOptions Ambient { get; set; } = new();
    public UiOptions Ui { get; set; } = new();

    /// <summary>极趣 Note 4 墨水屏推送配置。API Key 仅以 DPAPI 密文（<see cref="PushOptions.ApiKeyEnc"/>）落盘，明文永不写磁盘。</summary>
    public PushOptions Push { get; set; } = new();

    [JsonIgnore]
    public DateOnly? CleanDate =>
        DateOnly.TryParse(CleanDateIso, out var d) ? d : null;

    /// <summary>
    /// 展示层：负载类别的下界（单核峰值 %）。首档固定从 0 起算，配置里的首档值不参与归档。
    /// 存储层永远是 10% 细粒度，这里只控制如何合并成友好类别——
    /// 边界只影响展示，不做后台即时重算；GUI 在切回页面时重新取值，HTML 报告在下次生成时生效。
    /// </summary>
    public int[] CategoryLowerBounds { get; set; } = [0, 8, 35, 70, 92];

    public string[] CategoryNames { get; set; } =
        ["空闲", "轻度", "中等", "重度", "满载"];

    /// <summary>把 10% 细粒度档位映射到展示类别序号，越界时归入最后一档。</summary>
    public int CategoryOf(int lowerBound)
    {
        var bounds = CategoryLowerBounds;
        if (bounds == null || bounds.Length == 0) return 0;
        var idx = 0;
        for (int i = 0; i < bounds.Length; i++)
            if (lowerBound >= bounds[i]) idx = i;
        return idx;
    }

    public string CategoryName(int index)
    {
        var names = CategoryNames;
        return names != null && index >= 0 && index < names.Length
            ? names[index]
            : "类别" + index;
    }
}

public sealed record SamplerOptions
{
    /// <summary>采样间隔（秒）。5 秒足够反映人的工作节奏，MSR 开销极低。</summary>
    public int IntervalSeconds { get; init; } = 5;

    /// <summary>时间片长度（分钟）。每片结束落盘一次。</summary>
    public int SliceMinutes { get; init; } = 30;

    /// <summary>降频余量低于此值即视为"已影响性能"，在 GUI 中高亮。</summary>
    public double TjmaxAlertC { get; init; } = 8;

    public bool StartMinimized { get; init; } = true;
}

public sealed record AmbientOptions
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string LocationName { get; init; } = "";

    /// <summary>联网获取环境气温。关闭时全部降级为本地代理。</summary>
    public bool UseOnlineWeather { get; init; } = true;

    /// <summary>联网刷新的间隔（分钟）。默认每小时一次。</summary>
    public int RefreshMinutes { get; init; } = 60;

    /// <summary>联网时额外回补多少天的历史气温（Open-Meteo 最多 92 天）。</summary>
    public int BackfillDays { get; init; } = 30;
}

public sealed record UiOptions
{
    /// <summary>托盘 tooltip 里高亮的提醒阈值（℃）。</summary>
    public double AlertTempC { get; init; } = 85;

    /// <summary>
    /// 首次运行引导。true 时程序第一次起来会问一句"要不要现在开开机自启"；
    /// 问完（选是或选否）即落盘为 false，不再打扰。
    /// 见 <c>UI/SetupHint.cs</c>。
    /// </summary>
    public bool ShowSetupHint { get; init; } = true;

    /// <summary>
    /// 首次启动的托盘气泡引导（2026-10-13，beta 用户反馈"打开就闪退"）。
    /// true 时程序第一次起来会在托盘弹一条"已在后台运行，双击图标开主窗口"；
    /// 弹完即落盘为 false，之后日常使用不再打扰。见 <c>UI/TrayIconHost.cs</c>。
    /// 与 <see cref="ShowSetupHint"/> 各管一件事：一个解决"找不到程序"，
    /// 一个解决"要不要开自启"。
    /// </summary>
    public bool ShowTrayHint { get; init; } = true;

    /// <summary>
    /// 主题模式。默认跟随系统「默认应用模式」。
    /// 强制浅/深只在用户明确要求时才用——设置页下拉框写这里。
    /// </summary>
    public ThemeMode ThemeMode { get; init; } = ThemeMode.Follow;

    /// <summary>
    /// 主窗口上次关闭时的位置与大小。
    /// <c>WindowWidth</c> / <c>WindowHeight</c> 为 0 表示"还没记过"，
    /// 此时窗口按 900×600 + 屏幕居中打开。
    /// X / Y 可能为负——多显示器下主屏左边的屏就是负坐标，不能用 0 当"未设置"。
    /// </summary>
    public int WindowX { get; init; }
    public int WindowY { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }

    /// <summary>四个值都记过，且尺寸合法（不小于 MinimumSize）。</summary>
    public bool HasWindowBounds =>
        WindowWidth > 0 && WindowHeight > 0
        && WindowWidth >= 760 && WindowHeight >= 520;
}

/// <summary>
/// 墨水屏（极趣 Note 4）推送配置。
///
/// 【为什么是 record + init】与 <see cref="SamplerOptions"/> 等保持一致：改值必须整体替换，
/// 避免"改了一半"的部分更新落到磁盘上。GUI 写入时一律 <c>settings.Push with { ... }</c>。
///
/// 【API Key 永远不落明文】<see cref="ApiKeyEnc"/> 存的是 DPAPI（CurrentUser）密文 base64；
/// 明文只在运行时由 <c>Push.CredentialStore</c> 解出，或在本会话内存中暂存（DPAPI 不可用时的降级）。
/// </summary>
public sealed record PushOptions
{
    /// <summary>是否启用每日自动推送（调度检查据此决定推不推；手动 --push 不受此限）。</summary>
    public bool Enabled { get; init; }

    /// <summary>设备 MAC，形如 AA:BB:CC:DD:EE:FF。来自 <c>GET /devices</c> 返回。</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>槽 1（近 30 天趋势图）的图片页槽位 1–5（持久化存储，不打扰待办主页）。</summary>
    public string PageId { get; init; } = "1";

    /// <summary>槽 2（自清灰以来平滑图）的图片页槽位 1–5，默认 <c>"2"</c>。与 <see cref="PageId"/> 相同则槽 2 跳过。</summary>
    public string PageId2 { get; init; } = "2";

    /// <summary>目标推送时刻（24h），默认 23:00。</summary>
    public int Hour { get; init; } = 23;

    // 【2026-11-05 双槽】此前的 window（Last30Days / SinceClean）已删除：
    // 两个窗口现在由两个槽各承担一半，设置项已无意义。旧 settings.json 里的 window 键
    // 会被 System.Text.Json 忽略（JsonStore 未启用 JsonUnmappedMemberHandling.Disallow），
    // 不报错、不影响其他配置；下一次保存时该键自然消失。

    /// <summary>DPAPI 密文（base64）。明文永不落盘。空 = 未配置。</summary>
    public string ApiKeyEnc { get; init; } = "";
}
