using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace ThermalTrace.UI.Charts;

/// <summary>折线上的一点。Value 为 NaN = 该日该类别没采到，线在此断开，不伪造连线。</summary>
internal sealed record ChartPoint(DateOnly Date, double Value);

/// <summary>
/// 一条折线。
/// <para>
/// Unit / ShowSign 只影响 Tooltip 的数值格式（℃ 带符号是 ΔT 的惯例，GB/h/% 不带）。
/// </para>
/// <para>
/// RightAxis = 画在右侧 Y 轴：量纲不同的两条线（如 ℃ 与 h、GB 与 %）必须分开轴，
/// 挤在同一根轴上时大量纲的那条会把小量纲的压成一条贴地直线。
/// </para>
/// <para>
/// ForceZero = 该轴是否把 0 基线纳入：ΔT 的 0 是"与空闲同温"，有语义；
/// 绝对温度 / 累计量纳入 0 只会把曲线压成顶部一条细线。
/// </para>
/// </summary>
internal sealed record ChartSeries(
    string Name,
    Color Color,
    IReadOnlyList<ChartPoint> Points,
    string Unit = "℃",
    bool ShowSign = true,
    bool RightAxis = false,
    bool ForceZero = true,
    DashStyle LineStyle = DashStyle.Solid,
    IReadOnlySet<DateOnly>? Marked = null);

/// <summary>
/// 两个图表共用的刻度计算。
/// 集中在这里是因为"Y 轴留 8% 余量"和"取整齐刻度"两份逻辑若各写一遍必然漂移。
/// </summary>
internal static class ChartMath
{
    /// <summary>
    /// 求 Y 轴范围，上下各留 8% 余量，别让曲线贴边。
    /// </summary>
    /// <param name="forceZero">是否把 0 基线纳入范围。ΔT 的 0 有语义，必须纳入；
    /// 绝对温度 / 累计量纳入了 0 只会把曲线压成顶部一条细线。</param>
    public static (double Lo, double Hi) WithMargin(double min, double max, bool forceZero = true)
    {
        double lo = forceZero ? Math.Min(0, min) : min;
        double hi = forceZero ? Math.Max(0, max) : max;
        double span = hi - lo;
        if (span < 1e-6) span = Math.Max(1.0, Math.Abs(hi) * 0.2);
        return (lo - span * 0.08, hi + span * 0.08);
    }

    /// <summary>取 4~6 档整齐刻度（1 / 2 / 2.5 / 5 / 10 × 10ⁿ）。</summary>
    public static List<double> NiceTicks(double lo, double hi)
    {
        var list = new List<double>();
        double raw = (hi - lo) / 4.0;
        if (raw <= 0) raw = 1.0;

        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = norm <= 1 ? mag
                    : norm <= 2 ? 2 * mag
                    : norm <= 2.5 ? 2.5 * mag
                    : norm <= 5 ? 5 * mag
                    : 10 * mag;

        for (double v = Math.Ceiling(lo / step) * step;
             v <= hi + step * 1e-6 && list.Count < 12;
             v += step)
        {
            list.Add(Math.Abs(v) < step * 1e-6 ? 0.0 : v);
        }

        if (list.Count < 2)
        {
            list.Clear();
            list.Add(lo);
            list.Add(hi);
        }
        return list;
    }

    /// <summary>刻度文案。带符号的量（ΔT）另行格式化，这里只管坐标数字。</summary>
    public static string Tick(double v) => v.ToString("0.##");

    public static string Signed(double v, int digits = 1) =>
        (v >= 0 ? "+" : "") + v.ToString("0." + new string('0', digits));

    /// <summary>无量纲数值的通用格式（Tooltip 里的 GB / h / % 用）。</summary>
    public static string Val(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// 自绘折线图：X = 日期，Y = ΔT，每个负载类别一条线。
///
/// 【为什么自己画】第三方图表库一律不引，且改桶边界后要能同步重算重绘。
///
/// 【唯一的交互是十字准星 + Tooltip】没有它用户读不出某天某类的具体数值——
/// 验收第 1 项就是这一条。其余一律不做（不缩放、不框选、不导出）。
///
/// 【DPI】坐标全部按 float 算，线宽/描边/内缩乘 <c>DpiScale</c>；
/// 字号<b>不乘</b>——字体按 point 创建，Graphics 会自行按 DPI 换算，乘了就是双重缩放。
/// </summary>
internal sealed class LineChart : BufferedPanel
{
    private IReadOnlyList<DateOnly> _axis = Array.Empty<DateOnly>();
    private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    private readonly List<double[]> _values = new();
    private readonly List<bool[]> _marked = new();

    private RectangleF _plot;
    private float[] _xs = Array.Empty<float>();
    private double _loL, _hiL;          // 左轴范围
    private double _loR, _hiR;          // 右轴范围
    private bool _hasLeft, _hasRight;   // 某一轴没有任何数据时不画它的刻度
    private int _hover = -1;

    public LineChart()
    {
        Padding = new Padding(6);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Cross;
    }

    /// <summary>Y 轴标题（画在图例行左侧）。</summary>
    public string AxisTitle { get; set; } = "ΔT ℃";

    /// <summary>无数据时的居中提示。</summary>
    public string EmptyText { get; set; } = "";

    /// <summary>
    /// 装数据并立即重绘。
    /// 内部先把每个类别的值按 X 轴下标摊平（缺的填 NaN），
    /// 这样绘制和悬停命中都只要查一次表，不必每次遍历日期字典。
    /// </summary>
    public void SetData(IReadOnlyList<DateOnly> axis, IReadOnlyList<ChartSeries> series)
    {
        _axis = axis ?? Array.Empty<DateOnly>();
        _series = series ?? Array.Empty<ChartSeries>();

        var index = new Dictionary<DateOnly, int>(_axis.Count);
        for (int i = 0; i < _axis.Count; i++) index[_axis[i]] = i;

        _values.Clear();
        _marked.Clear();
        foreach (var s in _series)
        {
            var arr = new double[_axis.Count];
            var marks = new bool[_axis.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = double.NaN;
            foreach (var pt in (s.Points ?? Array.Empty<ChartPoint>()))
            {
                if (!index.TryGetValue(pt.Date, out var k)) continue;
                arr[k] = pt.Value;
                if (s.Marked is not null) marks[k] = s.Marked.Contains(pt.Date);
            }
            _values.Add(arr);
            _marked.Add(marks);
        }

        _hover = -1;
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

        if (_axis.Count == 0 || _series.Count == 0)
        {
            DrawCenter(g, p, EmptyText.Length > 0 ? EmptyText : "当前范围内没有可绘制的数据", outer);
            return;
        }

        // ---------------- 左右轴各自的范围 ----------------
        // 量纲不同的线（℃ 与 h、GB 与 %）必须分轴：挤在同一根轴上时，
        // 大量纲的那条会把小量纲的压成一条贴地直线，图就白画了。
        double minL = double.MaxValue, maxL = double.MinValue;
        double minR = double.MaxValue, maxR = double.MinValue;
        bool zeroL = false, zeroR = false;
        for (int si = 0; si < _series.Count; si++)
        {
            bool onRight = _series[si].RightAxis;
            if (onRight) zeroR |= _series[si].ForceZero; else zeroL |= _series[si].ForceZero;
            foreach (var v in _values[si])
            {
                if (double.IsNaN(v)) continue;
                if (onRight) { if (v < minR) minR = v; if (v > maxR) maxR = v; }
                else { if (v < minL) minL = v; if (v > maxL) maxL = v; }
            }
        }

        _hasLeft = minL != double.MaxValue;
        _hasRight = minR != double.MaxValue;
        if (!_hasLeft && !_hasRight)
        {
            DrawCenter(g, p, EmptyText.Length > 0 ? EmptyText : "当前范围内没有可绘制的数据", outer);
            return;
        }

        // 只有一轴有数据时，另一轴借用它的范围（那一轴不出刻度，只为 Y() 有数可用）
        if (!_hasLeft) { minL = minR; maxL = maxR; }
        if (!_hasRight) { minR = minL; maxR = maxL; }

        (_loL, _hiL) = ChartMath.WithMargin(minL, maxL, _hasLeft && zeroL);
        (_loR, _hiR) = ChartMath.WithMargin(minR, maxR, _hasRight && zeroR);

        using var tickFont = new Font(Font.FontFamily, 8.5f);
        using var legendFont = new Font(Font.FontFamily, 9f);
        using var labelBrush = new SolidBrush(p.TextDim);

        // ---------------- 布局 ----------------
        var leftTicks = _hasLeft ? ChartMath.NiceTicks(_loL, _hiL) : new List<double>();
        var rightTicks = _hasRight ? ChartMath.NiceTicks(_loR, _hiR) : new List<double>();

        float yLabelW = 0f;
        foreach (var t in leftTicks)
            yLabelW = Math.Max(yLabelW, g.MeasureString(ChartMath.Tick(t), tickFont).Width);
        float yLabelWR = 0f;
        foreach (var t in rightTicks)
            yLabelWR = Math.Max(yLabelWR, g.MeasureString(ChartMath.Tick(t), tickFont).Width);

        float pad = Padding.Left + 2f * s;
        float left = pad + (_hasLeft ? yLabelW + 8f * s : 4f * s);
        float right = pad + (_hasRight ? yLabelWR + 8f * s : 12f * s);
        float plotW = Math.Max(12f, Width - left - right);

        // 图例排不下时换第二行，宁可让出 18px 图高，也不截掉"空闲基线 / 环境气温"。
        float legendW = AxisTitle.Length > 0 ? g.MeasureString(AxisTitle, legendFont).Width + 16f * s : 0f;
        foreach (var ser in _series)
            legendW += 18f * s + 4f * s + g.MeasureString(ser.Name, legendFont).Width + 14f * s;
        bool wrapLegend = legendW > plotW;

        float top = pad + (wrapLegend ? 40f : 22f) * s;
        float bottom = pad + 22f * s;              // 日期占一行

        _plot = new RectangleF(left, top, plotW,
            Math.Max(12f, Height - top - bottom));

        _xs = new float[_axis.Count];
        for (int i = 0; i < _xs.Length; i++)
            _xs[i] = _axis.Count == 1
                ? _plot.Left + _plot.Width / 2f
                : _plot.Left + _plot.Width * i / (_axis.Count - 1);

        // ---------------- 网格与 Y 刻度（左轴 + 右轴） ----------------
        using (var gridPen = new Pen(Gfx.Mix(p.Border, p.Text, 0.12), 1f * s))
        using (var rightFmt = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        })
        {
            foreach (var t in leftTicks)
            {
                float y = Y(t, false);
                g.DrawLine(gridPen, _plot.Left, y, _plot.Right, y);
                g.DrawString(ChartMath.Tick(t), tickFont, labelBrush,
                    new RectangleF(pad, y - 9f * s, yLabelW + 2f, 18f * s), rightFmt);
            }
            foreach (var t in rightTicks)
            {
                float y = Y(t, true);
                g.DrawLine(gridPen, _plot.Left, y, _plot.Right, y);
                g.DrawString(ChartMath.Tick(t), tickFont, labelBrush,
                    new RectangleF(_plot.Right + 6f * s, y - 9f * s, yLabelWR + 2f, 18f * s), rightFmt);
            }
        }

        // 0 基线加粗一根：ΔT = 0 是"与空闲同温"，视觉上要能一眼看到谁在它上面。
        // 双轴时两根轴的 0 不在同一高度，各自画。
        using (var zeroPen = new Pen(Gfx.Mix(p.TextDim, p.Surface, 0.45), 1f * s))
        {
            if (_hasLeft && _loL < 0 && _hiL > 0)
                g.DrawLine(zeroPen, _plot.Left, Y(0, false), _plot.Right, Y(0, false));
            if (_hasRight && _loR < 0 && _hiR > 0)
                g.DrawLine(zeroPen, _plot.Left, Y(0, true), _plot.Right, Y(0, true));
        }

        // ---------------- X 日期 ----------------
        DrawDateAxis(g, p, s, tickFont, labelBrush);

        // ---------------- 图例 ----------------
        DrawLegend(g, p, s, legendFont, labelBrush, wrapLegend);

        // ---------------- 折线 ----------------
        DrawLines(g, s);

        // ---------------- 十字准星 + Tooltip ----------------
        if (_hover >= 0 && _hover < _axis.Count)
            DrawCrosshair(g, p, s, legendFont, outer);
    }

    private float Y(double v, bool right)
    {
        var lo = right ? _loR : _loL;
        var hi = right ? _hiR : _hiL;
        return (float)(_plot.Bottom - (v - lo) / (hi - lo) * _plot.Height);
    }

    private void DrawDateAxis(Graphics g, ColorPalette p, float s, Font tickFont, Brush brush)
    {
        float labelW = g.MeasureString("00-00", tickFont).Width;
        int maxLabels = Math.Max(2, (int)(_plot.Width / (labelW + 14f * s)));
        int step = Math.Max(1, (int)Math.Ceiling(_axis.Count / (double)maxLabels));

        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap
        };

        int last = -1;
        for (int i = 0; i < _axis.Count; i += step)
        {
            DrawOne(i);
            last = i;
        }

        // 末点必显示，但要和前一个标签留出至少 0.6 步的间距，否则会挤成一团
        int tail = _axis.Count - 1;
        if (tail > last && (tail - last) >= step * 0.6) DrawOne(tail);

        void DrawOne(int i)
        {
            if (i == _hover)
            {
                using var hot = new SolidBrush(p.Text);
                g.DrawString(_axis[i].ToString("MM-dd"), tickFont, hot,
                    new RectangleF(_xs[i] - 34f * s, _plot.Bottom + 4f * s, 68f * s, 16f * s), fmt);
            }
            else
            {
                g.DrawString(_axis[i].ToString("MM-dd"), tickFont, brush,
                    new RectangleF(_xs[i] - 34f * s, _plot.Bottom + 4f * s, 68f * s, 16f * s), fmt);
            }
        }
    }

    private void DrawLegend(Graphics g, ColorPalette p, float s, Font font, Brush brush, bool wrap)
    {
        using var titleBrush = new SolidBrush(p.TextDim);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        float x = _plot.Left;
        float y = Padding.Top + 2f * s;

        if (AxisTitle.Length > 0)
        {
            float w = g.MeasureString(AxisTitle, font).Width;
            g.DrawString(AxisTitle, font, titleBrush, new RectangleF(x, y, w + 2f, 16f * s), fmt);
            x += w + 16f * s;
        }

        float rowStart = _plot.Left;
        bool secondRow = false;
        foreach (var ser in _series)
        {
            float tw = g.MeasureString(ser.Name, font).Width;
            float itemW = 18f * s + 4f * s + tw + 14f * s;
            if (wrap && !secondRow && x + itemW > _plot.Right && x > rowStart)
            {
                secondRow = true;
                x = rowStart;
                y += 18f * s;
            }
            if (x + itemW > _plot.Right) break;    // 两行仍放不下才截断，不再往下换行挤图

            if (ser.LineStyle == DashStyle.Solid)
            {
                Gfx.FillRound(g, ser.Color, new RectangleF(x, y + 6f * s, 18f * s, 3.5f * s), 1.6f * s);
            }
            else
            {
                using var pen = new Pen(ser.Color, 1.8f * s) { DashStyle = ser.LineStyle };
                g.DrawLine(pen, x, y + 7.5f * s, x + 18f * s, y + 7.5f * s);
            }
            g.DrawString(ser.Name, font, brush, new RectangleF(x + 22f * s, y, tw + 2f, 16f * s), fmt);
            x += itemW;
        }
    }

    private void DrawLines(Graphics g, float s)
    {
        bool sparse = _axis.Count <= 12;

        for (int si = 0; si < _series.Count; si++)
        {
            var arr = _values[si];
            using var pen = new Pen(_series[si].Color, 2f * s)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                DashStyle = _series[si].LineStyle
            };
            using var dot = new SolidBrush(_series[si].Color);

            var run = new List<PointF>();
            for (int i = 0; i < arr.Length; i++)
            {
                if (double.IsNaN(arr[i]))
                {
                    Flush(run, pen, dot, g, s, sparse);
                    continue;
                }

                var pt = new PointF(_xs[i], Y(arr[i], _series[si].RightAxis));
                run.Add(pt);
                if (sparse) g.FillEllipse(dot, pt.X - 2.6f * s, pt.Y - 2.6f * s, 5.2f * s, 5.2f * s);
            }
            Flush(run, pen, dot, g, s, sparse);

            // 标记日画空心点：近似基线这类"口径不同"的点必须在图上能认出来，
            // 不能和实测点混成一条同款实线。
            var marks = _marked[si];
            using var ring = new Pen(_series[si].Color, 1.4f * s);
            using var fill = new SolidBrush(Theme.Current.Surface);
            for (int i = 0; i < arr.Length; i++)
            {
                if (!marks[i] || double.IsNaN(arr[i])) continue;
                var pt = new PointF(_xs[i], Y(arr[i], _series[si].RightAxis));
                g.FillEllipse(fill, pt.X - 3f * s, pt.Y - 3f * s, 6f * s, 6f * s);
                g.DrawEllipse(ring, pt.X - 3f * s, pt.Y - 3f * s, 6f * s, 6f * s);
            }
        }

        // 孤立的一天（前后都断档）也要看得见：DrawLines 画不出单点，补一个小圆
        static void Flush(List<PointF> run, Pen pen, Brush dot, Graphics g, float s, bool sparse)
        {
            if (run.Count >= 2)
            {
                g.DrawLines(pen, run.ToArray());
            }
            else if (run.Count == 1 && !sparse)
            {
                var pt = run[0];
                g.FillEllipse(dot, pt.X - 2.2f * s, pt.Y - 2.2f * s, 4.4f * s, 4.4f * s);
            }
            run.Clear();
        }
    }

    private void DrawCrosshair(Graphics g, ColorPalette p, float s, Font font, RectangleF outer)
    {
        float x = _xs[_hover];

        using (var cross = new Pen(Gfx.Mix(p.TextDim, p.Surface, 0.40), 1f * s) { DashStyle = DashStyle.Dash })
            g.DrawLine(cross, x, _plot.Top, x, _plot.Bottom);

        float topMost = float.MaxValue;
        for (int si = 0; si < _values.Count; si++)
        {
            var v = _values[si][_hover];
            if (double.IsNaN(v)) continue;

            float y = Y(v, _series[si].RightAxis);
            topMost = Math.Min(topMost, y);

            using var dot = new SolidBrush(_series[si].Color);
            g.FillEllipse(dot, x - 3.4f * s, y - 3.4f * s, 6.8f * s, 6.8f * s);
            using var ring = new Pen(p.Surface, 1.4f * s);
            g.DrawEllipse(ring, x - 3.4f * s, y - 3.4f * s, 6.8f * s, 6.8f * s);
        }

        DrawTooltip(g, p, s, font, x, topMost, outer);
    }

    /// <summary>
    /// 自绘 Tooltip（不用系统 ToolTip：它不跟随主题，且在自绘控件上闪烁明显）。
    /// 位置锚在准星那一列（而不是跟着鼠标跑），这样只有换列才需要重绘。
    /// </summary>
    private void DrawTooltip(Graphics g, ColorPalette p, float s, Font font,
        float x, float topMost, RectangleF outer)
    {
        using var titleFont = new Font(Font.FontFamily, 9f, FontStyle.Bold);
        using var rowFont = new Font(Font.FontFamily, 9f);

        var title = _axis[_hover].ToString("yyyy-MM-dd");
        var rows = new List<(Color Color, string Name, string Value)>();
        for (int si = 0; si < _values.Count; si++)
        {
            var v = _values[si][_hover];
            if (double.IsNaN(v)) continue;
            var ser = _series[si];
            var text = (ser.ShowSign ? ChartMath.Signed(v) : ChartMath.Val(v)) + ser.Unit;
            var name = _marked[si][_hover] ? ser.Name + "（近似）" : ser.Name;
            rows.Add((ser.Color, name, text));
        }
        if (rows.Count == 0) rows.Add((p.TextDim, "该日无数据", "—"));

        float lineH = 15f * s;
        float boxW = g.MeasureString(title, titleFont).Width;
        foreach (var r in rows)
            boxW = Math.Max(boxW, 14f * s + g.MeasureString(r.Name, rowFont).Width + 10f * s
                                  + g.MeasureString(r.Value, titleFont).Width);
        boxW += 16f * s;
        float boxH = lineH * (1 + rows.Count) + 12f * s;

        float bx = x + 12f * s;
        if (bx + boxW > outer.Right - 4f * s) bx = x - 12f * s - boxW;
        bx = Math.Clamp(bx, outer.Left + 4f * s, Math.Max(outer.Left + 4f * s, outer.Right - 4f * s - boxW));

        float by = _plot.Top + 6f * s;
        // 悬停的点在上方时把框挪到底部，别把要看的点挡住
        if (topMost < by + boxH + 8f * s) by = _plot.Bottom - boxH - 6f * s;
        by = Math.Clamp(by, outer.Top + 4f * s, Math.Max(outer.Top + 4f * s, outer.Bottom - 4f * s - boxH));

        var box = new RectangleF(bx, by, boxW, boxH);
        Gfx.FillRound(g, Gfx.Mix(p.Surface, p.Text, 0.06), box, 4f * s);
        Gfx.DrawRound(g, p.Border, 1f * s, box, 4f * s);

        using var near = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        using var far = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        using var titleBrush = new SolidBrush(p.Text);
        using var nameBrush = new SolidBrush(p.TextDim);

        float tx = bx + 8f * s;
        float ty = by + 6f * s;
        float innerW = boxW - 16f * s;

        g.DrawString(title, titleFont, titleBrush, new RectangleF(tx, ty, innerW, lineH), near);
        ty += lineH;

        foreach (var r in rows)
        {
            Gfx.FillRound(g, r.Color, new RectangleF(tx + 1f * s, ty + lineH / 2f - 2f * s, 10f * s, 3.5f * s), 1.5f * s);
            using var valueBrush = new SolidBrush(p.Text);
            g.DrawString(r.Name, rowFont, nameBrush,
                new RectangleF(tx + 14f * s, ty, innerW - 24f * s, lineH), near);
            g.DrawString(r.Value, titleFont, valueBrush,
                new RectangleF(tx, ty, innerW, lineH), far);
            ty += lineH;
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

    // ------------------------------------------------------------------
    // 悬停：只有这一处交互
    // ------------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_xs.Length == 0) return;

        int idx = NearestIndex(e.X);
        if (idx == _hover) return;      // 只有换列才重绘：Tooltip 锚在列上，不跟鼠标跑
        _hover = idx;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover >= 0)
        {
            _hover = -1;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    private int NearestIndex(int mouseX)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < _xs.Length; i++)
        {
            float d = Math.Abs(_xs[i] - mouseX);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
