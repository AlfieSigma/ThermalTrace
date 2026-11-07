using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ThermalTrace.UI;

/// <summary>
/// 自绘常用件。全部从 BufferedPanel 派生，因此自动跟随主题、自动双缓冲。
///
/// 【红线】这里禁止硬编码任何 Color——一律取 Theme.Current.*。
/// 两套配色必须成对维护，避免维护漂移。
/// </summary>

internal static class Gfx
{
    /// <summary>圆角矩形路径。radius 会按实际尺寸收敛，避免小尺寸下炸成怪形状。</summary>
    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Max(0f, Math.Min(radius, Math.Min(r.Width, r.Height) / 2f));
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }

        float d = radius * 2f;
        path.AddArc(r.X, r.Y, d, d, 180f, 90f);
        path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
        path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
    {
        using var path = RoundRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void DrawRound(Graphics g, Color color, float width, RectangleF r, float radius)
    {
        using var path = RoundRect(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    /// <summary>两色线性插值，用于 hover / pressed / disabled 状态。t=0 取 a，t=1 取 b。</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * t),
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>给定底色返回可读的前景色。不玩 Gamma 校正，够用即可。</summary>
    public static Color ContrastOn(Color bg)
    {
        var lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return lum > 0.62 ? Color.FromArgb(16, 17, 20) : Color.White;
    }
}

/// <summary>
/// 圆角卡片。今日概览的五个大卡片、设置页的分区容器都用它。
///
/// 只有视觉，没有业务：内容由调用方往里塞控件。
/// 用 <see cref="TitleText"/> 时请把 Padding.Top 预留出标题高度（约 40px），
/// 标题画在 (Padding.Left, Padding.Top) 处。
/// </summary>
internal class CardPanel : BufferedPanel
{
    private Color? _fill;
    private string _title = "";

    public CardPanel()
    {
        Padding = new Padding(14);
        CornerRadius = 8f;
    }

    /// <summary>圆角半径（像素，会按 DPI 缩放）。</summary>
    public float CornerRadius { get; set; }

    /// <summary>卡面颜色，null = 用主题 Surface。</summary>
    public Color? FillColor
    {
        get => _fill;
        set { _fill = value; Invalidate(); }
    }

    /// <summary>描边颜色，null = 用主题 Border。</summary>
    public Color? StrokeColor { get; set; }

    public string TitleText
    {
        get => _title;
        set { _title = value ?? ""; Invalidate(); }
    }

    public override Color ContentBackColor => _fill ?? Theme.Current.Surface;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var p = Theme.Current;
        var g = e.Graphics;
        var s = DpiScale;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var r = new RectangleF(0.5f * s, 0.5f * s, Width - 1f * s, Height - 1f * s);
        Gfx.FillRound(g, _fill ?? p.Surface, r, CornerRadius * s);
        Gfx.DrawRound(g, StrokeColor ?? p.Border, 1f * s, r, CornerRadius * s);

        if (_title.Length > 0)
        {
            using var font = new Font(Font, FontStyle.Bold);
            using var brush = new SolidBrush(p.Text);
            g.DrawString(_title, font, brush, new PointF(Padding.Left, Padding.Top));
        }
    }
}

/// <summary>
/// 扁平按钮。不用系统 Button 是因为它的 Flat 外观仍然受主题/视觉样式牵制，
/// 而 Accent（强调蓝 / 深色提亮蓝）必须在两套配色下都压得住。
/// </summary>
internal sealed class AccentButton : BufferedPanel
{
    private bool _hover;
    private bool _pressed;
    private bool _primary = true;
    private Color? _fill;

    public AccentButton()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Primary = true;
        Width = 108;
        Height = 32;
        Cursor = Cursors.Hand;
        Padding = new Padding(14, 0, 14, 0);
    }

    /// <summary>true = 实心强调色；false = 面 + 边框的次级按钮。</summary>
    /// <remarks>
    /// 必须走 setter 触发 <see cref="Control.Invalidate"/>：调用方（如 TrendTab 切视图/范围）
    /// 只改这一位就指望按钮立刻变样。用自动属性的话"失去选中态的那个按钮"不会重绘，
    /// 要等鼠标划过它（OnMouseEnter 里那次 Invalidate）才变灰——2026-10-10 实际反馈的正是这个。
    /// </remarks>
    public bool Primary
    {
        get => _primary;
        set
        {
            if (_primary == value) return;
            _primary = value;
            Invalidate();
        }
    }

    /// <summary>覆盖底色，null = Primary 用 Accent、否则用 Surface。</summary>
    public Color? FillColor
    {
        get => _fill;
        set
        {
            if (_fill == value) return;
            _fill = value;
            Invalidate();
        }
    }

    public override Color ContentBackColor => CurrentFace();

    private Color CurrentFace()
    {
        var p = Theme.Current;
        var c = _fill ?? (_primary ? p.Accent : p.Surface);
        if (!Enabled) return Gfx.Mix(c, p.Bg, 0.55);
        if (_pressed) return Gfx.Mix(c, p.IsDark ? Color.White : Color.Black, p.IsDark ? 0.20 : 0.12);
        if (_hover) return Gfx.Mix(c, p.IsDark ? Color.White : Color.Black, p.IsDark ? 0.12 : 0.07);
        return c;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        var s = DpiScale;
        var face = CurrentFace();

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var r = new RectangleF(0.5f * s, 0.5f * s, Width - 1f * s, Height - 1f * s);
        Gfx.FillRound(g, face, r, 4f * s);
        if (!Primary)
            Gfx.DrawRound(g, p.Border, 1f * s, r, 4f * s);

        if (Text.Length > 0)
        {
            var fc = Primary ? Gfx.ContrastOn(face) : (Enabled ? p.Text : p.TextDim);
            using var brush = new SolidBrush(fc);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(Text, Font, brush, r, sf);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }
}

/// <summary>
/// 分区标题 + 分隔线。今日概览的「供电」「今日概览」这类小节头用它。
/// 文字贴底左对齐，下方一条 Border 细线。
/// </summary>
internal sealed class SectionLabel : BufferedPanel
{
    public SectionLabel()
    {
        Height = 26;
        Padding = new Padding(2, 6, 2, 4);
        ShowRule = true;
    }

    /// <summary>底部是否画一条分隔线。</summary>
    public bool ShowRule { get; set; }

    public override Color ContentBackColor => Theme.Current.Bg;

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        var s = DpiScale;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var font = new Font(Font, FontStyle.Bold);
        using var brush = new SolidBrush(p.Text);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Far,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        var textRect = new RectangleF(Padding.Left, 0,
            Width - Padding.Horizontal, Height - (ShowRule ? 1f * s : 0f));
        g.DrawString(Text, font, brush, textRect, sf);

        if (ShowRule)
        {
            using var pen = new Pen(p.Border, 1f * s);
            g.DrawLine(pen, 0f, Height - 1f * s, Width, Height - 1f * s);
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
    }
}
