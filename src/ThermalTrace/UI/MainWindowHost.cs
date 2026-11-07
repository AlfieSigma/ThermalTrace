using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ThermalTrace.Analysis;
using ThermalTrace.Core;
using ThermalTrace.Model;
using ThermalTrace.Services;
using ThermalTrace.UI.Tabs;

namespace ThermalTrace.UI;

/// <summary>
/// 主窗口宿主：900×600 的四标签窗体（今日概览 / 趋势分析 / 解读 / 设置）。
///
/// 【入口签名不许动】TrayIconHost 里 MainWindowHost.Toggle() 的调用一行都不能改：
/// 托盘右键菜单与双击都走这里，改签名会连带炸掉托盘。
///
/// 【为什么点 X 只是隐藏】托盘常驻程序里关窗 ≠ 退出进程。若真的 Close()，Form 会被释放，
/// 下次再 Show() 抛 ObjectDisposedException，托盘入口从此点不开——这是不能接受的回归。
/// 退出只认托盘菜单那条路（Application.Exit，CloseReason = ApplicationExitCall）。
///
/// 【不做的事】实时仪表盘 / 大数字 / 告警气泡一律不做——那是 LiteMonitor 的活（产品边界）。
/// 本窗口只负责"看长期结果"和"改配置"。
/// </summary>
internal static class MainWindowHost
{
    private static Form? _form;
    private static SamplerService? _sampler;

    /// <summary>本次打开内还没刷新的页。首次切过去时才付各自的取数成本（开窗提速 C 项）。</summary>
    private static readonly HashSet<TabPage> _pendingRefresh = new();

    // ------------------------------------------------------------------
    // 一次打开内共享的趋势取数（开窗提速 B 项）
    //
    // 趋势页与解读页默认都看近 7 天，原先各自 TrendAnalyzer.Build + HardwareTrend.Build；
    // 本层按「窗口 + 供电 + 设置版本 + 数据版本」作为键，同键在一侧打开期间只构建一次。
    // 【不做长期缓存】窗口隐藏 / 下次打开即清空，不进磁盘、不跨进程。
    // ------------------------------------------------------------------

    private static readonly Dictionary<(TrendWindow Window, bool OnAc), TrendReport> _trendReports = new();
    private static readonly Dictionary<TrendWindow, HardwareTrendReport> _hardwareReports = new();

    /// <summary>当前这批缓存对应的设置与数据版本；变了就整批作废，避免同一次打开内显示旧数。</summary>
    private static string _trendStamp = "";

    /// <summary>
    /// <see cref="Show"/> 正在执行 Show() + 当前页刷新这一段。
    ///
    /// 【为什么需要它】WinForms 在窗口首次显示时会连续给当前标签页发两次 VisibleChanged，
    /// 若放行会让当前页在同一轮打开里重读两次（G4.5）。各页的 OnVisibleChanged 在打开期间
    /// 跳过自身的刷新，统一由 <see cref="RefreshCurrentPage"/> 刷当前页一次；
    /// 切页时不处于打开期间，交给待刷集合决定是否取数。
    /// </summary>
    internal static bool IsOpening { get; private set; }

    /// <summary>
    /// 挂上当前采样服务，由 <c>Commands.Background()</c> 在托盘启动前调用一次。
    ///
    /// 【为什么不加参数】红线：<see cref="Toggle"/> 的签名一行都不能改 —— 托盘菜单与双击都走它。
    /// 两个页签的「重新绑定传感器」需要采样服务把新绑定热换进正在跑的循环，
    /// 所以只能从托盘创建链路上把实例挂过来，而不是顺着 Toggle 往下传。
    /// </summary>
    public static void Attach(SamplerService sampler) => _sampler = sampler;

    /// <summary>
    /// 当前主窗口（可能为 null 或已释放），供托盘菜单弹窗挂父窗口用。
    /// MessageBox 传 null 也能显示，但没有父窗口时会出现在屏幕正中而不是贴着主窗口，
    /// 多窗时容易被误认成别的程序的对话框。
    /// </summary>
    public static IWin32Window? OwnerForm => _form is { IsDisposed: false } ? _form : null;

    /// <summary>托盘「打开主窗口」/ 双击托盘图标的入口。可见则隐藏，否则显示。</summary>
    public static void Toggle()
    {
        if (_form is { Visible: true, IsDisposed: false })
        {
            _form.Hide();
            ResetTrendCache();
            return;
        }
        Show();
    }

    public static void Show()
    {
        // 用户关过一次窗（或被退出流程释放过）后 _form 已 Dispose，必须按需重建，
        // 否则这里会抛 ObjectDisposedException。
        _form = _form is { IsDisposed: false } ? _form : BuildForm();

        // 每次打开都是一批新缓存：隐藏期间采样仍在落盘、设置也可能被改过，
        // 上次打开的报告不能当现况（B 项口径）。
        ResetTrendCache();

        IsOpening = true;
        try
        {
            // 每次打开都把四页重新标成待刷：隐藏期间采样仍在落盘，旧内容不能当现况。
            _pendingRefresh.Clear();
            foreach (var page in AllPages(_form)) _pendingRefresh.Add(page);

            _form.Show();
            _form.WindowState = FormWindowState.Normal;

            // 设置页的 DateTimePicker 在开窗阶段预建原生句柄并预热首次赋值（实测 143–184 ms），
            // 这笔成本不再落在"首次点设置页"的点击路径上；控件外观与交互不变。
            foreach (var page in AllPages(_form))
                if (page is SettingsTab settings) settings.WarmUp();

            RefreshCurrentPage(_form);
        }
        finally
        {
            IsOpening = false;
        }
        _form.Activate();
    }

    /// <summary>
    /// 打开窗口时只同步刷当前页（通常停在今日概览），其余页留到首次切过去再刷。
    /// 【为什么必须有这一步】窗口是"隐藏而非销毁"，不刷的话用户看到的还是几小时前的卡片
    /// （采样每 30 分钟才落一片）。把趋势 / 解读 / 设置页的取数成本延后，开窗只需等当前页。
    /// </summary>
    private static void RefreshCurrentPage(Form form)
    {
        if (CurrentPage(form) is not { } page) return;
        RefreshPage(page);
        _pendingRefresh.Remove(page);
    }

    /// <summary>
    /// 页签首次可见时由各页 OnVisibleChanged 调用：只有本次打开里还没刷过的页才取数；
    /// 已刷过的页切回来不重复取（C 项口径），开窗期间的重复可见事件由 <see cref="IsOpening"/> 挡住。
    /// </summary>
    internal static void OnTabActivated(TabPage page)
    {
        if (!_pendingRefresh.Remove(page)) return;
        RefreshPage(page);
    }

    /// <summary>
    /// 设置页改了会影响其它页数据的口径（负载类别边界 / 清灰基准日）后调用：
    /// 把当前页以外的页重新标脏，切过去时按新口径取数，不让已访问过的页继续显示旧值。
    /// </summary>
    internal static void MarkDataStale()
    {
        if (_form is not { IsDisposed: false }) return;
        var current = CurrentPage(_form);
        foreach (var page in AllPages(_form))
            if (page != current) _pendingRefresh.Add(page);
    }

    private static TabPage? CurrentPage(Form form)
    {
        foreach (var tabs in form.Controls.OfType<TabControl>())
            return tabs.SelectedTab ?? (tabs.TabPages.Count > 0 ? tabs.TabPages[0] : null);
        return null;
    }

    private static IEnumerable<TabPage> AllPages(Form form) =>
        form.Controls.OfType<TabControl>().SelectMany(t => t.TabPages.Cast<TabPage>());

    private static void RefreshPage(TabPage page)
    {
        if (page is OverviewTab overview) overview.RefreshData();
        else if (page is TrendTab trend) trend.RefreshData();
        else if (page is InterpretationTab interpretation) interpretation.RefreshData();
        // 设置页同样要重读——自启状态是"现在的事实"，不是窗口上次打开时的事实
        else if (page is SettingsTab settings) settings.RefreshData();
    }

    // ------------------------------------------------------------------
    // 共享趋势取数（B 项）
    // ------------------------------------------------------------------

    /// <summary>一次取数结果：趋势报告 + 硬件趋势，同一窗口内两页共用。</summary>
    internal sealed record TrendData(TrendReport Report, HardwareTrendReport Hardware);

    /// <summary>
    /// 趋势页 / 解读页的取数入口（两者都要硬件趋势时用）。同键（窗口＋供电＋设置版本＋数据版本）只构建一次；
    /// 不同键各自构建，互不串数。
    /// </summary>
    internal static TrendData GetTrendData(AppSettings settings, TrendWindow window, bool onAc)
    {
        var report = GetSharedReport(settings, window, onAc);
        return new TrendData(report, GetHardwareTrend(settings, window));
    }

    /// <summary>
    /// 只取趋势报告。设置页的槽 2 适用性也走这里：它只用报告里的 Days / FirstDay / LastDay / OnAc，
    /// 与窗口无关，因此和趋势页默认的近 7 天键共用同一份构建结果。
    /// </summary>
    internal static TrendReport GetSharedReport(AppSettings settings, TrendWindow window, bool onAc)
    {
        EnsureTrendStamp(settings);

        var key = (window, onAc);
        if (!_trendReports.TryGetValue(key, out var report))
        {
            report = TrendAnalyzer.Build(settings, window, onAc);
            _trendReports[key] = report;
        }
        return report;
    }

    /// <summary>
    /// 硬件趋势的按需构建：趋势页折线 / 柱状视图不调用它，只有硬件视图与解读卡片需要。
    /// 同一次打开内按窗口只构建一次，其次切回直接复用（与 B 项报告共享层同一批缓存、同一个版本戳）。
    /// </summary>
    internal static HardwareTrendReport GetHardwareTrend(AppSettings settings, TrendWindow window)
    {
        EnsureTrendStamp(settings);

        if (!_hardwareReports.TryGetValue(window, out var hardware))
        {
            hardware = HardwareTrend.Build(settings, window);
            _hardwareReports[window] = hardware;
        }
        return hardware;
    }

    /// <summary>只看共享层里有没有现成硬件趋势，不构建（折线 / 柱状视图刷新时用）。</summary>
    internal static HardwareTrendReport? PeekHardwareTrend(AppSettings settings, TrendWindow window)
    {
        EnsureTrendStamp(settings);
        return _hardwareReports.GetValueOrDefault(window);
    }

    /// <summary>
    /// 只看共享层里有没有现成报告，不构建。设置页的槽 2 适用性用它：命中就复用 B 的同一份，
    /// 未命中则改走轻量扫描，不为一行状态整份重建。
    /// </summary>
    internal static TrendReport? PeekSharedReport(AppSettings settings, TrendWindow window, bool onAc)
    {
        EnsureTrendStamp(settings);
        return _trendReports.GetValueOrDefault((window, onAc));
    }

    /// <summary>
    /// 设置与数据的版本戳变了就整批作废（报告与硬件趋势一起），避免同一次打开内显示旧数。
    /// </summary>
    private static void EnsureTrendStamp(AppSettings settings)
    {
        var stamp = TrendCacheStamp(settings);
        if (stamp == _trendStamp) return;
        ResetTrendCache();
        _trendStamp = stamp;
    }

    /// <summary>清空共享层：每次隐藏 / 打开都调用，避免把上次打开的报告当现况，也及时释放内存。</summary>
    private static void ResetTrendCache()
    {
        _trendReports.Clear();
        _hardwareReports.Clear();
        _trendStamp = "";
    }

    /// <summary>
    /// 设置与数据的版本戳。设置侧只收会影响 TrendAnalyzer.Build 结果的字段；
    /// 数据侧用 daily\ 与 health\ 文件的（数量, 总长度, 总写入时间），
    /// 与 DailyStore.ReadFileCached 的失效判据同源，落盘后下一次取数必然换新。
    /// </summary>
    private static string TrendCacheStamp(AppSettings settings)
    {
        var bounds = settings.CategoryLowerBounds is { Length: > 0 } b ? string.Join(",", b) : "";
        var names = settings.CategoryNames is { Length: > 0 } n ? string.Join("|", n) : "";
        return $"{bounds}#{names}#{settings.Sampler.IntervalSeconds}#{settings.CleanDateIso}#{DataStamp()}";
    }

    private static string DataStamp()
    {
        long count = 0, length = 0, ticks = 0;

        void Scan(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.EnumerateFiles(dir, "*.csv"))
                {
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;
                    count++;
                    length += info.Length;
                    ticks += info.LastWriteTimeUtc.Ticks;
                }
            }
            catch
            {
                // 单个目录读不到就按"没有变化"处理；取数本身仍会照常读 CSV
            }
        }

        Scan(AppPaths.DailyDir);
        Scan(AppPaths.HealthDir);
        return $"{count}/{length}/{ticks}";
    }

    private static Form BuildForm()
    {
        Theme.Initialize();

        var form = new Form
        {
            Text = "ThermalTrace — 散热长期趋势记录",
            Icon = AppIcon.Get(),
            Size = new Size(900, 600),          // 1080p 下单页放下，不滚动
            MinimumSize = new Size(760, 520),   // 尺寸下限是硬性约束；MaximumSize 不设
            StartPosition = FormStartPosition.CenterScreen
        };
        RestoreBounds(form);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            // 【为什么必须自绘标签头】系统 TabControl 的头读视觉样式色，刷不了主题色，
            // 深色模式下会是白底黑字的一块。
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(116, 30),
            Padding = new Point(8, 4)
        };
        tabs.DrawItem += DrawTabHeader;

        // ItemSize 不参与 AutoScale，高 DPI 下字号变大了而头不变，中文会被挤掉。
        // 句柄建好（此时 DeviceDpi 才已知）后按实际 DPI 换算一次。
        tabs.HandleCreated += (_, _) =>
        {
            var s = tabs.DeviceDpi <= 0 ? 1f : tabs.DeviceDpi / 96f;
            tabs.ItemSize = new Size((int)Math.Round(116 * s), (int)Math.Round(30 * s));
        };

        // 四页：趋势页与解读页各自持有一份范围状态（G8），切换一页不改变另一页的选择。
        // 采样服务可能为 null（单测/诊断路径直接建窗口），页签拿到 null 就退化为
        // "绑定后需重启程序才生效"，不能因此崩掉。
        var trendTab = new TrendTab();
        var interpretationTab = new InterpretationTab();

        tabs.TabPages.Add(new OverviewTab(_sampler));
        tabs.TabPages.Add(trendTab);
        tabs.TabPages.Add(interpretationTab);
        tabs.TabPages.Add(new SettingsTab(_sampler));

        form.Controls.Add(tabs);

        // 点 X → 隐藏；只有 Application.Exit（托盘「退出」）传来的 ApplicationExitCall 才真关。
        // 两条路都会走到这里，位置/大小就在这一个地方记 —— 不用再挂 ResizeEnd / Disposed。
        form.FormClosing += (_, e) =>
        {
            SaveBounds(form);
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                form.Hide();
                ResetTrendCache();
            }
        };

        EventHandler onThemeChanged = (_, _) => Reapply(form, tabs);
        Theme.Changed += onThemeChanged;
        form.Disposed += (_, _) => Theme.Changed -= onThemeChanged;

        Theme.Apply(form);
        tabs.Invalidate();   // 标签头是自绘的，刷完色要显式让它重画
        return form;
    }

    // ------------------------------------------------------------------
    // 窗口位置 / 大小记忆（存 settings.json 的 ui.windowXxx）
    // ------------------------------------------------------------------

    /// <summary>
    /// 恢复上次关闭时的位置与大小。记过就用记的，没记过（首次运行）保持 900×600 居中。
    ///
    /// 【为什么不无条件信】位置是"上次那台显示器布局"下的产物：拔掉外接屏、改分辨率、
    /// 换扩展模式之后，原坐标可能落在任何一块屏幕之外 —— 窗口会开在看不见的地方，
    /// 用户只能从托盘反复点却什么都看不到。所以必须与某一块当前屏幕有足够重叠才认，
    /// 否则安静地退回默认（居中 + 900×600），不做任何提示。
    /// </summary>
    private static void RestoreBounds(Form form)
    {
        UiOptions ui;
        try
        {
            ui = SettingsStore.Load().Ui;
        }
        catch
        {
            return;   // 配置损坏时按"没记过"处理，不该让窗口打不开
        }

        if (!ui.HasWindowBounds) return;

        var rect = new Rectangle(ui.WindowX, ui.WindowY, ui.WindowWidth, ui.WindowHeight);

        // 与任一屏幕的工作区（不含任务栏）重叠到 120px 以上才算"看得见"
        const int minVisible = 120;
        var visible = Screen.AllScreens.Any(s =>
        {
            var hit = Rectangle.Intersect(s.WorkingArea, rect);
            return hit.Width >= minVisible && hit.Height >= minVisible;
        });
        if (!visible) return;

        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = rect;
    }

    /// <summary>
    /// 把当前位置与大小写回 settings.json。
    /// 最小化 / 最大化时不记：那两种状态下 <c>Bounds</c> 是屏幕尺寸或恢复位，
    /// 记下来会让下次打开变成"整屏"或"缩在角落"。
    /// </summary>
    private static void SaveBounds(Form form)
    {
        if (form.IsDisposed || form.WindowState != FormWindowState.Normal) return;

        var b = form.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;

        try
        {
            var s = SettingsStore.Load();
            s.Ui = s.Ui with
            {
                WindowX = b.X,
                WindowY = b.Y,
                WindowWidth = b.Width,
                WindowHeight = b.Height
            };
            SettingsStore.Save(s);
        }
        catch (Exception ex)
        {
            // 记不住位置不是错误，绝不能因此打断关闭流程
            ConsoleHost.WriteError($"[warn] 窗口位置保存失败（下次仍居中打开）: {ex.Message}");
        }
    }

    /// <summary>
    /// 自绘标签头。选中页用窗口底色（与页内容连成一体）+ 顶部一条强调色，
    /// 未选中用 Surface 压一层并降为次要文字色——两套配色下都能一眼看出当前在哪一页。
    /// </summary>
    private static void DrawTabHeader(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tab || e.Index < 0 || e.Index >= tab.TabCount) return;

        var p = Theme.Current;
        var g = e.Graphics;
        var rect = e.Bounds;
        var selected = (e.State & DrawItemState.Selected) != 0;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using (var face = new SolidBrush(selected ? p.Bg : p.Surface))
            g.FillRectangle(face, rect);

        // 最后一个标签右侧那条空带不属于任何 e.Bounds，补刷底色，
        // 否则深色主题下会露出一块系统白。
        if (e.Index == tab.TabCount - 1 && rect.Right < tab.Width)
        {
            using var rest = new SolidBrush(p.Bg);
            g.FillRectangle(rest, rect.Right, rect.Top, tab.Width - rect.Right, rect.Height + 2);
        }

        using (var rule = new Pen(p.Border, 1f))
            g.DrawLine(rule, rect.Left, rect.Bottom - 1, rect.Right - 1, rect.Bottom - 1);

        if (selected)
        {
            using var accent = new Pen(p.Accent, 3f);
            g.DrawLine(accent, rect.Left + 1, rect.Top + 1, rect.Right - 2, rect.Top + 1);
        }

        using var brush = new SolidBrush(selected ? p.Text : p.TextDim);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(tab.TabPages[e.Index].Text, tab.Font, brush, rect, sf);
    }

    /// <summary>主题切换后整窗重刷。自绘标签头不受 Theme.Apply 管，必须单独让它重画。</summary>
    private static void Reapply(Form form, TabControl tabs)
    {
        if (form.IsDisposed) return;

        // SystemEvents 通常回调在 UI 线程，但不保证；跨线程时切回窗口线程再刷。
        if (form.IsHandleCreated && form.InvokeRequired)
        {
            form.BeginInvoke(new Action(() => Reapply(form, tabs)));
            return;
        }

        Theme.Apply(form);
        tabs.Invalidate();
        form.Invalidate(true);
    }

}
