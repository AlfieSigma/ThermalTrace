using Microsoft.Win32;

namespace ThermalTrace.Core;

/// <summary>
/// 【2026-11-14】电源事件宿主：把挂起 / 唤醒变成程序的一等输入。
///
/// 起因：2026-11-14 休眠一夜唤醒后，LHM 设备重枚举导致采样循环每拍抛
/// NullReferenceException，心跳照常更新、页面却一直空着。之前没有任何一层
/// 消费系统电源事件，也没有能重复触发恢复链路的验证入口。
///
/// 分工：
///  - 本类只负责"接事件、消费模拟请求、把动作送回 UI 线程"；
///  - 心跳重置与重建请求交给 SamplerService；实际重建 LHM 由采样线程执行；
///  - 页面 / 托盘刷新由注入的回调完成。
///
/// 为什么不在这里直接重开硬件：LHM 的 Computer 由采样线程独占使用，
/// UI 线程同时 Open/Close 会撞上正在读 MSR 的那一拍。
/// </summary>
internal sealed class PowerModeWatcher : IDisposable
{
    private readonly SingleInstance _single;
    private readonly Action _onSuspend;
    private readonly Action<string> _onResume;
    private readonly Action _refreshUi;
    private readonly SynchronizationContext? _uiContext;
    private System.Windows.Forms.Timer? _poll;
    private bool _disposed;

    public PowerModeWatcher(SingleInstance single, Action onSuspend,
        Action<string> onResume, Action refreshUi)
    {
        _single = single;
        _onSuspend = onSuspend;
        _onResume = onResume;
        _refreshUi = refreshUi;
        _uiContext = SynchronizationContext.Current;
    }

    public void Start()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // --simulate-resume 走单实例命名事件。沿用一个 500 毫秒轮询，
        // 与托盘消费"显示主窗口"请求的方式一致，不新起线程。
        _poll = new System.Windows.Forms.Timer { Interval = 500 };
        _poll.Tick += (_, _) =>
        {
            if (!_disposed && _single.ConsumeResumeRequest())
                Resume("模拟唤醒");
        };
        _poll.Start();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            Post(_onSuspend);
        else if (e.Mode == PowerModes.Resume)
            Post(() => Resume("系统唤醒"));
    }

    /// <summary>真实唤醒与模拟唤醒共用同一条路径，验证入口不是另一套逻辑。</summary>
    private void Resume(string reason)
    {
        _onResume(reason);
        _refreshUi();
    }

    private void Post(Action action)
    {
        var context = _uiContext;
        if (context is not null)
        {
            try { context.Post(_ => Safe(action), null); return; }
            catch { /* 窗口正在退出时退化为同步执行 */ }
        }
        Safe(action);
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch { /* 电源事件处理失败不能拖垮消息循环 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _poll?.Stop();
        _poll?.Dispose();
        _poll = null;
    }
}
