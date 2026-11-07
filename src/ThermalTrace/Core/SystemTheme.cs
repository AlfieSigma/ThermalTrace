using Microsoft.Win32;

namespace ThermalTrace.Core;

/// <summary>
/// 读取 Windows 系统主题。
///
/// 【为什么必须手写】
/// .NET 8 没有 Application.SetColorMode(SystemColorMode)（那是 .NET 9/10 的 API），
/// net8.0-windows 下也不存在任何官方深色模式开关。整套主题只能自己读注册表 + 自己刷色。
/// 见主题处理约定。
///
/// 【Win10 上读什么】
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
///   AppsUseLightTheme   DWORD   1 = 浅色应用模式，0 = 深色应用模式
/// Win10 1607+ 就有这个键。
/// </summary>
internal static class SystemTheme
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// 系统「默认应用模式」是否为浅色。
    /// 【默认值必须是 true】某些优化软件会删掉这个键值，
    /// 读不到时按深色处理会让用户莫名得到一个黑窗口（主题处理约定）。
    /// </summary>
    public static bool IsLightAppTheme()
    {
        try
        {
            var v = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1);
            return v switch
            {
                int i => i != 0,
                _ => true          // 类型异常（被写成字符串等）→ 浅色
            };
        }
        catch
        {
            return true;           // 无权限 / 键不存在 → 浅色
        }
    }

    /// <summary>
    /// 系统「Windows 界面色」是否为浅色。仅供诊断输出，
    /// 判定一律以 <see cref="IsLightAppTheme"/> 为准——两者是独立开关，
    /// 用户在「设置 → 个性化 → 颜色」里改的通常是前者，观察到的却是后者。
    /// </summary>
    public static bool IsLightSystemTheme()
    {
        try
        {
            var v = Registry.GetValue(PersonalizeKey, "SystemUsesLightTheme", 1);
            return v is int i ? i != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>系统应用模式真的翻转时触发（挂载了消息循环时回调在 UI 线程上）。</summary>
    public static event EventHandler? Changed;

    private static bool _hooked;
    private static bool _lastLight = true;

    /// <summary>幂等。挂上 UserPreferenceChanged 监听。</summary>
    public static void EnsureHook()
    {
        if (_hooked) return;
        _hooked = true;
        _lastLight = IsLightAppTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void RemoveHook()
    {
        if (!_hooked) return;
        _hooked = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    /// <summary>
    /// UserPreferenceChanged 触发得非常频繁（改配色、改壁纸、锁屏、远程桌面切换都会来一发），
    /// 所以绝不能把它直接透出去：必须回读注册表，只有真实值翻转了才通知订阅者，
    /// 否则每来一次 WM_SETTINGCHANGE 就全窗体重绘一次。
    /// </summary>
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        bool light;
        try
        {
            light = IsLightAppTheme();
        }
        catch
        {
            return;
        }

        if (light == _lastLight) return;
        _lastLight = light;
        Changed?.Invoke(sender, EventArgs.Empty);
    }
}
