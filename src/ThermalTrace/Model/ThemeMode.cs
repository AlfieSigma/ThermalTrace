namespace ThermalTrace.Model;

/// <summary>
/// 主题模式。默认 <see cref="Follow"/> —— 后台记录工具没有理由让用户手动挑主题。
///
/// 配合 <c>UiOptions.ThemeMode</c> 持久化到 settings.json，
/// 序列化为字符串（JsonStore 已注册 JsonStringEnumConverter）。
/// 详见主题处理约定。
/// </summary>
public enum ThemeMode
{
    /// <summary>跟随系统「默认应用模式」（Win10 1607+ 的 AppsUseLightTheme）。</summary>
    Follow,

    /// <summary>强制浅色。</summary>
    Light,

    /// <summary>强制深色。</summary>
    Dark
}
