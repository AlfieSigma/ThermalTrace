using System.Drawing.Drawing2D;
using System.Globalization;
using ThermalTrace.Analysis;
using ThermalTrace.Core;
using ThermalTrace.Model;
using ThermalTrace.UI.Charts;

namespace ThermalTrace.UI.Tabs;

/// <summary>
/// 「趋势分析」页：顶部切换折线 / 柱状 + 时间范围，下面是自绘图，底部一行结论。
///
/// 【取值规则】
///  - 折线：X = 日期，Y = ΔT，每个负载类别一条线（Cool→Hot 语义色）
///  - 柱状：直接用 <c>TrendReport.Comparison</c> 的 <c>CurDeltaT</c> / <c>PrevDeltaT</c>，
///    <c>Comparable == false</c> 的类别跳过，不画空柱
///  - 不足 3 天<b>不画图</b>，整块渲染成中文空态（依据 <c>TrendSlope.HasTrend => Points >= 3</c>）
///
/// 【数据从哪来】全部走 <c>TrendAnalyzer.Build(settings, window)</c>，与 CLI <c>--trend</c> 同源。
/// 本类只负责取数、配色、排版，不碰任何计算。
///
/// 【为什么窗口要传给后端而不是前端裁剪】柱状图要的是"两个窗口的加权聚合"，
/// 不是"取最后 N 天"——聚合必须在后端按样本数加权做，前端裁剪出来的数字会和 --trend 对不上。
/// </summary>
internal sealed class TrendTab : ThemedTabPage
{
    private readonly TableLayoutPanel _root;
    private readonly FlowLayoutPanel _toolbar;
    private readonly AccentButton _lineButton;
    private readonly AccentButton _barButton;
    private readonly AccentButton _hardwareButton;
    private readonly AccentButton _rangeWeek;
    private readonly AccentButton _rangeMonth;
    private readonly AccentButton _rangeAll;
    private readonly AccentButton _powerAc;
    private readonly AccentButton _powerDc;

    private readonly Panel _stageHost;
    private readonly Panel _stage;
    private readonly LineChart _lineChart;
    private readonly BarChart _barChart;
    private readonly TableLayoutPanel _hardwareHost;
    private readonly LineChart _gpuChart;
    private readonly LineChart _ssdChart;

    private readonly TableLayoutPanel _emptyPanel;
    private readonly Label _emptyMain;
    private readonly Label _emptySub;

    private readonly CategoryUsageStrip _usageStrip;
    private readonly Label _footer;

    /// <summary>分档时长条带的高度；隐藏时整行收为 0，图表占满。</summary>
    private const float UsageRowHeight = 80f;

    /// <summary>趋势页的三个视图（3.3 加入「硬件」）。</summary>
    private enum TrendView { Line, Bar, Hardware }

    private TrendView _view = TrendView.Line;
    private TrendWindow _window = TrendWindow.Week;
    private bool _onAc = true;
    private AppSettings? _settings;
    private TrendReport? _report;
    private HardwareTrendReport? _hardware;

    public TrendTab()
    {
        Text = "趋势分析";
        UseVisualStyleBackColor = false;
        BackColor = Theme.Current.Bg;
        ForeColor = Theme.Current.Text;
        Padding = new Padding(16);

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));   // 工具条
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 图表
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, UsageRowHeight)); // 分档时长
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));   // 结论行

        // ---------------- 工具条 ----------------
        _toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };

        _toolbar.Controls.Add(TextLabel("视图"));
        _lineButton = new AccentButton { Text = "折线", Width = 64, Height = 28, Primary = true, Margin = new Padding(0, 3, 8, 0) };
        _barButton = new AccentButton { Text = "柱状", Width = 64, Height = 28, Primary = false, Margin = new Padding(0, 3, 8, 0) };
        _hardwareButton = new AccentButton { Text = "硬件", Width = 64, Height = 28, Primary = false, Margin = new Padding(0, 3, 0, 0) };
        _lineButton.Click += (_, _) => SetView(TrendView.Line);
        _barButton.Click += (_, _) => SetView(TrendView.Bar);
        _hardwareButton.Click += (_, _) => SetView(TrendView.Hardware);
        _toolbar.Controls.AddRange([_lineButton, _barButton, _hardwareButton]);

        _toolbar.Controls.Add(Spacer(16));
        _toolbar.Controls.Add(TextLabel("范围"));

        _rangeWeek = new AccentButton { Text = "近 7 天", Width = 66, Height = 28, Primary = true, Margin = new Padding(0, 3, 8, 0) };
        _rangeMonth = new AccentButton { Text = "近 30 天", Width = 76, Height = 28, Primary = false, Margin = new Padding(0, 3, 8, 0) };
        _rangeAll = new AccentButton { Text = "全部", Width = 56, Height = 28, Primary = false, Margin = new Padding(0, 3, 0, 0) };
        _rangeWeek.Click += (_, _) => SetRange(TrendWindow.Week);
        _rangeMonth.Click += (_, _) => SetRange(TrendWindow.Month);
        _rangeAll.Click += (_, _) => SetRange(TrendWindow.All);
        _toolbar.Controls.AddRange([_rangeWeek, _rangeMonth, _rangeAll]);

        // 供电切换：与今日概览同一套按钮语义。工具条 WrapContents=false 且行高固定，
        // 加这两枚按钮后整排约 680px，收窄上方按钮留出余量，760px 最小窗口内不会挤出。
        _toolbar.Controls.Add(Spacer(8));
        _toolbar.Controls.Add(TextLabel("供电"));
        _powerAc = new AccentButton { Text = "交流", Width = 60, Height = 28, Primary = true, Margin = new Padding(0, 3, 8, 0) };
        _powerDc = new AccentButton { Text = "电池", Width = 60, Height = 28, Primary = false, Margin = new Padding(0, 3, 0, 0) };
        _powerAc.Click += (_, _) => SetPowerSource(true);
        _powerDc.Click += (_, _) => SetPowerSource(false);
        _toolbar.Controls.AddRange([_powerAc, _powerDc]);

        // ---------------- 图表区（折线 / 柱状 / 硬件 三选一，另两个隐藏） ----------------
        _stageHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Theme.Current.Bg };
        _stage = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Theme.Current.Bg };
        _lineChart = new LineChart { Dock = DockStyle.Fill };
        _barChart = new BarChart { Dock = DockStyle.Fill, Visible = false };
        _stage.Controls.Add(_barChart);
        _stage.Controls.Add(_lineChart);

        // 硬件视图：GPU / SSD 两张小图上下排（3.3）。
        // 【为什么是两张而不是一张四线】℃ 与 h、GB 与 % 量纲不同，
        // 四根线挤在同一根 Y 轴上，大量纲的会把小量纲的压成一条贴地直线。
        _hardwareHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg,
            Visible = false
        };
        _hardwareHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _hardwareHost.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        _hardwareHost.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        _gpuChart = new LineChart { Dock = DockStyle.Fill, Padding = new Padding(6, 6, 6, 2) };
        _ssdChart = new LineChart { Dock = DockStyle.Fill, Padding = new Padding(6, 2, 6, 6) };
        _hardwareHost.Controls.Add(_gpuChart, 0, 0);
        _hardwareHost.Controls.Add(_ssdChart, 0, 1);
        _stage.Controls.Add(_hardwareHost);

        // ---------------- 空态 ----------------
        _emptyPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };
        _emptyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _emptyPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));
        _emptyPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
        _emptyPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
        _emptyPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));

        _emptyMain = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0)
        };
        _emptySub = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.TopCenter,
            Margin = new Padding(0)
        };
        _emptyPanel.Controls.Add(_emptyMain, 0, 1);
        _emptyPanel.Controls.Add(_emptySub, 0, 2);
        _emptyPanel.Visible = false;

        _stageHost.Controls.Add(_emptyPanel);
        _stageHost.Controls.Add(_stage);

        // ---------------- 分档时长条带（折线 / 柱状可见，硬件视图收起） ----------------
        _usageStrip = new CategoryUsageStrip
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4)
        };

        // ---------------- 结论行 ----------------
        _footer = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            AutoEllipsis = true
        };

        _root.Controls.Add(_toolbar, 0, 0);
        _root.Controls.Add(_stageHost, 0, 1);
        _root.Controls.Add(_usageStrip, 0, 2);
        _root.Controls.Add(_footer, 0, 3);

        Controls.Add(_root);

        ApplyColors();
    }

    private static Label TextLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 9, 8, 0)
    };

    private static Label Spacer(int width) => new()
    {
        Text = "",
        AutoSize = false,
        Width = width,
        Height = 1,
        Margin = new Padding(0)
    };

    // ------------------------------------------------------------------
    // 取数 / 渲染
    // ------------------------------------------------------------------

    /// <summary>
    /// 重新取数并重画。窗口是"隐藏而非销毁"，每次打开主窗口、每次切回本页都要调一次。
    /// 设置页改了桶边界后切回来也靠它立即重算。
    /// </summary>
    public void RefreshData()
    {
        if (IsDisposed) return;

        try
        {
            _settings = SettingsStore.Load();
            // 趋势报告由宿主共享给解读页，一次打开内只构建一份（开窗提速 B 项）；
            // 硬件趋势按需构建：折线 / 柱状视图不取，只有硬件视图与解读卡片要。
            _report = MainWindowHost.GetSharedReport(_settings, _window, _onAc);
            _hardware = _view == TrendView.Hardware
                ? MainWindowHost.GetHardwareTrend(_settings, _window)
                : MainWindowHost.PeekHardwareTrend(_settings, _window);
        }
        catch (Exception ex)
        {
            _report = null;
            ShowEmpty("读取趋势数据失败", ex.Message);
            _footer.Text = "";
            return;
        }

        // 解读已移到独立标签页（G2），本页只画图与结论行；两页各持一份范围，互不跟随（G8）。
        Render();
    }

    private void SetView(TrendView view)
    {
        if (_view == view) return;
        _view = view;

        _lineButton.Primary = view == TrendView.Line;
        _barButton.Primary = view == TrendView.Bar;
        _hardwareButton.Primary = view == TrendView.Hardware;

        // 柱状图没有上期可比数据：从「全部」切到柱状时自动落到近 30 天，
        // 既不画空柱，也不让人对着一个点了没反应的按钮（G7）。
        // SetRange 会重取数并刷新按钮状态，这里不再往下走。
        SyncRangeButtons();
        if (view == TrendView.Bar && _window.IsAll)
        {
            SetRange(TrendWindow.Month);
            return;
        }

        // 切到硬件视图才取硬件趋势：首次一次构建，之后同一窗口复用，不重复构建。
        if (view == TrendView.Hardware && _settings is not null)
            _hardware = MainWindowHost.GetHardwareTrend(_settings, _window);

        // 三个视图取的是已读到的数据的不同侧面，不必重新读 CSV
        if (_report is not null || _hardware is not null) Render();
        else RefreshData();
    }

    private void SetRange(TrendWindow window)
    {
        if (ReferenceEquals(_window, window)) return;
        _window = window;
        SyncRangeButtons();

        // 换窗口 = 换了聚合区间，必须回后端重算
        RefreshData();
    }

    /// <summary>换供电 = 换一批 CSV 行，同样必须回后端重算（分析入口按供电过滤）。</summary>
    private void SetPowerSource(bool onAc)
    {
        if (_onAc == onAc) return;
        _onAc = onAc;
        _powerAc.Primary = onAc;
        _powerDc.Primary = !onAc;
        RefreshData();
    }

    private void SyncRangeButtons()
    {
        _rangeWeek.Primary = ReferenceEquals(_window, TrendWindow.Week);
        _rangeMonth.Primary = ReferenceEquals(_window, TrendWindow.Month);
        _rangeAll.Primary = ReferenceEquals(_window, TrendWindow.All);

        // 柱状视图没有上期窗口可比，「全部」置灰且不可点；折线 / 硬件视图恢复（G7）。
        _rangeAll.Enabled = _view != TrendView.Bar;
    }

    private void Render()
    {
        if (_view == TrendView.Hardware)
        {
            RenderHardware();
            return;
        }

        var settings = _settings;
        var report = _report;
        if (settings is null || report is null) return;

        var days = report.Days;
        if (days.Count < 3)
        {
            // 空态：依据 TrendSlope.HasTrend => Points >= 3，后端只认 3 个点以上，
            // 前端就照实转述，绝不画一张空网格图糊弄。
            // "一天都没有"和"还差一天"是两种心情，分开说：
            // 前者要讲清"什么时候开始有"，后者要讲清"还差几天"。
            if (days.Count == 0)
            {
                ShowEmpty(
                    "还没有任何历史数据",
                    $"程序每 {Math.Clamp(settings.Sampler.SliceMinutes, 5, 120)} 分钟落盘一个时间片，"
                    + "累计满 3 天这里就会出现第一条曲线。");
            }
            else
            {
                int need = 3 - days.Count;
                ShowEmpty(
                    $"趋势需要至少 3 天数据（当前 {days.Count} 天）",
                    $"还需要 {need} 天 —— {(need == 1 ? "明天起" : $"{need} 天后")}这里会出现第一条曲线");
            }
            _footer.Text = "";
            return;
        }

        ShowCharts();
        RenderUsage(settings, report);
        if (_view == TrendView.Line) RenderLine(settings, report);
        else RenderBar(settings, report);
    }

    private void RenderLine(AppSettings settings, TrendReport report)
    {
        // 折线只画当前范围窗口（G1）。report.Days 保持全量不动：
        // 解读的"已有 N 天 / 还差几天"仍按全量算，裁剪只发生在渲染层。
        var days = CurrentWindow(report).Days;

        var axis = new List<DateOnly>(days.Count);
        foreach (var d in days) axis.Add(d.Date);

        int count = Math.Max(settings.CategoryNames.Length, settings.CategoryLowerBounds.Length);
        var series = new List<ChartSeries>(count + 2);
        for (int c = 0; c < count; c++)
        {
            var pts = new List<ChartPoint>(axis.Count);
            bool any = false;
            foreach (var d in days)
            {
                var v = DeltaTOf(d, c);
                if (!double.IsNaN(v)) any = true;
                pts.Add(new ChartPoint(d.Date, v));
            }
            // 整个类别一次都没采到就不出现在图例里，否则图例全是没数据的线
            if (!any) continue;
            series.Add(new ChartSeries(settings.CategoryName(c), SeriesColor(c, count), pts));
        }

        // ---- 空闲基线 + 环境气温 ----
        // 两条都是绝对温度，放右轴；ΔT 留左轴。缺失日写 NaN，线自然断开，不补 0。
        // 基线为近似值的日期用空心点标出，与实测点区分。
        var approx = new HashSet<DateOnly>();
        var baselinePts = new List<ChartPoint>(axis.Count);
        var ambientPts = new List<ChartPoint>(axis.Count);
        bool anyBaseline = false, anyAmbient = false;
        foreach (var d in days)
        {
            if (!double.IsNaN(d.IdleBaseline)) anyBaseline = true;
            if (!double.IsNaN(d.AmbientMean)) anyAmbient = true;
            if (d.IdleApproximated) approx.Add(d.Date);
            baselinePts.Add(new ChartPoint(d.Date, d.IdleBaseline));
            ambientPts.Add(new ChartPoint(d.Date, d.AmbientMean));
        }

        var p = Theme.Current;
        if (anyBaseline)
            series.Add(new ChartSeries("空闲基线", p.TextDim, baselinePts,
                "℃", false, true, false, DashStyle.Dash, approx));
        if (anyAmbient)
            series.Add(new ChartSeries("环境气温", p.Accent, ambientPts,
                "℃", false, true, false, DashStyle.Dash));

        _lineChart.AxisTitle = "ΔT ℃ / 右轴 绝对 ℃";
        _lineChart.EmptyText = series.Count == 0 ? "当前范围内没有可绘制的数据" : "";
        _lineChart.SetData(axis, series);

        var span = days.Count > 0
            ? $"数据 {days[0].Date:MM-dd} ~ {days[^1].Date:MM-dd}（共 {days.Count} 天） · "
            : "";
        _footer.Text = span + SlopeText(settings, report);
    }

    /// <summary>
    /// 当前范围窗口的日期区间与可见日集。统一走 <see cref="TrendWindow.Resolve"/>，
    /// 折线、条带、环境摘要都读同一份，避免三处各自裁剪出不同窗口。
    /// </summary>
    private (DateOnly From, DateOnly To, List<DailyTrend> Days) CurrentWindow(TrendReport report)
    {
        if (report.FirstDay is not { } first || report.LastDay is not { } last)
            return (default, default, report.Days);

        var (curFrom, curTo, _, _) = _window.Resolve(first, last);
        return (curFrom, curTo,
            report.Days.Where(d => d.Date >= curFrom && d.Date <= curTo).ToList());
    }

    /// <summary>
    /// 分档时长条带。时长 / 样本直接取 <c>TrendAnalyzer.WindowUsage</c>，
    /// 与 CSV 的 <c>n</c> 同一套样本加权；本方法只做配色、百分比与文案。
    /// </summary>
    private void RenderUsage(AppSettings settings, TrendReport report)
    {
        var (from, to, days) = CurrentWindow(report);
        var usage = TrendAnalyzer.WindowUsage(settings, report.Days, from, to);
        var total = usage.Sum(u => u.Hours);

        var cells = new List<UsageCell>(usage.Count);
        foreach (var u in usage)
        {
            cells.Add(new UsageCell(
                settings.CategoryName(u.Category),
                SeriesColor(u.Category, usage.Count),
                u.Hours,
                u.Samples));
        }

        _usageStrip.SetData(
            $"本期各档时长（{(report.OnAc ? "交流" : "电池")}） · {TodaySummaryData.HoursText(total)}",
            EnvironmentText(days),
            cells);
    }

    /// <summary>右轴两条辅助线的窗口摘要；缺失说"无数据"，绝不印 0℃。</summary>
    private static string EnvironmentText(List<DailyTrend> days)
    {
        var ambient = days.Where(d => !double.IsNaN(d.AmbientMean)).Select(d => d.AmbientMean).ToList();
        var baseline = days.Where(d => !double.IsNaN(d.IdleBaseline)).Select(d => d.IdleBaseline).ToList();
        int approx = days.Count(d => d.IdleApproximated && !double.IsNaN(d.IdleBaseline));

        var a = ambient.Count == 0
            ? "环温 无数据"
            : $"环温 {ambient.Average():0.0}℃（{ambient.Count}/{days.Count} 天）";
        var b = baseline.Count == 0
            ? "基线 无数据"
            : $"基线 {baseline.Average():0.0}℃" + (approx > 0 ? $"（{approx} 天近似）" : "");
        return $"右轴：{a} · {b}";
    }

    private void RenderBar(AppSettings settings, TrendReport report)
    {
        var cmp = report.Comparison.Count > 0 ? report.Comparison : report.Month;

        var groups = new List<BarGroup>(cmp.Count);
        foreach (var c in cmp)
        {
            // Comparable == false（缺一侧）就不画——画半截柱子会让人误读成"上期为 0"
            if (!c.Comparable) continue;
            if (c.CurDeltaT is null || c.PrevDeltaT is null) continue;
            groups.Add(new BarGroup(c.Category, c.CurDeltaT, c.PrevDeltaT));
        }

        if (groups.Count == 0)
        {
            _barChart.EmptyText =
                $"「{_window.Label}」还没有可对比的类别 —— 同一类别需要在前后两个周期都有数据。";
            _barChart.SetData(Array.Empty<BarGroup>());
            _footer.Text = RangeText(report);
            return;
        }

        _barChart.EmptyText = "";
        _barChart.SetData(groups);

        var worst = groups
            .Where(g => g.Diff.HasValue)
            .OrderByDescending(g => Math.Abs(g.Diff!.Value))
            .FirstOrDefault();

        _footer.Text = RangeText(report) + (worst is null
            ? ""
            : $" · 变化最大「{worst.Category}」{ChartMath.Signed(worst.Diff!.Value)}℃"
              + $"（{(worst.Diff!.Value > 0 ? "散热变差" : "散热改善")}）");
    }

    /// <summary>
    /// 「硬件」视图（3.3）：GPU 与 SSD 两张小图上下排，页脚给结论。
    ///
    /// 【画线门槛】GPU 数据稀疏是常态（G6）：某根线在窗口内只要有 ≥1 个真实点就画
    /// （孤立点 LineChart 会画成小圆点）；没有数据的日子在轴上留 NaN、线断开，
    /// 不把两段没数据的日子连起来。SSD 同理（程序没跑的日子没有行）。
    ///
    /// 【为什么不套用 ΔT 的 3 天门槛】那个门槛是最小二乘斜率的下限，
    /// 硬件视图的斜率/预估有 G5 自己的 14 天下限，画线本身只需要真实点。
    /// </summary>
    private void RenderHardware()
    {
        var hw = _hardware;
        if (hw is null || !hw.HasAnyData)
        {
            ShowEmpty(
                "还没有 GPU / SSD 历史数据",
                "GPU 温度与 SSD 寿命计数器自 2026-10-13 起才开始记录，"
                + "程序连续运行几天后这里就会出现第一条曲线。");
            _footer.Text = "";
            return;
        }

        ShowCharts();

        var p = Theme.Current;
        var axis = hw.Axis;

        // ---- GPU：均温（左轴，绝对温度不纳 0 基线）+ 使用时长（右轴）----
        var gpuSeries = new List<ChartSeries>();
        var tempPts = axis.Select(d => new ChartPoint(d, hw.GpuTempOn(d))).ToList();
        var hoursPts = axis.Select(d => new ChartPoint(d, hw.GpuHoursOn(d))).ToList();
        if (tempPts.Any(t => !double.IsNaN(t.Value)))
            gpuSeries.Add(new ChartSeries("GPU 均温", p.Warm, tempPts, "℃", true, false, false));
        if (hoursPts.Any(t => !double.IsNaN(t.Value)))
            gpuSeries.Add(new ChartSeries("GPU 使用", p.Cool, hoursPts, "h", false, true, true));
        _gpuChart.AxisTitle = "GPU 均温℃ / 使用h";
        _gpuChart.EmptyText = hw.GpuDays.Count == 0
            ? "当前范围内没有 GPU 数据（GPU 负载为 0 的样本不计入使用时长）"
            : "";
        _gpuChart.SetData(axis, gpuSeries);

        // ---- SSD：累计写入（左轴，累计量不纳 0 基线）+ 剩余寿命（右轴）----
        var ssdSeries = new List<ChartSeries>();
        var writePts = axis.Select(d => new ChartPoint(d, hw.SsdOn(d)?.DataWrittenGb ?? double.NaN)).ToList();
        var lifePts = axis.Select(d => new ChartPoint(d, hw.SsdOn(d)?.Life ?? double.NaN)).ToList();
        if (writePts.Any(t => !double.IsNaN(t.Value)))
            ssdSeries.Add(new ChartSeries("累计写入", p.Cool, writePts, " GB", false, false, false));
        if (lifePts.Any(t => !double.IsNaN(t.Value)))
            ssdSeries.Add(new ChartSeries("剩余寿命", p.Hot, lifePts, "%", false, true, false));
        _ssdChart.AxisTitle = "累计写入GB / 剩余寿命%";
        _ssdChart.EmptyText = hw.SsdRows.Count == 0
            ? "当前范围内没有 SSD 记录（程序没跑的日子没有行）"
            : "";
        _ssdChart.SetData(axis, ssdSeries);

        // ---- 页脚结论 ----
        // 2026-10-16 口径：使用时长 = 负载>0 时长；温度拆闲时(0%)/忙时(>0)两档才有意义。
        // 整个窗口都没有负载数据（2026-10-16 之前的旧构建）时明说，不印 0h。
        var gpuPart = hw.GpuDays.Count == 0
            ? "GPU 本期无数据"
            : hw.GpuLoadDaysCount == 0
                ? "GPU 本期无负载数据（2026-10-16 前为旧口径）"
                : $"GPU 使用 {TodaySummaryData.HoursText(hw.GpuHours)}"
                  + $" · 忙时 {T(hw.GpuActiveTempMean, "0.0")}℃"
                  + $" · 闲时 {T(hw.GpuIdleTempMean, "0.0")}℃"
                  + $"（{hw.GpuDays.Count} 天有数据）";

        var ssd = hw.Ssd;
        var ssdPart = ssd.HasTrend
            ? $"SSD 日均写入 {ssd.DataWrittenPerDay:0.#} GB · 预计还有 {(long)ssd.ProjectedLifeDays} 天到寿命耗尽"
            : $"SSD 还差 {Math.Max(0, SsdWindowStat.MinRowsForTrend - ssd.RowCount)} 天"
              + $"（≥{SsdWindowStat.MinRowsForTrend} 天才给速率与预估）";
        if (ssd.LifeFlat) ssdPart += " · 寿命 % 连日不动：计数器粒度粗，属正常";

        _footer.Text = $"{gpuPart}　|　{ssdPart}";
    }

    /// <summary>本期 / 上期的具体日期区间。有了它才能逐根柱子去核对 --trend 报告里的数字。</summary>
    private string RangeText(TrendReport report)
    {
        if (report.FirstDay is not { } first || report.LastDay is not { } last) return "";
        var (cf, ct, pf, pt) = _window.Resolve(first, last);
        return $"本期 {cf:MM-dd}~{ct:MM-dd}　上期 {pf:MM-dd}~{pt:MM-dd}";
    }

    /// <summary>
    /// 取某天某类的 ΔT，按报告口径那一侧取数；与 <c>BuildSlopes</c> 同源，
    /// 否则页脚引用的斜率和画出来的线不是同一批数。
    /// </summary>
    private double DeltaTOf(DailyTrend d, int category) =>
        d.Get(category, _report?.OnAc ?? true)?.DeltaT ?? double.NaN;

    /// <summary>类别线色：Cool → Warm → Hot 均匀过渡（越热的类别越红）。</summary>
    private static Color SeriesColor(int index, int count)
    {
        var p = Theme.Current;
        if (count <= 1) return p.Cool;
        double t = index / (double)(count - 1);
        return t < 0.5
            ? Gfx.Mix(p.Cool, p.Warm, t * 2.0)
            : Gfx.Mix(p.Warm, p.Hot, (t - 0.5) * 2.0);
    }

    /// <summary>NaN → 「—」。忙时/闲时均温在旧口径数据里没有，绝不能渲染成 0℃（R5）。</summary>
    private static string T(double v, string fmt) =>
        double.IsNaN(v) ? TodayCardData.Dash : v.ToString(fmt, CultureInfo.InvariantCulture);

    private static string SlopeText(AppSettings settings, TrendReport report)
    {
        var power = report.OnAc ? "交流" : "电池";
        var pick = TrendAnalyzer.PrimarySlope(report);
        if (pick is null)
            return $"趋势（{power}）：数据点不足或每日变化小于 0.02℃，暂不给结论";

        var (cat, slope) = pick.Value;
        var v = slope.PerDay;
        return $"趋势（{power}）：「{settings.CategoryName(cat)}」ΔT 约每天{(v > 0 ? "上升" : "下降")} "
               + $"{Math.Abs(v):0.00}℃（基于 {slope.Points} 个日点，仅作参考）";
    }

    // ------------------------------------------------------------------
    // 空态 / 图表 切换
    // ------------------------------------------------------------------

    private void ShowEmpty(string main, string sub)
    {
        _emptyMain.Text = main;
        _emptySub.Text = sub;
        _emptyPanel.Visible = true;
        _emptyPanel.BringToFront();
        _stage.Visible = false;
        SetUsageVisible(false);
    }

    private void ShowCharts()
    {
        _emptyPanel.Visible = false;
        _stage.Visible = true;
        _stage.BringToFront();
        _lineChart.Visible = _view == TrendView.Line;
        _barChart.Visible = _view == TrendView.Bar;
        _hardwareHost.Visible = _view == TrendView.Hardware;
        SetUsageVisible(_view != TrendView.Hardware);
    }

    /// <summary>条带只在折线 / 柱状视图出现；硬件视图把整行让给两幅小图。</summary>
    private void SetUsageVisible(bool visible)
    {
        _usageStrip.Visible = visible;
        _root.RowStyles[2] = new RowStyle(SizeType.Absolute, visible ? UsageRowHeight : 0f);
    }

    // ------------------------------------------------------------------
    // 主题
    // ------------------------------------------------------------------

    private void ApplyColors()
    {
        var p = Theme.Current;
        BackColor = p.Bg;
        ForeColor = p.Text;
        _root.BackColor = p.Bg;
        _stageHost.BackColor = p.Bg;
        _stage.BackColor = p.Bg;
        _hardwareHost.BackColor = p.Bg;
        _emptyPanel.BackColor = p.Bg;
        Theme.ApplyTree(_toolbar);

        _emptyMain.ForeColor = p.Text;
        _emptyMain.BackColor = p.Bg;
        _emptySub.ForeColor = p.TextDim;
        _emptySub.BackColor = p.Bg;

        _footer.ForeColor = p.TextDim;
        _footer.BackColor = p.Bg;

        _lineButton.Invalidate();
        _barButton.Invalidate();
        _hardwareButton.Invalidate();
        _rangeWeek.Invalidate();
        _rangeMonth.Invalidate();
        _rangeAll.Invalidate();
        Invalidate(true);
    }

    /// <summary>主题切换后由 <see cref="ThemedTabPage"/> 调回；线色/柱色都取主题色算，
    /// 必须重算，不能只靠 Invalidate。</summary>
    protected override void ApplyTheme()
    {
        ApplyColors();
        RefreshData();
    }

    /// <summary>切回本页时刷新（窗口开着、数据在后台继续落盘的场景）。
    /// 打开窗口期间跳过；之后首次切到本页才刷，已刷过的页切回来不重复取数。</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !MainWindowHost.IsOpening) MainWindowHost.OnTabActivated(this);
    }
}
