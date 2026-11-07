using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ThermalTrace.Analysis;
using ThermalTrace.Core;
using ThermalTrace.Model;
using ThermalTrace.Services;

namespace ThermalTrace.UI.Tabs;

/// <summary>
/// 「今日概览」页：顶部一行统计 → 供电 AC/DC 切换 → 五个负载档大卡片 → 页脚。
///
/// 【取值规则】
///  - 每张卡按 settings.CategoryOf(bandLower) 归并，行权重 = SliceRow.SampleCount
///  - ΔT = 该类加权均温 − Aggregator.DailyIdleBaseline（复用后端，不自己算）
///  - 无数据的类一律显示「—」，绝不显示 0℃（0 会被读成"凉快"）
///  - 空闲基线被近似时页脚加小字注记，与 HTML 报告口径一致
///  - 空数据日整页显示提示 + 「立即自检」按钮
///
/// 【数据从哪来】全部走 Analysis.TodaySummary，与 CLI --today 同源同值。
/// 本类只负责排版与着色，不碰任何计算。
///
/// 【为什么 BackColor 要自己赋】TabPage 默认按视觉样式取色，深色主题下会是一块白底。
/// </summary>
internal sealed class OverviewTab : ThemedTabPage
{
    private const string Dash = "—";

    private readonly TableLayoutPanel _page;
    private readonly TableLayoutPanel _body;
    private readonly TableLayoutPanel _empty;
    private readonly Label _emptyMain;
    private readonly Label _emptySub;
    private readonly Label _header;
    private readonly AccentButton _acButton;
    private readonly AccentButton _dcButton;
    private TableLayoutPanel? _cardRow;
    private readonly Label _footerLeft;
    private readonly Label _footerRight;
    private readonly Label _gpuLabel;
    private readonly Label _ssdLabel;
    private readonly Label _note;
    private readonly AccentButton _selfCheckButton;
    private readonly AccentButton _rebindButton;
    private readonly SamplerService? _sampler;

    private readonly List<StatCard> _cards = new();
    private readonly System.Windows.Forms.Timer _fileWatch = new() { Interval = 15_000 };
    private int _cardCount;
    private float _headerHeight = 30f;
    private bool _onAc = true;
    private bool _checking;
    private bool _rebinding;
    private DateTime _lastFileWriteUtc;
    private long _lastFileLength = -1;

    /// <summary>
    /// <paramref name="sampler"/> 为 null 时（诊断路径直接建窗口）仍可点重新绑定，
    /// 只是绑定要等下次启动才生效。
    /// </summary>
    public OverviewTab(SamplerService? sampler = null)
    {
        _sampler = sampler;
        // 有采样服务且已经出过样本时，默认跟着当前供电走：
        // 用电池打开窗口不该停在"交流"页，再让用户自己发现还有另一路。
        _onAc = sampler?.Latest is not null ? sampler.LatestContext.OnAc : true;
        _fileWatch.Tick += (_, _) => OnFileWatchTick();
        Text = "今日概览";
        UseVisualStyleBackColor = false;
        BackColor = Theme.Current.Bg;
        ForeColor = Theme.Current.Text;
        Padding = new Padding(16);

        // ---------------- 数据态 ----------------
        _body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _body.RowStyles.Add(new RowStyle(SizeType.Absolute, _headerHeight));   // 顶部信息行（两行文字，按内容增高）
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));             // 五张大卡片
        _body.RowStyles.Add(new RowStyle(SizeType.Absolute, 82f));             // 页脚（三行 × 26f + 余量）
        _body.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));              // 近似注记（有内容才给高度）

        _header = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };

        // 供电切换：FlowLayoutPanel 一行排开，不换行。
        // 【为什么放在页级而不是数据区里】空态也要能切供电（G3a 验收）：
        // 只有一路有数据时，切到没数据的那一路会进空态，按钮必须仍然可见可点。
        var powerRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var powerLabel = new Label
        {
            Text = "供电：",
            AutoSize = true,
            Margin = new Padding(0, 9, 8, 0)
        };
        // 宽度留给「电池 · 1h20m」这类角标，避免另一侧有数据却看不见。
        _acButton = new AccentButton { Text = "交流", Width = 140, Height = 28, Margin = new Padding(0, 3, 8, 0) };
        _dcButton = new AccentButton { Text = "电池", Width = 140, Height = 28, Margin = new Padding(0, 3, 8, 0) };
        _acButton.Click += (_, _) => SetPowerSource(true);
        _dcButton.Click += (_, _) => SetPowerSource(false);
        powerRow.Controls.AddRange([powerLabel, _acButton, _dcButton]);

        // 页脚三行（noncpu_signals_gui.md 3.2）：今日最高/超85 一行左右，
        // GPU、SSD 各占一整行。
        // 【为什么 GPU/SSD 要整行】SSD 行有四个计数器，挤半行会被 AutoEllipsis
        // 截断——被截断的数字比不显示更糟。
        // 【为什么两列都不能用 AutoSize】AutoSize 列会被跨列子控件（GPU/SSD 行）
        // 扰乱测宽，第 0 列一度被压到 100px，把「今日最高 … · 03:30 · 中等」
        // 截掉一半（2026-10-14 回归，见 noncpu_signals_gui.md 3.3）。
        // 固定比例最稳：两行文字都远短于半行宽度。
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = new Padding(0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
        _footerLeft = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        _footerRight = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 0, 2, 0)
        };
        _gpuLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            AutoEllipsis = true
        };
        _ssdLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            AutoEllipsis = true
        };
        footer.Controls.Add(_footerLeft, 0, 0);
        footer.Controls.Add(_footerRight, 1, 0);
        footer.Controls.Add(_gpuLabel, 0, 1);
        footer.SetColumnSpan(_gpuLabel, 2);
        footer.Controls.Add(_ssdLabel, 0, 2);
        footer.SetColumnSpan(_ssdLabel, 2);

        _note = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0)
        };

        // 卡片行先空着：类别数取决于 settings.CategoryLowerBounds，
        // 只有取到数才知道几列，由 RebuildCards() 填进第 2 行。
        _body.Controls.Add(_header, 0, 0);
        _body.Controls.Add(footer, 0, 2);
        _body.Controls.Add(_note, 0, 3);

        // ---------------- 空态 ----------------
        // 数据少的时候才是用户最常看到的样子，所以这里要回答三个问题：
        //   现在是什么情况 / 什么时候会有数据 / 一直为空的话该查什么。
        _empty = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };
        _empty.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _empty.RowStyles.Add(new RowStyle(SizeType.Percent, 34f));
        _empty.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));   // 主文案
        _empty.RowStyles.Add(new RowStyle(SizeType.Absolute, 96f));   // 副文案 + 状态行（为什么 / 什么时候有 / 采样是否活着）
        _empty.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));   // 按钮行（自检 + 重新绑定）
        _empty.RowStyles.Add(new RowStyle(SizeType.Percent, 66f));

        _emptyMain = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.BottomCenter,
            Margin = new Padding(0)
        };
        _emptySub = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.TopCenter,
            Margin = new Padding(0)
        };
        _selfCheckButton = new AccentButton
        {
            Text = "立即自检",
            Width = 132,
            Height = 32,
            Anchor = AnchorStyles.None      // TableLayoutPanel 会把 Anchor=None 的控件居中
        };
        _selfCheckButton.Click += (_, _) => RunSelfCheck();

        //【B6】重新绑定：GUI 里原本**没有任何**绑定入口，唯一相关的按钮是「立即自检」。
        // 新用户装上后拿不到任何数据，等于装了等于没装。放在空态是因为只有"没数据"的人需要它。
        _rebindButton = new AccentButton
        {
            Text = "重新绑定传感器",
            Width = 152,
            Height = 32,
            Primary = false,              // 次级样式，主操作仍是自检
            Anchor = AnchorStyles.None
        };
        _rebindButton.Click += (_, _) => RunRebind();

        // 两个按钮并排放一行。用 FlowLayoutPanel 而不是把行拆成两行：
        // 行数不能动（RowStyles 的个数必须与 RowCount 一致，拆行漏改一处控件就挤成一团）。
        // Dock=None + AutoSize + Anchor=None —— Anchor=None 让 TableLayoutPanel 把它按内容居中。
        var emptyActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.None,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };
        _selfCheckButton.Margin = new Padding(0, 0, 10, 0);
        _rebindButton.Margin = new Padding(0);
        emptyActions.Controls.Add(_selfCheckButton);
        emptyActions.Controls.Add(_rebindButton);

        _empty.Controls.Add(_emptyMain, 0, 1);
        _empty.Controls.Add(_emptySub, 0, 2);
        _empty.Controls.Add(emptyActions, 0, 3);

        // 页级两行：供电切换常驻，数据区 / 空态在下面互斥显示。
        // 这样两路都没数据时按钮仍在（G3a），不必把供电按钮复制一份到空态里。
        _page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            BackColor = Theme.Current.Bg
        };
        _page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _page.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));   // 供电切换
        _page.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 数据区 / 空态
        _page.Controls.Add(powerRow, 0, 0);
        _page.Controls.Add(_body, 0, 1);
        _page.Controls.Add(_empty, 0, 1);
        Controls.Add(_page);

        _empty.Visible = false;
    }

    /// <summary>
    /// 重新取数并重画。窗口是"隐藏而非销毁"，每次打开主窗口、每次切回本页都要调一次，
    /// 否则用户看到的是几小时前的旧卡片（采样每 30 分钟才落一片）。
    /// 设置页改了桶边界后切回来也靠它立即重算。
    /// </summary>
    public void RefreshData()
    {
        if (IsDisposed) return;

        // 取数用的设置与页头口径用同一份，避免"卡片按新边界、口径行还是旧值"。
        var settings = SettingsStore.Load();
        var data = TodaySummary.Build(settings, DateOnly.FromDateTime(DateTime.Now), _onAc);

        if (_cardCount != data.Cards.Count) RebuildCards(data.Cards.Count);
        Render(data, settings);
        ApplyColors();
    }

    private void SetPowerSource(bool onAc)
    {
        if (_onAc == onAc) return;
        _onAc = onAc;
        RefreshData();
    }

    // ------------------------------------------------------------------
    // 渲染
    // ------------------------------------------------------------------

    private void Render(TodaySummaryData d, AppSettings settings)
    {
        // 空态按"当前供电"判断，而不是两路合起来的 HasAnyData：
        // 只有另一路有数据时，切到空的那一路应当看到对应文案（G3a）。
        var empty = _onAc ? !d.HasAcData : !d.HasDcData;

        // 供电切换按钮在空态同样要显示当前选择，所以在空态分支之前就上色
        _acButton.Primary = _onAc;
        _dcButton.Primary = !_onAc;
        // 另一侧有数据时把量级写在按钮上，别让用户以为今天只有一路。
        var interval = Math.Max(1, settings.Sampler.IntervalSeconds);
        _acButton.Text = d.HasAcData
            ? $"交流 · {HoursText(d.AcSamples * interval / 3600.0)}" : $"交流 · 今日 {d.AcSlices} 片";
        _dcButton.Text = d.HasDcData
            ? $"电池 · {HoursText(d.DcSamples * interval / 3600.0)}" : $"电池 · 今日 {d.DcSlices} 片";

        if (empty)
        {
            //【B6】空态分三种，新用户最可能看到的是第一种：
            // "没绑传感器" 和 "今天还没到落盘点" 的处置完全不同，混成一句话会让用户干等。
            if (!settings.Sensors.IsUsable)
            {
                _emptyMain.Text = "尚未绑定传感器。";
                _emptySub.Text =
                    "没有绑定就取不到温度，此刻的采样只会记录空值。点『重新绑定传感器』即可自动识别本机硬件。";
            }
            else if (!d.HasAcData && !d.HasDcData)
            {
                _emptyMain.Text = "今日暂无数据。";
                var sliceMin = Math.Clamp(settings.Sampler.SliceMinutes, 5, 120);
                _emptySub.Text =
                    $"程序每 {sliceMin} 分钟落盘一个时间片，稍后再看即可；"
                    + $"若已运行超过 {sliceMin} 分钟仍然为空，请确认是以管理员权限启动的 —— 读 MSR 必须提权。";
            }
            else
            {
                // 只有一路没有数据：说清是哪一路，别让用户以为程序停了。
                _emptyMain.Text = _onAc
                    ? "今日还没有交流供电时段的数据。"
                    : "今日还没有电池供电时段的数据。";
                _emptySub.Text = "今天只有另一路供电的记录；上方切换按钮可切过去查看已有数据。";
            }
            // 页头挂在 _body 里，空态时整块隐藏；状态信息必须写进这里才看得见。
            _emptySub.Text += "\n" + EmptyStatusLine(d, settings);
        }
        _empty.Visible = empty;
        _body.Visible = !empty;
        if (empty) return;

        var p = Theme.Current;

        // 页头只留口径说明，具体档位区间下沉到各负载卡（G5）；
        // 手改配置的非 0 首档提示仍留在页头，避免用户看不到“首档被忽略”。
        var ignored = CategoryCaliber.FirstBoundIgnored(settings.CategoryLowerBounds);
        var caliberLine = "当前口径（单核峰值）";
        if (ignored is not null) caliberLine += " · " + ignored;

        var headerText = $"{d.Date:yyyy-MM-dd}   供电：{(_onAc ? "交流" : "电池")}"
            + $"   环境气温 {(double.IsNaN(d.AmbientC) ? "不可用" : $"{d.AmbientC:0.0}℃" + (d.AmbientIsProxy ? "（代理）" : ""))}"
            + $"   已记录 {HoursText(d.RecordedHours)}   样本 {d.SampleCount}"
            + PendingFlushHint(d);
        if (caliberLine.Length > 0) headerText += "\n" + caliberLine;
        _header.Text = headerText;

        // 页头第二行可能因档位多而换行：按实际宽度量一次高度，绝不截断当前口径。
        int innerW = _body.ClientSize.Width > 20 ? _body.ClientSize.Width : 700;
        var measured = TextRenderer.MeasureText(headerText, _header.Font, new Size(innerW, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        var wanted = Math.Max(30f, measured.Height + 4f);
        if (Math.Abs(wanted - _headerHeight) > 0.5f)
        {
            _headerHeight = wanted;
            _body.RowStyles[0] = new RowStyle(SizeType.Absolute, _headerHeight);
        }

        for (int i = 0; i < d.Cards.Count && i < _cards.Count; i++)
        {
            var c = d.Cards[i];
            var card = _cards[i];
            card.TitleText = c.Name;
            // 区间与档名同属标题，独立于该档有无数据：无数据时主数值显示「—」，
            // 但区间仍然要能看出这一档覆盖哪段负载（G5 验收）。
            card.RangeText = CategoryCaliber.DescribeRange(settings.CategoryLowerBounds, c.Category);
            // 文案一律取 TodayCardData 上的现成结果，与 --today 逐项一致
            card.ValueText = c.TempText;
            card.SubText = c.HasDeltaT ? "ΔT " + c.DeltaTText : TodayCardData.Dash;
            card.Rows = [c.HoursText, c.PowerText, c.Over85Text, c.SamplesText];

            // 无数据的类：主数值与 ΔT 都用次要色（验收项：— 的灰度与背景要够对比）
            card.ValueColor = c.HasData ? p.Text : p.TextDim;
            card.SubColor = c.HasDeltaT ? DeltaTColor(c.DeltaT) : p.TextDim;
        }

        _footerLeft.Text = double.IsNaN(d.MaxTempC)
            ? "今日最高 —"
            : $"今日最高 {d.MaxTempC:0.0}℃ · {(d.MaxTempAt is null ? "--:--" : d.MaxTempAt.Value.ToString("HH:mm"))}"
              + (d.MaxTempCategory.Length > 0 ? $" · {d.MaxTempCategory}" : "");

        _footerRight.Text = $"超 85℃ 累计 {(long)Math.Round(d.Over85Minutes)} 分钟";

        // GPU / SSD 两行当前值。文案由分析层算好（GpuText / SsdText），
        // CLI --today 打印的是同一串（G4 同源同值）——本类不自己拼。
        _gpuLabel.Text = d.GpuText;
        _ssdLabel.Text = d.SsdText;

        // 空闲基线被近似时必须说清楚，否则 ΔT 的口径会被误读（与 HTML 报告同一句话）
        var note = d.IdleApproximated && !double.IsNaN(d.IdleBaseline)
            ? $"注：今日无 0-10% 空闲档数据，ΔT 基线用「{d.IdleBaselineBand}」档最低温 "
              + $"{d.IdleBaseline:0.0}℃ 近似（与 HTML 报告口径一致）"
            : "";
        _note.Text = note;
        _body.RowStyles[3] = new RowStyle(SizeType.Absolute, note.Length > 0 ? 22f : 0f);
    }

    /// <summary>ΔT 语义色：越热越红。阈值只是"看起来顺眼"，不参与任何判断。</summary>
    private static Color DeltaTColor(double deltaT) =>
        deltaT < 12 ? Theme.Current.Cool :
        deltaT < 22 ? Theme.Current.Warm :
        Theme.Current.Hot;

    /// <summary>小时数的紧凑格式。实现收敛到 <see cref="TodaySummaryData"/>，三处不再各写一份。</summary>
    private static string HoursText(double hours) => TodaySummaryData.HoursText(hours);

    /// <summary>
    /// 空态专属状态行。页头挂在 <c>_body</c> 里，空态时被整块隐藏；
    /// 上一版把落盘 / 心跳提示都放页头，结果最需要解释的空态一条都看不见
    /// （2026-11-06 实测反馈）。这里把关键事实直接写进空态副文案。
    /// </summary>
    private string EmptyStatusLine(TodaySummaryData d, AppSettings settings)
    {
        var interval = Math.Max(1, settings.Sampler.IntervalSeconds);
        var total = d.AcSamples + d.DcSamples;
        var hours = HoursText(total * interval / 3600.0);
        var cur = _onAc ? d.AcSlices : d.DcSlices;
        var other = _onAc ? d.DcSlices : d.AcSlices;
        var power = _onAc ? "交流" : "电池";
        var otherPower = _onAc ? "电池" : "交流";

        var line = $"今日已记录 {total} 个样本（约 {hours}）；当前显示{power} {cur} 片，{otherPower} {other} 片。\n";
        var hint = PendingFlushHint(d).TrimStart();
        if (hint.Length > 0) line += hint + "\n";
        if (SamplerLog.LastPowerChange() is { } p) line += "最近供电切换：" + ShortPowerLine(p);
        return line.TrimEnd('\n');
    }

    /// <summary>把 sampler.txt 的原始日志行压成「HH:mm:ss 电池 → 交流 · 电量 94%」。</summary>
    private static string ShortPowerLine(string line)
    {
        try
        {
            var close = line.IndexOf(']');
            var stamp = close > 1 ? line[1..close].Trim() : "";
            if (stamp.Length > 8) stamp = stamp[^8..];
            const string marker = "power: ";
            var kind = line.IndexOf(marker, StringComparison.Ordinal);
            var detail = kind >= 0 ? line[(kind + marker.Length)..] : line;
            detail = detail.Replace("供电切换：", "", StringComparison.Ordinal);
            return $"{stamp} {detail}";
        }
        catch
        {
            return line;
        }
    }

    /// <summary>采样心跳停摆的统一文案，空态与页头共用。</summary>
    private string StallText()
    {
        if (_sampler is not { } sampler || sampler.StopRequested) return "";
        int interval;
        try { interval = Math.Max(1, SettingsStore.Load().Sampler.IntervalSeconds); }
        catch { interval = 5; }
        var age = sampler.HeartbeatAge;
        return age > TimeSpan.FromSeconds(Math.Max(30, interval * 3))
            ? $"采样已停止 {(int)age.TotalMinutes} 分钟，请在托盘点『退出』后重新打开程序。"
            : "";
    }

    /// <summary>
    /// 页头提示：把"当前片还没落盘"与"采样已经停了"分开讲清楚。
    /// 采样停摆时旧实现仍写"将在 HH:MM 写入"，用户会一直等一批不会来的数据
    /// （2026-11-06 实机）。心跳由 SamplerService 提供，这里只读、不参与判断口径。
    /// </summary>
    private string PendingFlushHint(TodaySummaryData d)
    {
        var stall = StallText();
        if (stall.Length > 0) return "   " + stall;

        int sliceMin;
        try { sliceMin = Math.Clamp(SettingsStore.Load().Sampler.SliceMinutes, 5, 120); }
        catch { return ""; }

        var now = DateTime.Now;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour,
            now.Minute - now.Minute % sliceMin, 0);
        var elapsed = (int)Math.Floor((now - start).TotalMinutes);
        if (elapsed < 1) return "";

        var next = start.AddMinutes(sliceMin);
        var hint = $"   最近 {elapsed} 分钟的样本将在 {next:HH:mm} 写入";
        if (_onAc && d.HasDcData) hint += "；已有的电池数据请点上方『电池』查看";
        else if (!_onAc && d.HasAcData) hint += "；已有的交流数据请点上方『交流』查看";
        return hint;
    }

    /// <summary>
    /// 窗口停在本页时按落盘节奏跟上新数据：只比较当日 CSV 的元数据，变化才重算。
    /// 不读内存样本、不做秒级刷新（对应 R3 收窄后的口径）。
    /// </summary>
    private void OnFileWatchTick()
    {
        if (IsDisposed || !Visible) return;
        try
        {
            var info = new FileInfo(AppPaths.DailyFile(DateOnly.FromDateTime(DateTime.Now)));
            if (!info.Exists) return;
            var write = info.LastWriteTimeUtc;
            var len = info.Length;
            if (write == _lastFileWriteUtc && len == _lastFileLength) return;
            _lastFileWriteUtc = write;
            _lastFileLength = len;
            RefreshData();
        }
        catch
        {
            // 文件正被写入或刚跨天，等下一拍
        }
    }

    /// <summary>
    /// 重建卡片行。类别数由 settings.CategoryLowerBounds 决定（默认 5，用户可在设置页改），
    /// 所以列数会变。
    ///
    /// 【为什么整块重建而不是清空重填】TableLayoutPanel 在 ColumnStyles 被清空后
    /// 再补样式，列的求值顺序不直观，容易出现"第一列挤成一条"。
    /// 重建一次容器最省心——这个操作只在类别数变化时才发生，不是热路径。
    /// </summary>
    private void RebuildCards(int count)
    {
        if (_cardRow is not null)
        {
            _body.Controls.Remove(_cardRow);
            _cardRow.Dispose();
        }

        _cards.Clear();

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = count,
            RowCount = 1,
            Margin = new Padding(0, 6, 0, 6),
            BackColor = Theme.Current.Bg
        };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        for (int i = 0; i < count; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / count));
            var card = new StatCard
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, i == count - 1 ? 0 : 8, 0)
            };
            row.Controls.Add(card, i, 0);
            _cards.Add(card);
        }

        _cardRow = row;
        _body.Controls.Add(row, 0, 1);
        _cardCount = count;
    }

    // ------------------------------------------------------------------
    // 主题
    // ------------------------------------------------------------------

    /// <summary>
    /// 重新刷文字色。必须自己来：Theme.ApplyControl 会把所有 Label 一律刷成 p.Text，
    /// 而页脚注记与「—」需要用 TextDim 才压得住（两套配色下都要够对比）。
    /// </summary>
    private void ApplyColors()
    {
        var p = Theme.Current;
        BackColor = p.Bg;
        ForeColor = p.Text;
        _page.BackColor = p.Bg;
        _body.BackColor = p.Bg;
        _empty.BackColor = p.Bg;

        _header.ForeColor = p.Text;
        _header.BackColor = p.Bg;
        _emptyMain.ForeColor = p.Text;
        _emptyMain.BackColor = p.Bg;
        _emptySub.ForeColor = p.TextDim;
        _emptySub.BackColor = p.Bg;
        _footerLeft.ForeColor = p.Text;
        _footerLeft.BackColor = p.Bg;
        _footerRight.ForeColor = p.TextDim;
        _footerRight.BackColor = p.Bg;
        _gpuLabel.ForeColor = p.Text;
        _gpuLabel.BackColor = p.Bg;
        _ssdLabel.ForeColor = p.TextDim;
        _ssdLabel.BackColor = p.Bg;
        _note.ForeColor = p.TextDim;
        _note.BackColor = p.Bg;

        _acButton.Invalidate();
        _dcButton.Invalidate();
        _selfCheckButton.Invalidate();
        _rebindButton.Invalidate();
        Invalidate(true);
    }

    /// <summary>主题切换后由 <see cref="ThemedTabPage"/> 调回：先整页刷色，再按新配色重算一次。</summary>
    protected override void ApplyTheme()
    {
        ApplyColors();
        RefreshData();
    }

    /// <summary>切回本页时刷新（窗口开着、数据在后台继续落盘的场景）。
    /// 打开窗口期间跳过：宿主会同步刷当前页；之后首次切到本页才刷，已刷过的页切回来不重复取数。
    /// 页可见期间另由 <see cref="OnFileWatchTick"/> 按落盘变化跟上新数据。</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _fileWatch.Enabled = Visible && !IsDisposed;
        if (Visible && !MainWindowHost.IsOpening) MainWindowHost.OnTabActivated(this);
    }

    // ------------------------------------------------------------------
    // 空态的「立即自检」
    // ------------------------------------------------------------------

    private const string EmptySelfCheckMessage =
        "自检未产生输出。请确认已以管理员权限运行，或从终端执行 ThermalTrace.exe --self-check。";

    private void RunSelfCheck()
    {
        if (_checking) return;
        _checking = true;
        _selfCheckButton.Enabled = false;
        _selfCheckButton.Text = "自检中…";

        // 后台采集与截断在 MaintenanceActions 里，与设置页、托盘共用同一份实现。
        MaintenanceActions.RunSelfCheck(EmptySelfCheckMessage, FinishSelfCheck);
    }

    private void FinishSelfCheck(string text)
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(new Action(() => FinishSelfCheck(text)));
            return;
        }

        _checking = false;
        _selfCheckButton.Enabled = true;
        _selfCheckButton.Text = "立即自检";

        RefreshData();
        MessageBox.Show(FindForm(), text, "ThermalTrace — 传感器自检",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ------------------------------------------------------------------
    // 空态的「重新绑定传感器」
    // ------------------------------------------------------------------

    private const string RebindNotFoundMessage =
        "未能在本机找到可用的 CPU 温度传感器，绑定未保存（配置保持原样）。\n\n"
        + "请依次确认：\n"
        + "  1. 是否以管理员身份运行 —— 读 MSR 必须提权；\n"
        + "  2. 安全软件是否拦截了 WinRing0 驱动（360 / 火绒等），\n"
        + "     拦截后所有温度恒为空，详见 README 的常见问题。";

    private const string RebindSavedHeader = "绑定已保存，以下是本机识别到的传感器：\n\n";

    private void RunRebind()
    {
        if (_rebinding) return;
        _rebinding = true;
        _rebindButton.Enabled = false;
        _rebindButton.Text = "绑定中…";

        MaintenanceActions.RunRebind(_sampler, RebindNotFoundMessage, RebindSavedHeader, FinishRebind);
    }

    private void FinishRebind(string text)
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(new Action(() => FinishRebind(text)));
            return;
        }

        _rebinding = false;
        _rebindButton.Enabled = true;
        _rebindButton.Text = "重新绑定传感器";

        RefreshData();
        MessageBox.Show(FindForm(), text, "ThermalTrace — 传感器绑定",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ------------------------------------------------------------------
    // 大卡片
    // ------------------------------------------------------------------

    /// <summary>
    /// 一张负载档大卡片。整块自绘：标题 / 主数值 / ΔT / 时长 / 功耗 / 超85%。
    ///
    /// 【为什么不用 CardPanel + Label】一张卡里需要三档字号（大号温度、中号 ΔT、小号指标），
    /// 而单个 Label 只能有一种字体。全部自绘还能让字号随可用空间等比缩放——
    /// 这是验收项「760×520 下不重叠不溢出、且不允许横向滚动条」的关键。
    ///
    /// 【字号为什么能直接乘缩放系数】字体按 point 创建，Graphics 会按 DPI 自行换算，
    /// 所以这里只管"相对 166×288 的设计尺寸缩多少"，不用再乘 DpiScale
    /// （乘了就双重缩放）。只有 1px 描边和内缩需要 DpiScale。
    /// </summary>
    private sealed class StatCard : BufferedPanel
    {
        private string _title = "";
        private string _range = "";
        private string _value = Dash;
        private string _sub = "";
        private string[] _rows = Array.Empty<string>();

        public StatCard()
        {
            Padding = new Padding(12);
        }

        public string TitleText
        {
            get => _title;
            set { _title = value ?? ""; Invalidate(); }
        }

        /// <summary>档名旁的本档区间，例如 <c>0–7%</c>、<c>≥92%</c>（G5）。</summary>
        public string RangeText
        {
            get => _range;
            set { _range = value ?? ""; Invalidate(); }
        }

        /// <summary>主数值（均温）。无数据时传「—」。</summary>
        public string ValueText
        {
            get => _value;
            set { _value = value ?? Dash; Invalidate(); }
        }

        /// <summary>主数值下面那行（ΔT）。</summary>
        public string SubText
        {
            get => _sub;
            set { _sub = value ?? ""; Invalidate(); }
        }

        /// <summary>再下面的若干行指标（时长 / 功耗 / 超85%）。</summary>
        public string[] Rows
        {
            get => _rows;
            set { _rows = value ?? Array.Empty<string>(); Invalidate(); }
        }

        public Color? ValueColor { get; set; }
        public Color? SubColor { get; set; }

        public override Color ContentBackColor => Theme.Current.Surface;

        private float Scale()
        {
            if (Width <= 0 || Height <= 0) return 1f;
            var byWidth = Width / 166f;
            var byHeight = Height / 288f;
            return (float)Math.Clamp(Math.Min(byWidth, byHeight), 0.62, 1.25);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            var dpi = DpiScale;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var rect = new RectangleF(0.5f * dpi, 0.5f * dpi, Width - 1f * dpi, Height - 1f * dpi);
            Gfx.FillRound(g, p.Surface, rect, 8f * dpi);
            Gfx.DrawRound(g, p.Border, 1f * dpi, rect, 8f * dpi);

            var s = Scale();
            var pad = (float)Padding.Left;
            var innerW = Width - pad * 2f;

            using var center = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            // ---- 标题：档名 + 本档区间，固定在卡片左上角 ----
            // 区间用次要色小字号紧跟档名（G5）。空间不够时只允许档名一侧省略：
            // 先给区间预留实测宽度，档名拿剩下的部分，保证区间永远完整、不丢上界。
            // 两者都画在原来的标题带内，不额外占高。
            var titleTop = pad * 0.5f;
            var titleH = 20f * s;
            using (var titleFont = new Font(Font.FontFamily, 11.5f * s, FontStyle.Bold))
            using (var rangeFont = new Font(Font.FontFamily, 9.5f * s))
            using (var titleBrush = new SolidBrush(p.Text))
            using (var rangeBrush = new SolidBrush(p.TextDim))
            using (var near = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            using (var exact = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                var titleGap = _range.Length > 0 ? 5f * s : 0f;
                var rangeW = _range.Length > 0 ? g.MeasureString(_range, rangeFont).Width : 0f;
                var nameW = g.MeasureString(_title, titleFont).Width;
                if (_range.Length > 0)
                    nameW = Math.Min(nameW, Math.Max(0f, innerW - rangeW - titleGap));

                g.DrawString(_title, titleFont, titleBrush,
                    new RectangleF(pad, titleTop, nameW + 1f * dpi, titleH), near);
                if (_range.Length > 0)
                    g.DrawString(_range, rangeFont, rangeBrush,
                        new RectangleF(pad + nameW + titleGap, titleTop, rangeW + 1f * dpi, titleH), exact);
            }

            // ---- 余下内容在标题下方整体垂直居中 ----
            var valueH = 34f * s;
            var subH = 17f * s;
            var rowH = 16f * s;
            var gap = 6f * s;

            var top = pad + 22f * s;
            var blockH = valueH + subH + gap + _rows.Length * rowH;
            var y = top + Math.Max(0f, (Height - pad - top - blockH) / 2f);

            using (var valueFont = new Font(Font.FontFamily, 21f * s, FontStyle.Bold))
            using (var valueBrush = new SolidBrush(ValueColor ?? p.Text))
            {
                g.DrawString(_value, valueFont, valueBrush, new RectangleF(pad, y, innerW, valueH), center);
            }
            y += valueH;

            using (var subFont = new Font(Font.FontFamily, 10f * s))
            using (var subBrush = new SolidBrush(SubColor ?? p.TextDim))
            {
                g.DrawString(_sub, subFont, subBrush, new RectangleF(pad, y, innerW, subH), center);
            }
            y += subH + gap;

            using var rowFont = new Font(Font.FontFamily, 9.5f * s);
            using var rowBrush = new SolidBrush(p.TextDim);
            foreach (var line in _rows)
            {
                g.DrawString(line, rowFont, rowBrush, new RectangleF(pad, y, innerW, rowH), center);
                y += rowH;
            }
        }
    }
}
