using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using ThermalTrace.Analysis;

namespace ThermalTrace.UI.Charts;

/// <summary>分档时长条带中的一格：某类别在当前窗口内的累计量。</summary>
internal sealed record UsageCell(string Name, Color Color, double Hours, long Samples);

/// <summary>
/// 趋势页的分档时长条带：一行环境摘要 + 一条按占比划分的分段条 + 五格明细。
///
/// 【为什么自绘】五格要同时放下档名、占比、时长、样本数，且 760×520 下每格只有约 130px；
/// 五个 Label 拼起来必然出现某一格先截断、另一格还是大字。
///
/// 【数据口径】Hours / Samples 由 <c>TrendAnalyzer.WindowUsage</c> 给出，
/// 与 CSV 的 <c>n</c> 同一套样本加权；本控件只做排版与百分比换算，不重新估算时长。
/// </summary>
internal sealed class CategoryUsageStrip : BufferedPanel
{
    private string _headline = "";
    private string _environment = "";
    private IReadOnlyList<UsageCell> _cells = Array.Empty<UsageCell>();

    public CategoryUsageStrip()
    {
        Padding = new Padding(0);
    }

    public override Color ContentBackColor => Theme.Current.Surface;

    /// <summary>左上标题，例：<c>本期各档时长（AC+DC） · 91h24m</c>。</summary>
    public void SetData(string headline, string environment, IReadOnlyList<UsageCell> cells)
    {
        _headline = headline ?? "";
        _environment = environment ?? "";
        _cells = cells ?? Array.Empty<UsageCell>();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;

        // 布局单位用物理像素：行高由 TableLayoutPanel 给的是物理值（不会跟着缩放），
        // 这里再乘 DpiScale 会把第二行文字推出控件底部。字号用 point，系统自己按 DPI 放大；
        // 行高按实测字体高度算，125% / 150% 下都不会裁字。
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Gfx.FillRound(g, p.Surface, rect, 6f);
        Gfx.DrawRound(g, p.Border, 1f, rect, 6f);

        var pad = 12f;
        var innerW = Math.Max(4f, Width - pad * 2f);
        var top = 5f;

        using var headFont = new Font(Font.FontFamily, 9f);
        using var noteFont = new Font(Font.FontFamily, 8.5f);
        using var dim = new SolidBrush(p.TextDim);
        using var strong = new SolidBrush(p.Text);

        float headH = Math.Clamp(g.MeasureString("基线Ag", headFont).Height, 15f, 19f);

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
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        // ---- 第一行：标题 + 环境摘要 ----
        float headW = Math.Min(innerW, g.MeasureString(_headline, headFont).Width + 2f);
        g.DrawString(_headline, headFont, strong, new RectangleF(pad, top, headW, headH), near);

        float noteX = pad + headW + 12f;
        float noteW = Math.Max(0f, Width - pad - noteX);
        if (noteW > 24f)
        {
            g.DrawString(_environment, noteFont, dim,
                new RectangleF(noteX, top, noteW, headH), far);
        }

        // ---- 分段条：按小时占比切分，总宽固定 ----
        var bar = new RectangleF(pad, top + headH + 2f, innerW, 5f);
        Gfx.FillRound(g, p.Border, bar, 2.5f);

        var total = _cells.Sum(c => c.Hours > 0 ? c.Hours : 0);
        if (total > 0)
        {
            int last = -1;
            for (int i = 0; i < _cells.Count; i++)
                if (_cells[i].Hours > 0) last = i;

            float x = bar.Left;
            for (int i = 0; i < _cells.Count; i++)
            {
                var c = _cells[i];
                if (c.Hours <= 0) continue;

                float w = i == last ? bar.Right - x : innerW * (float)(c.Hours / total);
                using var seg = new SolidBrush(c.Color);
                g.FillRectangle(seg, x, bar.Top, Math.Max(1f, w), bar.Height);
                x += w;
            }
        }

        // ---- 五格明细 ----
        if (_cells.Count == 0) return;

        using var nameFont = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
        using var metaFont = new Font(Font.FontFamily, 8f);
        float nameH = Math.Clamp(g.MeasureString("样本Ag", nameFont).Height, 15f, 19f);
        float metaH = Math.Clamp(g.MeasureString("样本Ag", metaFont).Height, 14f, 18f);

        float cellTop = bar.Bottom + 3f;
        float cellW = innerW / _cells.Count;

        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i];
            float x = pad + cellW * i;
            float w = Math.Max(8f, cellW - (i == _cells.Count - 1 ? 0f : 8f));

            Gfx.FillRound(g, c.Color,
                new RectangleF(x, cellTop + (nameH - 7f) / 2f, 7f, 7f), 2f);

            float nameX = x + 11f;
            float nameW = Math.Max(0f, w - 11f);
            g.DrawString(c.Name, nameFont, strong,
                new RectangleF(nameX, cellTop, nameW, nameH), near);

            var share = total > 0 ? c.Hours / total : 0;
            var pct = c.Samples > 0 && total > 0 ? (share * 100).ToString("0.#") + "%" : TodayCardData.Dash;
            g.DrawString(pct, nameFont, dim, new RectangleF(nameX, cellTop, nameW, nameH), far);

            // 用 CSV 列名 n，既短又不产生第二套"样本"口径；760 宽下五格每格只有约 130px，
            // 分隔点两侧不留空格，优先保证时长与样本数都不被截断。
            var line2 = c.Samples > 0
                ? $"{TodaySummaryData.HoursText(c.Hours)}·n={SampleText(c.Samples)}"
                : TodayCardData.Dash;
            g.DrawString(line2, metaFont, dim,
                new RectangleF(x, cellTop + nameH + 1f, w, metaH), near);
        }
    }

    /// <summary>
    /// 样本数在五格里只给 4–5 个字符：全部窗口的 6 位数会把"时长"挤掉。
    /// ≥10 万取整到 k，1 万–10 万保留一位小数；精确值仍在 CSV 与概览卡里。
    /// </summary>
    private static string SampleText(long n)
    {
        if (n >= 100000) return n / 1000 + "k";
        if (n >= 10000) return (n / 1000.0).ToString("0.#") + "k";
        return n.ToString(CultureInfo.InvariantCulture);
    }
}
