using ThermalTrace.Analysis;
using ThermalTrace.Core;

namespace ThermalTrace.UI.Tabs;

/// <summary>
/// 独立的「解读」标签页（G2，2026-11-4）。
///
/// 内容正文全部复用 <see cref="InterpretationCard"/>，不复制解读规则、阈值或文案；
/// 范围由本页独立持有（G8），与趋势页互不跟随，只在自己可见时取数，
/// 不引入跨页事件总线。
///
/// 【范围按钮】近 7 天 / 近 30 天 / 自上次清灰。「全部」不出现——解读关注清灰前后的变化，
/// 全量窗口没有可对比的上期。未设置清灰基准日时「自上次清灰」禁用并提示去设置页。
///
/// 【报告按钮】（G16）工具条最右侧「生成并打开 HTML 报告」：后台生成后交给默认浏览器，
/// 复用 <see cref="Commands.WriteTrendReport"/>，与 <c>--trend</c> 同一份报告逻辑。
/// </summary>
internal sealed class InterpretationTab : ThemedTabPage
{
    private readonly InterpretationCard _card = new();
    private readonly TableLayoutPanel _root;
    private readonly TableLayoutPanel _toolbarRow;
    private readonly FlowLayoutPanel _toolbar;
    private readonly AccentButton _report;
    private readonly Label _rangeLabel;
    private readonly AccentButton _rangeWeek;
    private readonly AccentButton _rangeMonth;
    private readonly AccentButton _rangeSince;
    private readonly Label _cleanHint;
    private readonly AccentButton _powerAc;
    private readonly AccentButton _powerDc;
    private bool _onAc = true;

    /// <summary>报告生成期间为 true：按钮禁用并显示「正在生成…」，重复点击直接忽略。</summary>
    private bool _reporting;

    /// <summary>本页自己的范围（G8）：默认近 7 天，切页不跟随趋势页。</summary>
    private TrendWindow _window = TrendWindow.Week;

    /// <summary>最近一次读数得到的清灰基准日；null = 未设置，此时「自上次清灰」不可用。</summary>
    private DateOnly? _cleanDate;

    public InterpretationTab()
    {
        Text = "解读";
        Padding = new Padding(16, 12, 16, 16);
        BackColor = Theme.Current.Bg;

        _rangeLabel = new Label
        {
            Text = "范围",
            AutoSize = true,
            Margin = new Padding(0, 8, 8, 0),
            ForeColor = Theme.Current.TextDim
        };

        _rangeWeek = RangeButton("近 7 天", 84, () => SetRange(TrendWindow.Week));
        _rangeMonth = RangeButton("近 30 天", 84, () => SetRange(TrendWindow.Month));
        _rangeSince = RangeButton("自上次清灰", 96, () =>
        {
            if (_cleanDate is { } clean) SetRange(TrendWindow.SinceClean(clean));
        });
        // 首次 RefreshData 之前不知道有没有清灰基准日，先不给点，避免用空基准日构建窗口。
        _rangeSince.Enabled = false;

        // 供电切换与趋势页同语义：本页独立持有，切页不跟随（G8）。
        _powerAc = RangeButton("交流", 68, () => SetPowerSource(true));
        _powerDc = RangeButton("电池", 68, () => SetPowerSource(false));
        _powerAc.Primary = true;

        _cleanHint = new Label
        {
            Text = "请先在设置页填写最近一次清灰",
            AutoSize = true,
            Visible = false,
            Margin = new Padding(8, 8, 0, 0),
            ForeColor = Theme.Current.TextDim
        };

        // 报告入口放在同一行的最右侧：与左侧范围 / 供电工具条同一视觉风格，
        // 窗口缩到最小宽度时也始终可见，不会被左侧内容挤出工具条。
        _report = new AccentButton
        {
            Text = "生成并打开 HTML 报告",
            Width = 160,
            Height = 28,
            Primary = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(12, 4, 0, 0)
        };
        _report.Click += OnGenerateReport;

        _toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };
        _toolbar.Controls.Add(_rangeLabel);
        _toolbar.Controls.Add(_rangeWeek);
        _toolbar.Controls.Add(_rangeMonth);
        _toolbar.Controls.Add(_rangeSince);
        _toolbar.Controls.Add(_cleanHint);
        _toolbar.Controls.Add(new Label
        {
            Text = "供电",
            AutoSize = true,
            Margin = new Padding(8, 8, 8, 0),
            ForeColor = Theme.Current.TextDim
        });
        _toolbar.Controls.Add(_powerAc);
        _toolbar.Controls.Add(_powerDc);

        _toolbarRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };
        _toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _toolbarRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _toolbarRow.Controls.Add(_toolbar, 0, 0);
        _toolbarRow.Controls.Add(_report, 1, 0);

        _card.Dock = DockStyle.Fill;
        _card.Margin = new Padding(0, 6, 0, 0);

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _root.Controls.Add(_toolbarRow, 0, 0);
        _root.Controls.Add(_card, 0, 1);

        Controls.Add(_root);

        ApplyColors();
    }

    /// <summary>按本页当前范围重取数据并渲染。宿主打开窗口、切回本页时都走这里。</summary>
    public void RefreshData()
    {
        if (IsDisposed) return;

        try
        {
            var settings = SettingsStore.Load();
            _cleanDate = settings.CleanDate;

            // 清灰基准日被清空后，已选的「自上次清灰」退回近 7 天，
            // 不用空基准日构建窗口（G8）。
            if (_cleanDate is null && _window.IsSinceClean) _window = TrendWindow.Week;

            // 趋势页与解读页默认同看近 7 天：报告与硬件趋势都从宿主共享层取，同键只构建一次（B 项）。
            var data = MainWindowHost.GetTrendData(settings, _window, _onAc);
            var report = data.Report;
            var hardware = data.Hardware;
            _card.SetData(Interpreter.Build(settings, report, _window, hardware));
        }
        catch
        {
            // 解读是只读派生内容：读盘失败就回到空壳，不能拖垮整个主窗口。
            // 不清空 _cleanDate：读失败不等于用户没填基准日，提示不能跟着说假话。
            _card.SetData(null);
        }

        SyncRangeButtons();
        _card.PerformLayout();
    }

    private AccentButton RangeButton(string text, int width, Action onClick)
    {
        var button = new AccentButton
        {
            Text = text,
            Width = width,
            Height = 28,
            Primary = false,
            Margin = new Padding(0, 4, 8, 0)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void SetRange(TrendWindow window)
    {
        // 记录按值比较：同一清灰基准日重复点击不必再取一次数。
        if (_window == window) return;
        _window = window;
        RefreshData();
    }

    /// <summary>换供电要回后端重算，解读结论与卡片必须来自同一批数。</summary>
    private void SetPowerSource(bool onAc)
    {
        if (_onAc == onAc) return;
        _onAc = onAc;
        _powerAc.Primary = onAc;
        _powerDc.Primary = !onAc;
        RefreshData();
    }

    /// <summary>
    /// 「生成并打开 HTML 报告」：复用 <see cref="Commands.WriteTrendReport"/>，取数、排版与落盘
    /// 全部放到后台线程，UI 只负责忙碌态、打开浏览器和失败提示；不在本页另写一份报告代码。
    /// 供电跟随本页当前选择，窗口口径与 <c>--trend</c> 相同。
    /// </summary>
    private async void OnGenerateReport(object? sender, EventArgs e)
    {
        if (_reporting) return;
        _reporting = true;
        _report.Enabled = false;
        _report.Text = "正在生成…";

        try
        {
            var settings = SettingsStore.Load();
            var onAc = _onAc;
            var result = await Task.Run(() => Commands.WriteTrendReport(settings, onAc))
                .ConfigureAwait(true);
            if (IsDisposed) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = result.File,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(),
                    $"报告已生成，但无法用默认浏览器打开：\n{result.File}\n\n{ex.Message}",
                    "ThermalTrace — 生成 HTML 报告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                MessageBox.Show(FindForm(), $"报告生成失败：{ex.Message}",
                    "ThermalTrace — 生成 HTML 报告", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _reporting = false;
            if (!IsDisposed)
            {
                _report.Text = "生成并打开 HTML 报告";
                _report.Enabled = true;
            }
        }
    }

    private void SyncRangeButtons()
    {
        _rangeWeek.Primary = ReferenceEquals(_window, TrendWindow.Week);
        _rangeMonth.Primary = ReferenceEquals(_window, TrendWindow.Month);
        _rangeSince.Primary = _window.IsSinceClean;

        // 未设置清灰基准日时按钮禁用并提示去设置页；近 7 天 / 近 30 天不受影响（G8）。
        _rangeSince.Enabled = _cleanDate is not null;
        _cleanHint.Visible = _cleanDate is null;
    }

    /// <summary>切回本页时按本页范围刷新；打开窗口期间跳过，之后首次切到本页才刷，已刷过的页切回来不重复取数。</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !MainWindowHost.IsOpening) MainWindowHost.OnTabActivated(this);
    }

    // ------------------------------------------------------------------
    // 主题
    // ------------------------------------------------------------------

    private void ApplyColors()
    {
        var p = Theme.Current;
        BackColor = p.Bg;
        _root.BackColor = p.Bg;
        _toolbarRow.BackColor = p.Bg;
        _toolbar.BackColor = p.Bg;
        _rangeLabel.ForeColor = p.TextDim;
        _cleanHint.ForeColor = p.TextDim;
        Theme.ApplyTree(_toolbarRow);
    }

    /// <summary>主题切换后由 <see cref="ThemedTabPage"/> 调回；句柄未建时只刷色，
    /// 建好后连带重算（与原分支行为一致）。</summary>
    protected override void ApplyTheme()
    {
        ApplyColors();
        if (IsHandleCreated) RefreshData();
    }
}
