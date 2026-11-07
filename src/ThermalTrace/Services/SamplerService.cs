using System.Diagnostics;
using ThermalTrace.Core;
using ThermalTrace.Hardware;
using ThermalTrace.Model;
#if PUSH_ENABLED
using ThermalTrace.Push;
#endif
using ThermalTrace.Storage;

namespace ThermalTrace.Services;

/// <summary>
/// 后台采样服务：整个程序的主循环。
///
/// 【设计要点】
///  - 采样间隔默认 5 秒。实测每 5 秒读一次 MSR 的 CPU 开销远低于 0.5%，
///    相比"每秒原始流"的方案，开销与SSD 写入量都降低一个数量级。
///  - 原始采样点只在内存里活到当前时间片结束，算完统计立刻丢弃。
///  - 每片结束落盘一次（默认 30 分钟）；每 5 分钟把当前片状态刷进工作快照，
///    进程重启后接着累加，断电最多丢 5 分钟而不是一整片。
///  - 单次读取失败只跳过该次采样，绝不让整个服务崩掉。
/// </summary>
internal sealed class SamplerService : IDisposable
{
    private const int SnapshotIntervalMinutes = 5;

    private readonly AppSettings _settings;
    private readonly HardwareMonitor _hw;
    private readonly Aggregator _agg;
    /// <summary>
    /// 主温度零读数判据。LHM 在不支持的平台返回 0（不是 NaN），必须在入库前拦掉，
    /// 否则 0 会被当成真实低温写进 CSV 并参与空闲基线、ΔT 与超温统计。
    /// </summary>
    private readonly PrimaryTemperatureGuard _tempGuard = new();
    // 采样线程异常退出后看门狗要重建 CTS，故不设 readonly。
    private CancellationTokenSource _cts = new();
    private readonly object _startGate = new();
    private Thread? _thread;
    private volatile bool _running;
    private long _lastHeartbeatTicks;
    private bool? _lastOnAc;

    /// <summary>
    /// 当前生效的传感器绑定。单独存一份而不是每拍读 <c>_settings.Sensors</c>，
    /// 是为了让 GUI 换绑定不需要重启 —— 见 <see cref="ReloadSensors"/>。
    /// 采样线程读、UI 线程写，必须 volatile，否则可能被 JIT 提到循环外一直读旧值。
    /// </summary>
    private volatile SensorBinding _sensors;

    public SamplerService(AppSettings settings, HardwareMonitor hw)
    {
        _settings = settings;
        _hw = hw;
        _sensors = settings.Sensors;
        _agg = new Aggregator(settings.Sampler.SliceMinutes);
    }

    public Aggregator Aggregator => _agg;
    public Sample? Latest { get; private set; }
    public (LoadBand Band, bool OnAc) LatestContext { get; private set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public int SampleCount { get; private set; }
    public int ErrorCount { get; private set; }

    /// <summary>主温度是否已被判为不可用（恒返回 0）。界面据此给出原因，而不是显示 0.0℃。</summary>
    public bool PrimaryTempUnavailable => _tempGuard.Unavailable;

    /// <summary>采样线程是否仍在 Run 内。异常退出时 finally 会置 false。</summary>
    public bool IsRunning => _running;

    /// <summary>最近一次循环转完的时刻（UTC）。看门狗据此区分"活着"与"卡死"。</summary>
    public DateTimeOffset LastHeartbeatUtc => new(Interlocked.Read(ref _lastHeartbeatTicks), TimeSpan.Zero);

    public TimeSpan HeartbeatAge => DateTimeOffset.UtcNow - LastHeartbeatUtc;

    /// <summary>主动停止（托盘退出 / Ctrl+C），区别于线程异常退出。</summary>
    public bool StopRequested { get; private set; }

    public string LastError { get; private set; } = "";

    /// <summary>
    /// 采样循环当前停在哪个阶段。只在内存里赋值，不逐拍写盘；
    /// 心跳停摆时由托盘看门狗写进 sampler.txt，用来回答"卡在哪"。
    /// </summary>
    private volatile string _phase = "未启动";
    public string CurrentPhase => _phase;

    /// <summary>停摆诊断用：采样线程是否存活及其 ThreadState。</summary>
    public string ThreadStateText
    {
        get
        {
            try
            {
                var t = _thread;
                return t is null ? "无线程" : $"{(t.IsAlive ? "存活" : "已退出")}/{t.ThreadState}";
            }
            catch { return "不可读"; }
        }
    }

    /// <summary>
    /// 上次读 SSD 寿命的日期。初值 MinValue → 启动后第一拍就试一次。
    /// 跨天只试一次，成功与否都不再重试：一天读不到（盘忙 / 驱动还没起来）
    /// 不值得每 5 秒反复去打扰硬件。
    /// </summary>
    private DateOnly _lastHealthDay = DateOnly.MinValue;

#if PUSH_ENABLED
    /// <summary>
    /// 下次推送检查时刻（D13 调度，P4.1）。初值 MinValue → 启动后第一拍就查：
    /// 昨晚漏推的补推必须"下次开机即补"，不能等一个周期。
    /// </summary>
    private DateTimeOffset _nextPushCheck = DateTimeOffset.MinValue;

    /// <summary>
    /// 推送进行中。离线时重试链要走一分多钟，期间必须挡住后续检查再起一条
    /// （状态文件要等整条链跑完才写，中途判定仍是"该推"）。volatile：本字段由采样线程写、
    /// 推送线程的 finally 也写。
    /// </summary>
    private volatile bool _pushRunning;
#endif

    public event Action<Sample>? SampleProduced;

    /// <summary>
    /// 热替换传感器绑定，**不需要重启采样线程**。
    ///
    /// 【为什么必须有】GUI 改绑定走的是 SettingsStore.Load() → 改 → Save()，
    /// 那是另一个 AppSettings 实例；本服务构造时把 _settings 抓了一次，
    /// 若只写盘不换引用，用户点了「重新绑定传感器」会以为按钮没反应，
    /// 实际要等下次重启才生效。采样循环每拍都重读引用，所以换完下一拍（约 5 秒）即生效。
    /// </summary>
    public void ReloadSensors(SensorBinding binding)
    {
        _settings.Sensors = binding;   // 同步给共享的那份，GUI/托盘读到的仍是同一个对象
        _sensors = binding;

        // 换绑后旧传感器的「恒返回 0」结论不再适用，必须从可用态重新累计；
        // 否则即使新传感器正常，界面也会一直显示温度不可用（判据无法自愈到初始态）。
        _tempGuard.Reset();
        SamplerLog.Write("sensor", $"传感器绑定已热替换：{binding.PrimarySensorName}，主温度判据已重置");
    }

    /// <summary>启动采样线程。幂等：已有活线程时直接返回。</summary>
    public void Start() => StartCore(restarting: false);

    /// <summary>
    /// 看门狗用：线程已退出时重拉。线程还活着但没心跳（卡住）时返回 false，
    /// 不强杀线程——杀掉一个正握着 LHM 实例的线程会留下不可预期的状态。
    /// </summary>
    public bool TryRestart() => StartCore(restarting: true);

    private bool StartCore(bool restarting)
    {
        lock (_startGate)
        {
            if (_thread is { IsAlive: true }) return false;
            if (restarting) SamplerLog.Write("restart", "采样线程已退出，重新拉起");
            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
            StopRequested = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "ThermalTrace.Sampler" };
            _thread.Start();
            return true;
        }
    }

    /// <summary>
    /// 阻塞运行直到取消：采样循环 + 跨片落盘 + 定时刷新天气。
    ///
    /// 【2026-11-06】整圈循环统一包进 try：此前跨片 flush、日志、天气、推送检查都在
    /// try 之外，任一处抛异常会让后台线程静默退出，托盘和 GUI 却毫不知情。
    /// 循环每转一圈写一次心跳，看门狗据此区分"活着"与"卡死"。
    /// </summary>
    public void Run()
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.Sampler.IntervalSeconds, 1, 60));
        var nextWeather = DateTimeOffset.MinValue;
        _running = true;
        _lastOnAc = null;
        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);

        try
        {
            // 登记当前硬件口径（CPU + 传感器绑定），报告据此判断口径是否变化
            try { Analysis.FingerprintStore.Record(_settings); }
            catch { /* 指纹登记失败不影响采样 */ }

            SamplerLog.Write("start",
                $"间隔 {interval.TotalSeconds:0}s / 时间片 {_settings.Sampler.SliceMinutes} 分钟 / 数据目录 {AppPaths.DailyDir}");
            if (_agg.RestoredFromSnapshot)
                SamplerLog.Write("snapshot",
                    $"恢复当前片 {_agg.CurrentSliceStart:HH:mm}，{_agg.RestoredGroups} 组 / {_agg.RestoredSamples} 样本");
            ConsoleHost.WriteLine("ThermalTrace 后台采样已启动");
            ConsoleHost.WriteLine($"  采样间隔: {interval.TotalSeconds:0} 秒 /时间片 {_settings.Sampler.SliceMinutes} 分钟");
            ConsoleHost.WriteLine($"  数据目录: {AppPaths.DailyDir}");
            ConsoleHost.WriteLine("  实时温度显示请用 LiteMonitor，本程序只做长期趋势记录。");
            ConsoleHost.WriteLine("  按 Ctrl+C 退出。");
            ConsoleHost.WriteLine();

            var sw = Stopwatch.StartNew();
            var nextSnapshot = DateTimeOffset.Now.AddMinutes(SnapshotIntervalMinutes);

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _phase = "读取硬件";
                    var rowsBefore = _agg.RowsWritten;
                    _hw.Update();

                    _phase = "生成样本";
                    var s = _hw.ReadSample(_sensors, (int)sw.Elapsed.TotalSeconds);

                    // 主温度恒 0 的平台（LHM 返回 0 而非 NaN）在入库前拦掉：
                    // 命中零带的温度字段置 NaN，聚合层按既有规则丢弃，不写伪造的 0。
                    _phase = "主温度判据";
                    var wasUnavailable = _tempGuard.Unavailable;
                    s = _tempGuard.Apply(s);
                    LogTempGateChange(wasUnavailable);

                    _phase = "供电状态";
                    var (onAc, batteryPct) = SystemProbe.ReadPowerState();
                    LogPowerChange(onAc, batteryPct);
                    s = s with { OnAcPower = onAc };

                    _phase = "聚合与跨片落盘";
                    if (_agg.Add(s, onAc))
                        SampleProduced?.Invoke(s);

                    // 温度不可用时 Add 会拒绝入库，但界面仍需要拿到最近一拍来显示「不可用」，
                    // 所以 Latest 与样本计数不再依赖入库结果。
                    SampleCount++;
                    Latest = s;
                    LatestContext = (LoadBandExtensions.FromMaxCoreUtil(s.MaxCoreUtilPct), onAc);

                    // Add 内部跨片时已 FlushCurrent，这里只记日志：
                    // 旧实现又 flush 一次，会把新片第 1 个样本写成 n=1 的孤立行。
                    _phase = "跨片日志";
                    var written = _agg.RowsWritten - rowsBefore;
                    if (written > 0)
                    {
                        var file = Path.GetFileName(AppPaths.DailyFile(DateOnly.FromDateTime(DateTime.Now)));
                        SamplerLog.Write("slice", $"已写入 {written} 行 -> {file}");
                        ConsoleHost.WriteLine($"[info] 已写入 {written} 行 -> {file}");
                    }

                    _phase = "天气检查";
                    if (_settings.Ambient.UseOnlineWeather && DateTimeOffset.Now >= nextWeather)
                    {
                        nextWeather = DateTimeOffset.Now.AddMinutes(
                            Math.Max(30, _settings.Ambient.RefreshMinutes));
                        _ = RefreshWeatherAsync();
                    }

                    // 【每日一次】SSD 寿命计数器。
                    // 常规刷新跳过了存储硬件（见 HardwareMonitor.Update），所以读之前必须单独刷一次。
                    _phase = "硬盘健康";
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    if (today != _lastHealthDay)
                    {
                        _lastHealthDay = today;   // 当天只试这一回，成功与否都不再重试
                        TryRecordDriveHealth(today);
                    }

#if PUSH_ENABLED
                    // 【每日推送】墨水屏快照（D13）：每 60 秒判定一次"应推业务日期 T"是否还没推。
                    _phase = "推送检查";
                    if (DateTimeOffset.Now >= _nextPushCheck)
                    {
                        _nextPushCheck = DateTimeOffset.Now.AddSeconds(60);
                        TryScheduledPush();
                    }
#endif
                }
                catch (Exception ex)
                {
                    // 不覆盖 _phase：停摆诊断要保留"哪一阶段出的事"。
                    ErrorCount++;
                    LastError = ex.Message;
                    if (ErrorCount <= 3 || ErrorCount % 100 == 0)
                    {
                        SamplerLog.Write("error", $"第 {ErrorCount} 次循环异常: {ex.GetType().Name}: {ex.Message}");
                        ConsoleHost.WriteError($"[warn] 第 {ErrorCount} 次采样失败: {ex.Message}");
                    }
                }

                // 心跳：只要循环转完一圈就更新；卡在 _hw.Update() 之类时不会走到这里。
                Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);

                // 工作快照每 5 分钟一次，不在每拍的关键路径上。
                if (DateTimeOffset.Now >= nextSnapshot)
                {
                    nextSnapshot = DateTimeOffset.Now.AddMinutes(SnapshotIntervalMinutes);
                    _phase = "工作快照";
                    _agg.SaveSnapshot();
                }

                _phase = "等待下一拍";
                try { _cts.Token.WaitHandle.WaitOne(interval); }
                catch { break; }
            }
        }
        finally
        {
            _phase = "已退出";
            Shutdown();
            _running = false;
            SamplerLog.Write("stop", StopRequested ? "主动停止" :
                $"线程退出（非主动停止），样本 {SampleCount} / 异常 {ErrorCount}");
        }
    }

    /// <summary>
    /// 主温度判据状态切换留痕。只在进入「恒返回 0」与恢复时各写一条，
    /// 避免每 5 秒重复刷同一句话。
    /// </summary>
    private void LogTempGateChange(bool wasUnavailable)
    {
        if (_tempGuard.Unavailable == wasUnavailable) return;

        SamplerLog.Write("sensor", _tempGuard.Unavailable
            ? $"主温度连续 {PrimaryTemperatureGuard.ConfirmSamples} 拍读数为 0，判定传感器不可用；"
              + "该时段不写温度、不参与空闲基线与 ΔT"
            : "主温度恢复有效读数，重新纳入统计");
    }

    /// <summary>
    /// 把供电切换落到 sampler.txt。2026-11-06 上午小区停电时段恰好落在采样停摆窗口里，
    /// 事后只能靠 Windows 事件日志倒推；留下这条，"这段时间为什么没数据"可以直接和日志对上。
    /// 只写文件、不碰控制台输出（控制台写入是停摆候选卡点之一）。
    /// </summary>
    private void LogPowerChange(bool onAc, double batteryPct)
    {
        var prev = _lastOnAc;
        _lastOnAc = onAc;
        if (prev is null)
        {
            SamplerLog.Write("power", $"启动时刻供电={PowerName(onAc)}");
            return;
        }
        if (prev.Value == onAc) return;

        var pct = double.IsNaN(batteryPct) ? "" : $" · 电量 {batteryPct:0}%";
        SamplerLog.Write("power", $"供电切换：{PowerName(prev.Value)} → {PowerName(onAc)}{pct}");
    }

    private static string PowerName(bool onAc) => onAc ? "交流" : "电池";

#if PUSH_ENABLED
    /// <summary>
    /// 到点检查一次该不该推（D13 / P4.1）：启用中、且上次推送的业务日期早于应推日期 → 后台推一帧。
    ///
    /// 【为什么每 60 秒而不是每拍】采样 5 秒一拍，推送是每天一次的活；60 秒足以把"到点后多久
    /// 推出去"的误差压在一分钟内，而判定只是读个小状态文件 + 两次比较。推成之后 T 不再前进，
    /// 当天剩余检查恒为假——"已推过"天然惰性，不需要额外跳过逻辑。
    /// 【为什么必须后台】离线时重试链要走一分多钟，inline 在采样循环里会把采样堵住——
    /// 采样数据比推送重要，这是底线。
    /// 【为什么读盘上的新 settings】启用开关 / 推送时刻都是 GUI 改的（另一个实例写盘），
    /// 本服务的 _settings 是构造时快照，不重读就永远看不到用户刚点的"启用"。
    /// </summary>
    private void TryScheduledPush()
    {
        if (_pushRunning) return;

        try
        {
            var s = SettingsStore.Load();
            if (!s.Push.Enabled) return;
            if (!PushService.ShouldPushNow(s)) return;

            _pushRunning = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    // force: false —— 调度走幂等；用户明确点击的手动推才是 force（D12）
                    var outcome = await PushService.PushAsync(s, force: false).ConfigureAwait(false);
                    ConsoleHost.WriteLine(outcome.Ok
                        ? $"[eink] 每日推送完成：{outcome.Message}"
                        : $"[eink] 每日推送未完成：{outcome.Message}");
                }
                catch (Exception ex)
                {
                    ConsoleHost.WriteError($"[eink] 每日推送异常（不影响采样）：{ex.Message}");
                }
                finally
                {
                    _pushRunning = false;
                }
            });
        }
        catch (Exception ex)
        {
            // 连"检查"本身都不能影响采样
            ConsoleHost.WriteError($"[warn] 推送检查失败（不影响采样）：{ex.Message}");
        }
    }
#endif

    /// <summary>
    /// 读一次 SSD 寿命计数器并落盘（一天一行，幂等）。
    ///
    /// 【为什么必须 UpdateStorage】常规刷新跳过了存储硬件，不单独刷一次，
    /// 读到的就是启动时那个陈旧值 —— 那样一天一次的记录全是同一个数，等于没记。
    /// 【失败纪律】任何异常都不往上报：这是锦上添花的记录，绝不能影响采样主循环；
    /// 也不重试，按天节流已经挡住了"反复打扰硬件"。
    /// </summary>
    private void TryRecordDriveHealth(DateOnly day)
    {
        try
        {
            _hw.UpdateStorage();
            var health = _hw.ReadDriveHealth(day);
            if (health.IsEmpty) return;   // 一个计数器都没读到，写一行空数据没有意义

            if (DriveHealthStore.AppendOnce(health))
            {
                string S(double v) => double.IsNaN(v) ? "—" : $"{v:0.#}";
                ConsoleHost.WriteLine($"[info] SSD 寿命已记录：已用 {S(health.PercentageUsed)}%"
                    + $" / 备用 {S(health.AvailableSpare)}% / 通电 {S(health.PowerOnHours)} 小时");
            }
        }
        catch (Exception ex)
        {
            ConsoleHost.WriteError($"[warn] SSD 寿命记录失败（不影响采样）: {ex.Message}");
        }
    }

    /// <summary>
    /// 刷新环境气温。首次运行时若未设置位置则先做一次 IP 粗定位并写回配置。
    /// 全程异步且吞掉所有异常——网络不可用绝不能影响采样主流程。
    /// </summary>
    private async Task RefreshWeatherAsync()
    {
        var amb = _settings.Ambient;
        try
        {
            if (amb.Latitude == 0 && amb.Longitude == 0)
            {
                var geo = await AmbientWeather.LocateAsync().ConfigureAwait(false);
                if (geo is null)
                {
                    ConsoleHost.WriteLine("[info] 自动定位失败，环境温度将使用本地代理值。"
                        + "可在 GUI 的『设置』页手动填写城市坐标。");
                    return;
                }
                SettingsStore.UpdateAmbientLocation(geo);
                ConsoleHost.WriteLine($"[info] 已自动定位到 {geo.Name} ({geo.Latitude:F2}, {geo.Longitude:F2})");
                amb = SettingsStore.Load().Ambient;
            }

            var n = await AmbientWeather
                .FetchAsync(new AmbientWeather.Geo(amb.Latitude, amb.Longitude, amb.LocationName),
                            amb.BackfillDays)
                .ConfigureAwait(false);
            if (n > 0)
                ConsoleHost.WriteLine($"[info] 环境气温缓存已更新（新增 {n} 条）");
        }
        catch
        {
            // 断网/超时：降级为本地代理，不打扰用户
        }
    }

    private void Shutdown()
    {
        // 只刷快照，不写 CSV：下次启动接着累加，片边界一次性落盘。
        try { _agg.Shutdown(); }
        catch { /* 退出阶段不再抛异常 */ }
    }

    public void Cancel()
    {
        StopRequested = true;
        _cts.Cancel();
    }

    public void Dispose()
    {
        StopRequested = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
