using ThermalTrace.Core;
using ThermalTrace.Hardware;
using ThermalTrace.Services;

namespace ThermalTrace.UI;

/// <summary>
/// 「重新自检」「重新绑定传感器」两个维护动作的唯一实现。
/// 概览页、设置页、托盘三处只负责按钮状态与结果弹窗，不再各抄一套后台采集。
/// </summary>
internal static class MaintenanceActions
{
    private const int MaxReportLength = 3000;

    /// <summary>后台跑一次自检，把完整报告交给 <paramref name="finish"/>（可能在后台线程回调）。</summary>
    public static void RunSelfCheck(string emptyMessage, Action<string> finish) =>
        RunInBackground("自检失败：", () =>
        {
            var text = Commands.SelfCheckText();
            if (text.Length == 0) text = emptyMessage;
            if (text.Length > MaxReportLength) text = text[..MaxReportLength] + "\n…（已截断）";
            return text;
        }, finish);

    /// <summary>后台枚举传感器 → 自动推断 → 落盘 → 热换进采样循环。
    /// 文案差异由调用方传入，保证三个入口各自的措辞不变。</summary>
    public static void RunRebind(
        SamplerService? sampler,
        string notFoundMessage,
        string savedHeader,
        Action<string> finish) =>
        RunInBackground("绑定失败：",
            () => RebindText(sampler, notFoundMessage, savedHeader), finish);

    private static void RunInBackground(string failurePrefix, Func<string> work, Action<string> finish)
    {
        var thread = new Thread(() =>
        {
            string text;
            try
            {
                text = work();
            }
            catch (Exception ex)
            {
                text = failurePrefix + ex.Message;
            }
            finish(text);
        })
        { IsBackground = true };
        thread.Start();
    }

    private static string RebindText(SamplerService? sampler, string notFoundMessage, string savedHeader)
    {
        // 与采样线程不共用实例，避免并发枚举；两个入口一直是这个做法。
        using var hw = new HardwareMonitor();
        hw.Open();

        var settings = SettingsStore.Load();
        var result = Commands.PersistAutoBind(settings, hw.EnumerateSensors(), "用户点击『重新绑定传感器』");

        if (result is null) return notFoundMessage;

        // 【易漏】不热换就要等下次重启才生效，用户会以为按钮没反应
        var hot = sampler is not null;
        sampler?.ReloadSensors(result.Binding);

        return savedHeader + Commands.DescribeRoles(result) + "\n\n"
             + (hot
                 ? "采样线程已切到新绑定，约 5 秒后下一拍开始用新口径记录。"
                 : "本进程没有采样服务实例，重启程序后生效。");
    }
}
