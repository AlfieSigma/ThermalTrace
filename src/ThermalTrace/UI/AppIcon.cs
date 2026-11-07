using System.Drawing;

namespace ThermalTrace.UI;

/// <summary>
/// 应用图标的唯一来源：托盘、主窗口共用它，避免各处各写一份。
///
/// 【为什么从 exe 里提取而不是嵌入资源】
/// 本项目是单文件自包含发布（PublishSingleFile=true，见 ThermalTrace.csproj），
/// 嵌入资源在运行时没有稳定可预期的磁盘路径。Environment.ProcessPath
/// 在自包含单文件下稳定指向真实 exe，提取出来的图标带完整多尺寸信息，
/// Windows 任务栏与托盘都会自动按需选尺寸。
/// </summary>
internal static class AppIcon
{
    private static Icon? _cached;

    /// <summary>
    /// 取当前应用的图标。失败时回退到系统默认图标。
    ///
    /// 【为什么必须兜底而不是抛异常】托盘图标是用户唯一能看到"程序正在运行"的入口
    /// （主窗口是隐藏而非销毁的）。图标加载失败就把整个托盘炸掉，
    /// 比"图标不好看"严重得多。
    /// </summary>
    public static Icon Get()
    {
        if (_cached is not null) return _cached;

        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                _cached = Icon.ExtractAssociatedIcon(path);
            }
            catch (Exception)
            {
                // exe 被占用、文件被替换、或非 PE 文件——都退回默认图标
                _cached = null;
            }
        }

        _cached ??= SystemIcons.Application;
        return _cached;
    }
}
