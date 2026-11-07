using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ThermalTrace.Analysis;

namespace ThermalTrace.UI;

/// <summary>
/// 健康度色签：把等级画成一个实心圆角小牌子。
/// 颜色不在本类里决定——由 <see cref="InterpretationCard.HealthColor"/> 统一给出，
/// 免得同一个等级在两处显示成两个颜色。
/// </summary>
internal sealed class HealthChip : BufferedPanel
{
    private Color _fill = Theme.Current.TextDim;

    public HealthChip()
    {
        Width = 72;
        Height = 22;
    }

    /// <summary>牌子底色。文字色由 <see cref="Gfx.ContrastOn"/> 反算，两套配色下都可读。</summary>
    public Color Fill
    {
        get => _fill;
        set { _fill = value; Invalidate(); }
    }

    public override Color ContentBackColor => _fill;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = DpiScale;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var r = new RectangleF(0.5f * s, 0.5f * s, Width - 1f * s, Height - 1f * s);
        Gfx.FillRound(g, _fill, r, Height / 2f * s);

        if (Text.Length > 0)
        {
            using var brush = new SolidBrush(Gfx.ContrastOn(_fill));
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            };
            g.DrawString(Text, Font, brush, r, sf);
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
    }
}

/// <summary>
/// 解读内容面板（原趋势页的底部卡片，2026-11-4 起挂在独立的「解读」标签页）。
/// 内容由分析层的 <see cref="Interpreter"/> 一次算好（<see cref="InterpretationReport"/>），
/// 本类只负责排版与刷色，一个数都不自己算——GUI / 页脚 / <c>--trend</c> 同源同值的前提正在于此。
///
/// 【排版为什么手写】
/// 内容是变长的（证据 0~4 条、归因 0~N 条、建议 2~4 条），而且每条都要按可用宽度换行。
/// TableLayoutPanel 要动态增删行、还要 CSS 式的"自动行高"，项目在这个组合上栽过两次；
/// 这里照 SettingsTab 的做法自己算：y 逐个累加，行数变化时只改这一处。
///
/// 【不再自己决定停靠与高度】容器由宿主页给（Dock = Fill），
/// 这样同一份正文可以放进独立标签页，也不会再有"固定 220px 挤压图表"的问题（G2）。
/// </summary>
internal sealed class InterpretationCard : CardPanel
{
    private const int RowGap = 3;
    private const int SectionGap = 10;
    private const int Indent = 14;
    private const int CopyFlashMs = 1600;
    private const string CopyLabel = "复制数据摘要";

    private readonly HealthChip _chip = new();
    private readonly Label _summary = new() { AutoSize = true, Margin = Padding.Empty };
    private readonly Panel _scroll = new() { AutoScroll = true, Margin = Padding.Empty, Dock = DockStyle.None };
    private readonly Panel _content = new() { Margin = Padding.Empty, Dock = DockStyle.None };
    private readonly Label _confidence = new()
    {
        AutoSize = false,
        AutoEllipsis = true,
        Margin = Padding.Empty,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly AccentButton _copy = new()
    {
        Text = CopyLabel,
        Primary = false,
        Width = 118,
        Height = 26,
        Margin = Padding.Empty
    };

    private InterpretationReport? _data;
    private string _digest = "";
    private System.Windows.Forms.Timer? _flash;

    public InterpretationCard()
    {
        // 每个控件都必须有父容器，否则永远不会被绘制
        // （SettingsTab 的 AddSection 注释里记过同一个坑）
        Controls.Add(_chip);
        Controls.Add(_summary);
        Controls.Add(_scroll);
        Controls.Add(_confidence);
        Controls.Add(_copy);
        _copy.Visible = false;   // SetData 决定是否露出来（数据不足时不给复制入口）
        _scroll.Controls.Add(_content);

        _copy.Click += OnCopyClicked;
    }

    /// <summary>健康度着色：良好=Cool、关注=Warm、注意=Hot、数据不足=TextDim。</summary>
    internal static Color HealthColor(HealthLevel level)
    {
        var p = Theme.Current;
        return level switch
        {
            HealthLevel.Good => p.Cool,
            // 计划没给"平稳"的颜色：它与"良好"同属"无需动作"一档。
            // 造一个没有约定的新颜色比复用 Cool 更容易出现深/浅色不一致，区分交给文字。
            HealthLevel.Stable => p.Cool,
            HealthLevel.Watch => p.Warm,
            HealthLevel.Warning => p.Hot,
            _ => p.TextDim
        };
    }

    /// <summary>喂数据。null 表示"没有可解读的报告"，此时卡片留一块空壳。</summary>
    public void SetData(InterpretationReport? data)
    {
        _data = data;

        // 数据不足时只有"还差几天"这一句有效：不渲染五层内容，也不给复制入口（G2）。
        var insufficient = data is not null && data.Health == HealthLevel.Insufficient;
        _digest = data is not null && !insufficient ? data.AiDigest : "";
        _copy.Visible = data is not null && !insufficient;

        _chip.Text = data is null ? "无数据" : data.Health.Text();
        _chip.Fill = data is null ? Theme.Current.TextDim : HealthColor(data.Health);
        _summary.Text = data?.Summary ?? "";
        _confidence.Text = data?.ConfidenceNote ?? "";

        BuildRows();
        ApplyColors();
        DoLayout();
        Invalidate();
    }

    // ------------------------------------------------------------------
    // 内容行
    // ------------------------------------------------------------------

    private void BuildRows()
    {
        DisposeChildren(_content);
        _content.Controls.Clear();
        if (_data is null || _data.Health == HealthLevel.Insufficient) return;

        AddNumbered("证据", _data.Evidence.Select(e => e.Text).ToList());
        AddAttributions(_data.Attributions);
        AddNumbered("建议", _data.Suggestions);

        if (_data.Clean is { } clean)
        {
            AddSectionTitle("清灰基准");
            AddBody(clean.Text, dim: false);
        }
    }

    private void AddNumbered(string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        AddSectionTitle(title);
        for (int i = 0; i < lines.Count; i++)
            AddBody($"{i + 1}. {lines[i]}", dim: false);
    }

    /// <summary>
    /// 归因每条拆三行：假设（带置信度）→ 依据 → 验证方法。
    /// 拆开是为了让"验证方法"一眼可见——要求归因可证伪，
    /// 挤成一行它就会被当成结论而不是假设。
    /// </summary>
    private void AddAttributions(IReadOnlyList<AttributionItem> items)
    {
        if (items.Count == 0) return;

        AddSectionTitle("可能原因（含验证方法）");
        foreach (var a in items)
        {
            AddBody(a.Confidence.Length > 0
                ? $"· {a.Hypothesis}（置信度：{a.Confidence}）"
                : $"· {a.Hypothesis}", dim: false);
            if (a.Support.Length > 0) AddBody($"依据：{a.Support}", dim: true);
            if (a.Verification is { Length: > 0 } v) AddBody($"验证：{v}", dim: true);
        }
    }

    private void AddSectionTitle(string text)
    {
        // 用既有的 SectionLabel：它自带粗体标题与分隔线，且字体在 OnPaint 里临时 new + using，
        // 不需要持有 Font 对象——少一个"应用层面卡住了才敢 Dispose"的负担。
        _content.Controls.Add(new SectionLabel
        {
            Text = text,
            ShowRule = false,
            Margin = new Padding(0, SectionGap, 0, 0)
        });
    }

    private void AddBody(string text, bool dim)
    {
        _content.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Margin = Padding.Empty,
            // 缩进用 Padding 而不是在文字前塞空格：换行后的第二行同样对齐
            Padding = new Padding(Indent, 0, 0, 0),
            Tag = dim ? "dim" : "text"
        });
    }

    // ------------------------------------------------------------------
    // 排版
    // ------------------------------------------------------------------

    private void DoLayout()
    {
        if (Width <= 0 || Height <= 0) return;

        int left = Padding.Left;
        int top = Padding.Top;
        int right = Width - Padding.Right;
        int bottom = Height - Padding.Bottom;
        int avail = Math.Max(80, right - left);

        // ---- 底行：左侧可信度详情 + 右侧复制按钮（数据不足时按钮隐藏，可信度吃满整行） ----
        int copyY = bottom - _copy.Height;
        var showCopy = _copy.Visible;
        _copy.Location = showCopy ? new Point(right - _copy.Width, copyY) : new Point(right, copyY);
        // 宽度不够时宁可把可信度压成 0 宽（自动省略号），也不能压到按钮上
        _confidence.SetBounds(
            left, copyY,
            showCopy ? Math.Max(0, avail - _copy.Width - 12) : avail,
            _copy.Height);

        // ---- 头部：色签一行，总结在其下整宽换行 ----
        _chip.Location = new Point(left, top);
        SetMax(_summary, avail);
        _summary.Location = new Point(left, top + _chip.Height + 4);
        int contentTop = _summary.Bottom + RowGap;

        // ---- 滚动区 ----
        int scrollH = Math.Max(24, copyY - RowGap - contentTop);
        _scroll.SetBounds(left, contentTop, avail, scrollH);

        // 纵滚动条一出现可用宽度就少一条；按 SettingsTab 的写法排两次必收敛
        int inner = InnerWidth(avail);
        LayoutRows(inner);
        int again = InnerWidth(avail);
        if (again != inner) LayoutRows(again);
    }

    private int InnerWidth(int avail)
    {
        int reserve = _scroll.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth + 2 : 2;
        return Math.Max(60, avail - reserve);
    }

    /// <summary>给每行设最大宽度（触发换行）后依次落 y。</summary>
    private void LayoutRows(int width)
    {
        foreach (Control c in _content.Controls)
        {
            if (c is Label auto && auto.AutoSize)
            {
                // MaximumSize.Height = 0 只对 AutoSize 控件表示"不限制高度"；
                // 用在固定高度的控件上会把它的 Height 钳成 0（2026-10-13 冒烟实测：
                // 分区标题行变成 121x0，标题直接看不见）。两类控件必须分开处理。
                SetMax(c, width);
            }
            else
            {
                c.Size = new Size(width, Math.Max(1, c.Height));
            }
        }

        // AutoSize 的 Label 改了 MaximumSize 之后要跑一次布局才拿得到真实高度
        _content.PerformLayout();

        int y = 0;
        foreach (Control c in _content.Controls)
        {
            c.Location = new Point(0, y);
            y += c.Height + (c.Margin.Top > 0 ? SectionGap : RowGap);
        }

        _content.SetBounds(0, 0, width, Math.Max(1, y));
        _scroll.AutoScrollMinSize = new Size(0, Math.Max(1, y));
    }

    private static void SetMax(Control c, int width)
    {
        if (c.MaximumSize.Width != width) c.MaximumSize = new Size(width, 0);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        DoLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        DoLayout();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) DoLayout();
    }

    // ------------------------------------------------------------------
    // 「复制数据摘要」
    // ------------------------------------------------------------------

    private void OnCopyClicked(object? sender, EventArgs e)
    {
        if (_digest.Length == 0)
        {
            Flash("无可复制内容");
            return;
        }

        try
        {
            // 剪贴板可能被别的进程锁着：失败也要给回看得见的反馈，
            // 静默无反应会让人以为按了没中。
            Clipboard.SetText(_digest);
            Flash("已复制");
        }
        catch (Exception)
        {
            Flash("复制失败");
        }
    }

    /// <summary>按钮文字短暂改变再复原（项目既有的轻量反馈形式）。</summary>
    private void Flash(string text)
    {
        _copy.Text = text;
        _copy.Invalidate();

        _flash?.Stop();
        _flash?.Dispose();
        _flash = new System.Windows.Forms.Timer { Interval = CopyFlashMs };
        _flash.Tick += (_, _) =>
        {
            if (_flash is null) return;
            _flash.Stop();
            _flash.Dispose();
            _flash = null;
            if (!IsDisposed) _copy.Text = CopyLabel;
        };
        _flash.Start();
    }

    // ------------------------------------------------------------------
    // 主题
    // ------------------------------------------------------------------

    private void ApplyColors()
    {
        var p = Theme.Current;

        // 卡内的普通 Panel / Label 落到 Theme.ApplyControl 的 default 分支会拿到窗口底色，
        // 卡面上就会浮出一块更深/更浅的方块——统一压成卡片色的实现见 ThemeOps.Flatten。
        ThemeOps.Flatten(this, p.Surface);

        _summary.ForeColor = p.Text;
        _summary.BackColor = p.Surface;
        _confidence.ForeColor = p.TextDim;
        _confidence.BackColor = p.Surface;

        foreach (Control c in _content.Controls)
        {
            c.ForeColor = Equals(c.Tag, "dim") ? p.TextDim : p.Text;
            if (c is BufferedPanel) continue;   // 自绘件自己取 Theme.Current，别越俎代庖
            c.BackColor = p.Surface;
        }

        _copy.Invalidate();
        Invalidate();
    }

    /// <summary>
    /// 主题切换后重排并重刷色。行本身不用重建——颜色是 Label 的属性，
    /// 自绘件（色签 / 分区标题 / 复制按钮）各自订阅 Theme.Changed，这一趟只是为了
    /// 让"分区标题用到的新增自绘件"也拿到新色。
    /// </summary>
    protected override void OnThemeChanged()
    {
        base.OnThemeChanged();
        ApplyColors();
        DoLayout();
    }

    /// <summary>
    /// 行是每次刷新重建的，换掉之前必须显式 Dispose——
    /// 切一次范围就会新建几十个 Label，不回收的话句柄会一路涨上去。
    /// </summary>
    private static void DisposeChildren(Control parent)
    {
        var kids = parent.Controls.Cast<Control>().ToList();
        parent.Controls.Clear();
        foreach (var c in kids) c.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flash?.Stop();
            _flash?.Dispose();
            _flash = null;
        }
        base.Dispose(disposing);
    }
}
