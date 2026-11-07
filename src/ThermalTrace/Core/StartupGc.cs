namespace ThermalTrace.Core;

/// <summary>
/// 启动后的一次性 GC 收编（内存优化）。
///
/// 【为什么需要】.NET 的 GC 是懒的：常驻进程的工作集会贴着历史峰值只涨不落。
/// 启动阶段（JIT、LHM 全硬件枚举、WMI、首次渲染）会产出一大把瞬态垃圾，
/// 之后即使程序进入"每 5 秒一条采样"的稳态，那段峰值也不会主动还给系统。
/// 所以等所有初始化做完，主动收一次。
///
/// 【为什么是 30 秒】太早收会撞上还在进行的初始化——收了也是白收，马上又涨回去；
/// 太晚用户已经看到"占 180 MB"。30 秒时后台线程已稳定，主窗口多半还没打开。
///
/// 【分工】csproj 的 <c>GCConserveMemory</c> 管"长期别再涨"，这里管"把启动峰值清掉"，
/// 两个一起才有效；只做一个都看不到完整效果。
/// </summary>
internal static class StartupGc
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(30);

    /// <summary>登记一次"延迟后的强制收集"。火一次即撤，不留定时器。</summary>
    public static void Schedule()
    {
        // 【必须写全名】项目开了 UseWindowsForms，隐式 using 里同时有
        // System.Windows.Forms.Timer 和 System.Threading.Timer，写 Timer 会 CS0104 歧义。
        // 这里要的是线程定时器：它在线程池里跑一次，不依赖消息循环
        // （WinForms 那个 Timer 必须跑消息循环才 Tick，后台阶段可能还没到）。
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(_ =>
        {
            try
            {
                // blocking: false —— 非阻塞 gen2，不卡 UI 线程与采样线程
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);
                GC.WaitForPendingFinalizers();
            }
            catch
            {
                // GC 失败不该影响任何流程
            }
            finally
            {
                timer?.Dispose();
            }
        }, null, Delay, System.Threading.Timeout.InfiniteTimeSpan);
    }
}
