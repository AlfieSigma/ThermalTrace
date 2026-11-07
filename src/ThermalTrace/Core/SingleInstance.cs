using System.Security.Cryptography;
using System.Text;

namespace ThermalTrace.Core;

/// <summary>
/// GUI / 托盘模式的单实例保护。
///
/// 【为什么需要】2026-11-08 第三方机器上同时存在三个 GUI 进程（无参数连续启动），
/// 同一分钟三个进程各写了一次同样的切片行到同一 CSV，同一时间片被重复累加。
/// 后台常驻的采样与落盘只能有一个实例。
///
/// 【为什么不挡命令行】<c>--today</c> / <c>--trend</c> / <c>--interpret</c> 等只读命令
/// 天然可以并发运行；只有 <see cref="Commands.Background"/> 获取本保护。
///
/// 【为什么按数据根命名】冒烟脚本用 <c>THERMALTRACE_DATA_DIR</c> 指向临时目录，
/// 与已有正式实例共用固定名称会互相挡住；互斥量名称带上数据根哈希后，
/// 只有真正共用同一份数据的进程才互斥。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexPrefix = @"Local\ThermalTrace.SingleInstance.";
    private const string ShowEventPrefix = @"Local\ThermalTrace.ShowWindow.";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private bool _ownsMutex;

    private SingleInstance(Mutex? mutex, bool ownsMutex, EventWaitHandle? showEvent)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _showEvent = showEvent;
    }

    /// <summary>是否拿到了唯一实例所有权。false 表示已有一个实例在跑。</summary>
    public bool IsPrimary { get; private init; }

    /// <summary>
    /// 获取单实例。已存在实例时返回 IsPrimary=false，并把显示事件挂到同一名称上，
    /// 供 <see cref="SignalShow"/> 请求已有实例打开主窗口。
    /// </summary>
    public static SingleInstance Acquire()
    {
        var key = KeyOf(AppPaths.Root);
        var mutexName = MutexPrefix + key;
        var showName = ShowEventPrefix + key;

        EventWaitHandle? showEvent = TryOpenShowEvent(showName);

        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // 更高权限的实例已存在时，当前进程可能无权打开同名内核对象。
            // 无法取样总比重复采样安全：按"已有实例"处理。
            return new SingleInstance(null, false, showEvent) { IsPrimary = false };
        }

        if (createdNew)
            return new SingleInstance(mutex, true, showEvent) { IsPrimary = true };

        // 名称已存在：原进程可能仍持有，也可能已崩溃退出。WaitOne(0) 能区分这两种情况。
        try
        {
            if (mutex.WaitOne(0))
                return new SingleInstance(mutex, true, showEvent) { IsPrimary = true };
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例被强杀：当前进程接管所有权，继续作为唯一实例
            return new SingleInstance(mutex, true, showEvent) { IsPrimary = true };
        }
        catch (UnauthorizedAccessException)
        {
            // 无权取得所有权，按已有实例处理
        }

        mutex.Dispose();
        return new SingleInstance(null, false, showEvent) { IsPrimary = false };
    }

    /// <summary>请求已有实例显示主窗口。事件打开失败时静默忽略。</summary>
    public void SignalShow()
    {
        try { _showEvent?.Set(); }
        catch { /* 跨权限无法写入时只保证不重复采样 */ }
    }

    /// <summary>
    /// 主实例的托盘定时器调用：消费一次"显示主窗口"请求。
    /// 自动重置事件，天然合并连续双击。
    /// </summary>
    public bool ConsumeShowRequest()
    {
        try { return _showEvent?.WaitOne(0) == true; }
        catch (ObjectDisposedException) { return false; }
    }

    private static EventWaitHandle? TryOpenShowEvent(string name)
    {
        try { return new EventWaitHandle(false, EventResetMode.AutoReset, name); }
        catch { return null; }
    }

    /// <summary>数据根哈希。取前 16 位十六进制，短且足以避免不同目录撞名。</summary>
    private static string KeyOf(string root)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { /* 已释放或线程不符时忽略 */ }
            _ownsMutex = false;
        }
        _mutex?.Dispose();
        _mutex = null;
        _showEvent?.Dispose();
        _showEvent = null;
    }
}
