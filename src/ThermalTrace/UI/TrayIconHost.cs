using ThermalTrace.Core;
using ThermalTrace.Model;
using ThermalTrace.Services;

namespace ThermalTrace.UI;

/// <summary>
/// 极简托盘宿主。
///
/// 【与被否决的"实时仪表盘"的区别】
/// LiteMonitor 已经提供了实时温度显示和提醒，本托盘<b>刻意不重复</b>：
///  - 没有实时曲线、没有大数字、没有阈值告警气泡
///  - 只做两件事：tooltip 显示"今天大概怎么样"（这是LiteMonitor 没有的），
///    以及提供"打开主窗口 / 退出"入口
///
/// 【为什么需要托盘而不是纯后台】
/// 后台进程必须让用户知道它在运行、能随时找到并关掉。
/// 否则用户会担心"是不是偷偷在写什么东西"。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SamplerService _sampler;
    private readonly NotifyIcon _notifyIcon = new();
    private readonly ToolStripMenuItem _statusItem = new("正在启动…");
    private readonly ToolStripMenuItem _todayItem = new("今日概览");
    private readonly ToolStripMenuItem _restartItem = new("采样状态 / 重启");
    private bool _disposed;
    private bool _selfChecking;
    private bool _stallLogged;

    private const string EmptySelfCheckMessage = "自检未产生输出。请确认已以管理员权限运行。";

    public TrayIconHost(AppSettings settings, SamplerService sampler)
    {
        _settings = settings;
        _sampler = sampler;
    }

    public void Start()
    {
        _notifyIcon.Icon = AppIcon.Get();
        _notifyIcon.Visible = true;
        _notifyIcon.Text = "ThermalTrace — 长期趋势记录";

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_todayItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开主窗口", null, (_, _) => MainWindowHost.Toggle());
        menu.Items.Add("重新自检…", null, (_, _) => RunSelfCheck());
        _restartItem.Click += (_, _) => RestartSampler();
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            // 【2026-10-09 修复】主线程现在跑 Application.Run() 消息循环，
            // 仅 Cancel 采样线程不会退出程序，需同时退出消息循环。
            _sampler.Cancel();
            Application.Exit();
        });

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => MainWindowHost.Toggle();
        _notifyIcon.BalloonTipClicked += (_, _) => MainWindowHost.Toggle();

        // 每 10 秒刷新一次 tooltip 摘要（不打扰，无需高频）
        var timer = new System.Windows.Forms.Timer { Interval = 10_000 };
        timer.Tick += (_, _) => RefreshTooltip();
        timer.Start();

        RefreshTooltip();
        MaybeShowTrayHint();
    }

    /// <summary>
    /// 首次启动的一次性气泡：告诉用户"程序没闪退，它在托盘里"。
    ///
    /// 【为什么必须有】Background() 刻意不开窗口（后台常驻是产品定位），Win11 还会把
    /// 新托盘图标收进任务栏「^」折叠区。beta 用户的真实反馈是"打开然后就闪退了"——
    /// 程序其实一直在采样，只是屏幕上没有任何可见反馈。气泡是唯一不改变产品定位、
    /// 又能消除这个误解的手段。见 <see cref="Model.UiOptions.ShowTrayHint"/>。
    ///
    /// 【为什么用 Timer 而不是 Start() 里直接弹】托盘刚 Visible=true 就弹气泡，
    /// 部分 Windows 版本会直接丢弃（图标还没在托盘里站稳）。WinForms Timer 的 Tick
    /// 要等消息循环转起来才到，与 SetupHint 同样的理由。
    ///
    /// 【只弹一次】不管用户点不点气泡，都落盘关掉标记；反复弹同一条提示只会让人想卸载。
    /// </summary>
    private void MaybeShowTrayHint()
    {
        if (!_settings.Ui.ShowTrayHint) return;

        var timer = new System.Windows.Forms.Timer { Interval = 3_000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            try
            {
                _notifyIcon.ShowBalloonTip(
                    15_000,
                    "ThermalTrace 已在后台运行",
                    "它没有闪退——正在安静地记录散热数据，平时不弹任何窗口。\n"
                        + "双击右下角托盘图标（Win11 可能在「^」折叠区里）即可打开主窗口。",
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                // 气泡被系统策略禁用时不值得让启动失败：标记照关，不再重试
                ConsoleHost.WriteError($"[warn] 托盘提示气泡显示失败: {ex.Message}");
            }
            MarkTrayHintShown();
        };
        timer.Start();
    }

    private void MarkTrayHintShown()
    {
        try
        {
            var s = SettingsStore.Load();
            s.Ui = s.Ui with { ShowTrayHint = false };
            SettingsStore.Save(s);
        }
        catch (Exception ex)
        {
            // 标记没写掉只会多弹一次，不值得打断运行（与 SetupHint 同一取舍）
            ConsoleHost.WriteError($"[warn] 托盘提示标记保存失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 托盘「重新自检…」。
    ///
    /// 【为什么不能直接调 Commands.SelfCheck()】它只往控制台写，而程序是 WinExe
    /// （GUI 子系统），托盘常驻时根本没有控制台 —— 点了菜单什么都没发生，
    /// 用户只能得出"这个菜单是坏的"。这里改成弹窗，并且先把主窗口叫出来，
    /// 这样弹窗有父窗口、用户也顺带看见了程序本身。
    ///
    /// 【为什么要后台线程】自检要连续采样几十秒，直接在 UI 线程调会把整个程序卡死
    /// （含托盘菜单），用户会以为程序死了。
    /// </summary>
    private void RunSelfCheck()
    {
        if (_selfChecking) return;
        _selfChecking = true;

        MainWindowHost.Show();   // 先给弹窗一个父窗口；顺带让用户看见程序

        // 后台采集与截断在 MaintenanceActions 里，与概览页、设置页共用同一份实现。
        MaintenanceActions.RunSelfCheck(EmptySelfCheckMessage, FinishSelfCheck);
    }

    private void FinishSelfCheck(string text)
    {
        if (_disposed) return;
        if (_notifyIcon.ContextMenuStrip is { } menu && menu.InvokeRequired)
        {
            menu.BeginInvoke(new Action(() => FinishSelfCheck(text)));
            return;
        }

        _selfChecking = false;
        MessageBox.Show(MainWindowHost.OwnerForm, text, "ThermalTrace — 传感器自检",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RefreshTooltip()
    {
        var s = _sampler.Latest;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var rows = Storage.DailyStore.ReadRange(today, today);

        var stall = CheckSamplerHealth();

        string line1 = stall.Length > 0
            ? stall
            : s is null
                ? "等待传感器数据…"
                : $"当前 {s.PrimaryCpuC:0}C · 单核峰值 {s.MaxCoreUtilPct:0}% · 采样 {_sampler.SampleCount} 次";

        string line2 = rows.Count == 0
            ? "今日暂无落盘数据"
            : $"今日 {rows.Count} 个时间片 · 记录时长约 {EstimateMinutes(rows)} 分钟";

        _statusItem.Text = Trim(line1, 60);
        _todayItem.Text = Trim(line2, 60);
        _notifyIcon.Text = Trim("ThermalTrace\n" + line1, 127);
    }

    /// <summary>
    /// 看门狗：托盘 tooltip 每 10 秒刷一次，顺带检查采样心跳。
    /// 线程已退出 → 自动重拉；线程还活着但心跳超时 → 上屏告警，不强杀线程。
    /// 2026-11-06 实机采样静默停摆 45 分钟，界面却毫无提示，这一层就是补这个洞。
    /// </summary>
    private string CheckSamplerHealth()
    {
        // 托盘 Start() 早于 Commands.Background() 里的 sampler.Start()：
        // 启动头 15 秒不做判定，免得托盘把采样线程提前拉起或误报停摆。
        if (_sampler.StopRequested || _sampler.StartedAt > DateTimeOffset.Now.AddSeconds(-15))
            return "";

        if (!_sampler.IsRunning)
        {
            if (_sampler.TryRestart())
                SamplerLog.Write("watchdog", "检测到采样线程已退出，自动重启");
            return "";
        }

        var threshold = StallThreshold();
        var age = _sampler.HeartbeatAge;
        if (age <= threshold)
        {
            _stallLogged = false;
            return "";
        }

        if (!_stallLogged)
        {
            _stallLogged = true;
            SamplerLog.Write("stall",
                $"心跳停止 {(int)age.TotalSeconds}s，阶段={_sampler.CurrentPhase}，" +
                $"线程={_sampler.ThreadStateText}，LastError={_sampler.LastError}");
        }
        return $"采样已停止 {(int)age.TotalMinutes} 分钟，请在托盘点『退出』后重新打开程序";
    }

    private TimeSpan StallThreshold() =>
        TimeSpan.FromSeconds(Math.Max(30, _settings.Sampler.IntervalSeconds * 3));

    private void RestartSampler()
    {
        if (_sampler.TryRestart())
        {
            _stallLogged = false;
            MessageBox.Show(MainWindowHost.OwnerForm, "采样线程已重新拉起，下一拍开始恢复记录。",
                "ThermalTrace — 采样状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var age = _sampler.HeartbeatAge;
        var text = age > StallThreshold()
            ? "采样线程仍在运行但没有心跳，无法安全重启。请在托盘点『退出』后重新打开程序。"
            : $"采样正常，最近一次心跳 {(int)age.TotalSeconds} 秒前，累计 {_sampler.SampleCount} 次采样。";
        MessageBox.Show(MainWindowHost.OwnerForm, text,
            "ThermalTrace — 采样状态", MessageBoxButtons.OK,
            age > StallThreshold() ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    /// <summary>NotifyIcon.Text 上限 127 字符，超出会抛异常，必须截断。</summary>
    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// 用采样数反推有效记录时长（样本数 × 采样间隔）。
    /// 断电/休眠期间没有样本，所以这个值会小于日历时间——正好反映了真实覆盖率。
    /// </summary>
    private int EstimateMinutes(IReadOnlyList<Model.SliceRow> rows) =>
        (int)(rows.Sum(r => r.SampleCount)
              * Math.Max(1, _settings.Sampler.IntervalSeconds) / 60);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
