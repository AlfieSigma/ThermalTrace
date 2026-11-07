using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ThermalTrace.UI.Charts;

/// <summary>
/// 一个负载类别的周期对比：本期 vs 上期的 ΔT。
/// 两侧都可能为 null（那一侧没数据）——此类组由调用方按
/// <c>CategoryComparison.Comparable</c> 提前滤掉，这里不画半截柱子。
/// </summary>
internal sealed record BarGroup(string Category, double? Cur, double? Prev)
{
    public double? Diff => Cur.HasValue && Prev.HasValue ? Cur.Value - Prev.Value : null;
}

/// <summary>
/// 自绘分组柱状图：每类别两根柱并排（本期 / 上期），Δ 值标在柱顶。
///
/// 【数据来源】直接吃 <c>TrendReport.Month</c> / <c>Week</c> / <c>Comparison</c> 的
/// <c>CategoryComparison</c>，自己不做任何聚合——与 <c>--trend</c> 报告同源同值。
///
/// 【不画什么】不画 3D、阴影、渐变（WinForms 下这些一律显廉价）。
/// 上期柱用 Accent 与卡面的中间色，同色系但明显更淡，一眼能分主次。
/// </summary>
internal sealed class BarChart : BufferedPanel
{
    private IReadOnlyList<BarGroup> _groups = Array.Empty<BarGroup>();

    public BarChart()
    {
        Padding = new Padding(6);
        SetStyle(ControlStyles.Selectable, false);
    }

    /// <summary>本期柱的图例名。</summary>
    public string CurLabel { get; set; } = "本期";

    /// <summary>上期柱的图例名。</summary>
    public string PrevLabel { get; set; } = "上期";

    /// <summary>无柱可画时的居中提示（由页面按场景给具体原因）。</summary>
    public string EmptyText { get; set; } = "";

    public void SetData(IReadOnlyList<BarGroup> groups)
    {
        _groups = groups ?? Array.Empty<BarGroup>();
        Invalidate();
    }

    public override Color ContentBackColor => Theme.Current.Surface;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var p = Theme.Current;
        var g = e.Graphics;
        var s = g.DpiX <= 0 ? 1f : g.DpiX / 96f;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var outer = new RectangleF(0.5f * s, 0.5f * s, Width - 1f * s, Height - 1f * s);
        Gfx.FillRound(g, p.Surface, outer, 6f * s);
        Gfx.DrawRound(g, p.Border, 1f * s, outer, 6f * s);

        if (_groups.Count == 0)
        {
            DrawCenter(g, p, EmptyText.Length > 0 ? EmptyText : "当前范围内没有可对比的数据", outer);
            return;
        }

        double min = 0, max = 0;
        foreach (var grp in _groups)
        {
            if (grp.Cur is { } c) { min = Math.Min(min, c); max = Math.Max(max, c); }
            if (grp.Prev is { } q) { min = Math.Min(min, q); max = Math.Max(max, q); }
        }
        var (lo, hi) = ChartMath.WithMargin(min, max);

        using var tickFont = new Font(Font.FontFamily, 8.5f);
        using var legendFont = new Font(Font.FontFamily, 9f);
        using var labelBrush = new SolidBrush(p.TextDim);

        // ---------------- 布局 ----------------
        var ticks = ChartMath.NiceTicks(lo, hi);
        float yLabelW = 0f;
        foreach (var t in ticks)
            yLabelW = Math.Max(yLabelW, g.MeasureString(ChartMath.Tick(t), tickFont).Width);

        float pad = Padding.Left + 2f * s;
        float left = pad + yLabelW + 8f * s;
        float top = pad + 22f * s;             // 图例
        float bottom = pad + 34f * s;          // 类别名 + Δ
        float right = pad + 12f * s;

        var plot = new RectangleF(left, top,
            Math.Max(12f, Width - left - right),
            Math.Max(12f, Height - top - bottom));

        float Y(double v) => (float)(plot.Bottom - (v - lo) / (hi - lo) * plot.Height);

        // ---------------- 网格与刻度 ----------------
        using (var gridPen = new Pen(Gfx.Mix(p.Border, p.Text, 0.12), 1f * s))
        using (var rightFmt = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        })
        {
            foreach (var t in ticks)
            {
                float y = Y(t);
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                g.DrawString(ChartMath.Tick(t), tickFont, labelBrush,
                    new RectangleF(pad, y - 9f * s, yLabelW + 2f, 18f * s), rightFmt);
            }
        }

        float zeroY = Y(0);
        using (var zeroPen = new Pen(Gfx.Mix(p.TextDim, p.Surface, 0.45), 1f * s))
            g.DrawLine(zeroPen, plot.Left, zeroY, plot.Right, zeroY);

        // ---------------- 柱 ----------------
        var curColor = p.Accent;
        var prevColor = Gfx.Mix(p.Accent, p.Surface, 0.62);

        float groupW = plot.Width / _groups.Count;
        float gap = 6f * s;
        float barW = Math.Max(4f * s, Math.Min(26f * s, (groupW - gap * 2f) / 2f));

        using var catFont = new Font(Font.FontFamily, 9f);
        using var diffFont = new Font(Font.FontFamily, 8.5f);
        using var centerFmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        for (int i = 0; i < _groups.Count; i++)
        {
            var grp = _groups[i];
            float gx = plot.Left + groupW * i + groupW / 2f;
            float x0 = gx - barW - gap / 2f;
            float x1 = gx + gap / 2f;

            DrawBar(g, s, x0, barW, zeroY, Y, grp.Cur, curColor, p);
            DrawBar(g, s, x1, barW, zeroY, Y, grp.Prev, prevColor, p);

            // 类别名 + Δ（Δ 用语义色：变大偏热、变小偏凉，阈值与卡片页一致）
            using var catBrush = new SolidBrush(p.Text);
            g.DrawString(grp.Category, catFont, catBrush,
                new RectangleF(gx - groupW / 2f + 2f, plot.Bottom + 3f * s, groupW - 4f, 16f * s), centerFmt);

            if (grp.Diff is { } d)
            {
                var dc = d > 0.3 ? p.Hot : d < -0.3 ? p.Cool : p.TextDim;
                using var diffBrush = new SolidBrush(dc);
                g.DrawString("Δ " + ChartMath.Signed(d), diffFont, diffBrush,
                    new RectangleF(gx - groupW / 2f + 2f, plot.Bottom + 19f * s, groupW - 4f, 14f * s), centerFmt);
            }
        }

        // ---------------- 图例 ----------------
        DrawLegend(g, p, s, legendFont, labelBrush, plot, curColor, prevColor, pad);
    }

    private void DrawBar(Graphics g, float s, float x, float barW, float zeroY,
        Func<double, float> yOf, double? value, Color color, ColorPalette p)
    {
        if (value is not { } v) return;

        float y = yOf(v);
        float top = Math.Min(zeroY, y);
        float h = Math.Max(1.5f * s, Math.Abs(y - zeroY));
        Gfx.FillRound(g, color, new RectangleF(x, top, barW, h), 2f * s);

        // Δ 值标在柱顶（负值标在柱底）
        using var font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
        using var brush = new SolidBrush(p.Text);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap
        };
        float ty = v >= 0 ? top - 13f * s : top + h + 1f * s;
        g.DrawString(ChartMath.Signed(v), font, brush,
            new RectangleF(x - 10f * s, ty, barW + 20f * s, 13f * s), fmt);
    }

    private void DrawLegend(Graphics g, ColorPalette p, float s, Font font, Brush brush,
        RectangleF plot, Color curColor, Color prevColor, float pad)
    {
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };

        float x = plot.Left;
        float y = pad;
        foreach (var (color, name) in new[] { (curColor, CurLabel), (prevColor, PrevLabel) })
        {
            float tw = g.MeasureString(name, font).Width;
            Gfx.FillRound(g, color, new RectangleF(x, y + 4f * s, 12f * s, 9f * s), 2f * s);
            g.DrawString(name, font, brush, new RectangleF(x + 16f * s, y, tw + 2f, 16f * s), fmt);
            x += 16f * s + tw + 18f * s;
        }
    }

    private void DrawCenter(Graphics g, ColorPalette p, string text, RectangleF rect)
    {
        using var font = new Font(Font.FontFamily, 10f);
        using var brush = new SolidBrush(p.TextDim);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(text, font, brush, rect, fmt);
    }
}
