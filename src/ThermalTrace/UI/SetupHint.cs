using ThermalTrace.Core;
using ThermalTrace.Model;

namespace ThermalTrace.UI;

/// <summary>
/// 首次运行引导：只在"程序还没开自启"的时候问一句，帮助用户把自启一次打开。
///
/// 【为什么要这个东西】
/// 自启是本项目唯一的价值前提 —— 没有连续数据，"现在比一个月前热了多少"就无从回答
/// （产品边界）。但注册它需要一个"先想起来要开"的动作：
/// 日常使用通常不开终端，而设置页又需要用户先想到去点。所以第一次运行时当面问一次，
/// 之后不再打扰。
///
/// 【只问一次】
/// 无论用户选"是"还是"否"，都把 <c>UiOptions.ShowSetupHint</c> 写成 false 落盘。
/// 选"否"也照样关掉：反复弹同一个问题会让人直接把程序卸了。
///
/// 【失败必须原样给用户看】
/// 未提权时 <c>AutoStart.Enable()</c> 返回失败并带原因，这里弹红色错误框把原因原样贴出，
/// 与设置页开关遵循同一规则。
/// </summary>
internal static class SetupHint
{
    /// <summary>托盘就绪后再弹，给消息循环一点起来的时间。</summary>
    private const int DelayMs = 2000;

    public static void MaybeShow()
    {
        AppSettings settings;
        try
        {
            settings = SettingsStore.Load();
        }
        catch
        {
            return;   // 连配置都读不出来时，别再拿一个问题打扰用户
        }

        if (!settings.Ui.ShowSetupHint) return;

        // 自启已经开着了 —— 没什么可引导的，安静地把标记清掉
        if (AutoStart.Status().IsEnabled)
        {
            MarkShown();
            return;
        }

        // 【为什么用 Timer 而不是直接弹】Background() 里 Application.Run() 还没开始，
        // 此时 MessageBox.Show 会把后半段启动流程（释放控制台、起采样线程）整个卡住。
        // WinForms Timer 的 Tick 要等消息循环转起来才到，正好挂在托盘就绪之后。
        var timer = new System.Windows.Forms.Timer { Interval = DelayMs };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            Run();
        };
        timer.Start();
    }

    private static void Run()
    {
        var ask = MessageBox.Show(
            "ThermalTrace 已经在后台开始记录散热数据了。\n\n"
            + "要现在开启开机自启吗？\n"
            + "开启后每次登录它都会自己开始记录，你不需要再做任何事；\n"
            + "之后随时可以在「设置 → 开机自启」里关掉。",
            "ThermalTrace — 首次运行",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (ask == DialogResult.Yes) Enable();

        // 选什么都只问这一次
        MarkShown();
    }

    private static void Enable()
    {
        var result = AutoStart.Enable();
        if (result.Ok)
        {
            MessageBox.Show(
                "开机自启已开启。\n\n" + result.Message,
                "ThermalTrace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 原样把原因给用户，绝不像旧实现那样"没抛异常就当成功"
        // （自启的真实状态）
        MessageBox.Show(
            "注册开机自启失败\n\n" + result.Message,
            "ThermalTrace", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void MarkShown()
    {
        try
        {
            var s = SettingsStore.Load();
            s.Ui = s.Ui with { ShowSetupHint = false };
            SettingsStore.Save(s);
        }
        catch (Exception ex)
        {
            // 标记没写掉只会多问一次，不值得打断启动
            ConsoleHost.WriteError($"[warn] 首次引导标记保存失败: {ex.Message}");
        }
    }
}
