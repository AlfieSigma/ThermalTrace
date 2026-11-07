namespace ThermalTrace.Export;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ThermalTrace.Analysis;

/// <summary>
/// 把 <see cref="EinkSnapshot"/> 渲染成极趣 Note 4 能直接显示的 400×300 黑白 PNG。
///
/// 【唯一版面】2026-10-17 起只剩一个版面（切角黑栏 + 切角外框 + 5 档 ΔT +
/// WLS 趋势）。旧的 A/B/C 三套候选与它们的数据模型（<c>EinkModel</c>）已整体删除——
/// 保留它们只会让人以为还有别的选型通道。
///
/// 【为什么必须自己抖动，不能交给云端】
/// 官方只说"上传 400x300 像素的黑白无灰阶图片（彩色会套用平台算法转黑白）"，
/// 云端怎么转、转成什么样都不可控。这里本地先做 Floyd–Steinberg 抖动降到 1bpp，
/// 好处是**本地预览看到的就真机上看到的东西**——不必等推送到设备才知道字糊不糊。
///
/// 【三个 GDI+ 的坑，改这个文件前必读】
///  1. <c>Graphics.FromImage</c> 不支持 1bpp 索引位图（直接抛异常）→ 必须先在 32bpp 上画。
///  2. 1bpp 位图不支持 <c>SetPixel</c> → 只能 LockBits 直接写字节。
///  3. <c>Bitmap.Save</c> 对 1bpp 索引位图不一定成功 → 已内置降级到 8bpp 灰度 PNG。
/// </summary>
public static class EinkRenderer
{
    /// <summary>极趣 Note 4 的物理分辨率，改这个值等于推送一张会被缩放的图。</summary>
    public const int Width = 400;

    public const int Height = 300;

    private const int Margin = 10;
    private const string FontName = "Microsoft YaHei UI";

    /// <summary>
    /// 1bpp 设备上的文字渲染方式。
    ///
    /// 【为什么不是 AntiAliasGridFit】抗锯齿会在笔画边缘留下灰度像素，随后交给
    /// Floyd–Steinberg 抖动。11–14px 的细笔画只有约 1 像素宽，灰度边缘被抖动打散成
    /// 孤立黑白点，真机上表现为"字边毛糙、笔画断裂"。实测同一串 <c>2·103h</c>：
    /// 抗锯齿 220 个灰度像素，本模式 0 个。
    ///
    /// 【为什么不用 ClearType】子像素渲染带彩色边缘，转灰度后同样制造灰阶。
    /// </summary>
    private const TextRenderingHint PixelText = TextRenderingHint.SingleBitPerPixelGridFit;

    /// <summary>
    /// 数据不足时的模板图：固定文案 + 已积累天数。
    ///
    /// 【为什么按天数缓存而不是每次重画】明确"不用反复重画"——
    /// 调用方按 <c>nodata-{N}.png</c> 存文件，天数没变就直接复用，一次都不用重画。
    /// </summary>
    public static byte[] RenderNoDataPng(int days)
    {
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = PixelText;

            using var frame = new Pen(Color.Black, 1f);
            g.DrawRectangle(frame, 20, 20, Width - 40, Height - 40);

            using var brandFont = new Font(FontName, 15f, FontStyle.Bold);
            using var mainFont = new Font(FontName, 14f, FontStyle.Regular);
            using var subFont = new Font(FontName, 12f, FontStyle.Regular);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            var box = new RectangleF(20, 90, Width - 40, 34);
            g.DrawString("ThermalTrace", brandFont, Brushes.Black, box, sf);

            box = new RectangleF(20, 132, Width - 40, 30);
            g.DrawString("数据积累中", mainFont, Brushes.Black, box, sf);

            box = new RectangleF(20, 168, Width - 40, 28);
            // 门槛天数必须引用常量——硬编码"3"会与 MinDaysToDraw 将来的调整打脸
            g.DrawString(
                $"已 {Math.Max(0, days)} 天 / 满 {EinkSnapshotBuilder.MinDaysToDraw} 天出趋势",
                subFont, Brushes.Black, box, sf);
        }

        return EncodePng(bmp, null);
    }

    // ==================================================================
    // 趋势版面（2026-10-17 真机确认）
    //
    // 与第 1 稿的差异（都是 2026-10-17 设计讨论的结论）：
    //   切角黑栏（左「ThermalTrace 30」右「DAY n」，Bahnschrift）取代整宽反白上栏；
    //   下反白栏删除——时间戳移到图框外右下（Consolas 15px），它是全图最重要的信息；
    //   新增切角外框；5 条线**全部等宽 2px**（取消 1/1.5/2/2.5/3 的线宽阶梯）；
    //   **孤点照画**（第 1 稿 run.Count>=2 才画，孤点被整段丢掉）；
    //   趋势降为 1px 虚线（DashPattern 4,3）+ 右端 9×9 实心方块；
    //   右缘标签改「序号·时长h」（如 1·162h）且**左对齐**；
    //   纵轴两端刻度从图区外左侧挪进图区内左上/左下（白底防压线）。
    //
    // 【Y 范围】当前按"覆盖全部数据"实现（含尖峰）。
    //   注意：需求基线里曾有"禁止 Y 轴截断"，该基线未经确认（2026-10-17）。
    //   该说法未经确认（2026-10-17 明确指出），不要把它当作不可改的约束。
    // ==================================================================

    /// <summary>渲染趋势版面。32bpp 绘制 → Floyd–Steinberg → 1bpp PNG。</summary>
    public static byte[] RenderV2(
        EinkSnapshot snapshot, Action<string>? warn = null, DateTime? renderedAt = null)
    {
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = PixelText;

            if (snapshot.Slot == EinkSlot.Slot2)
                DrawSlot2(g, snapshot, renderedAt);
            else
                DrawFiveLine(g, snapshot, renderedAt);
        }

        return EncodePng(bmp, warn);
    }

    // ---------- 趋势版面常量（改这里等于改图）----------
    private const string BrandFont = "Bahnschrift";
    private const string MonoFont = "Consolas";

    /// <summary>
    /// 建字体的唯一入口——**必须显式给 <see cref="GraphicsUnit.Pixel"/>**。
    ///
    /// 【坑】<c>new Font(name, 25f)</c> 的重载默认单位是 **Point**（1/72 英寸），
    /// 96 DPI 下 25f = 33 像素。而设计稿里的 25 是真 25 像素——差 33%，
    /// 表现为"字比设计稿大很多、顶到栏外"。
    /// </summary>
    private static Font Px(string name, float size) =>
        new(name, size, FontStyle.Regular, GraphicsUnit.Pixel);

    private const int Chamfer = 3;
    private const int FrameStroke = 2;
    private static readonly Rectangle BarBox = new(8, 4, 384, 34);          // 切角黑栏
    private static readonly Rectangle FrameBox = new(6, 44, 388, 228);      // 切角外框
    private static readonly RectangleF PlotArea = new(12, 50, 328, 216);    // 图区
    // 月份刻度画在外框底线（FrameBox.Bottom=272）上，不另开刻度条、也不进图区：
    // 图区底边 266 与框线之间正好有 6px，字跨在框线上，白底只挖掉这一段框线。

    // 右缘标签列起点（左对齐）。12px 下最坏串「5·9999h」墨迹宽 46px、起点相对
    // 绘制原点再右移 3px；TagX=342 时墨迹右缘 390，外框 2px 描边自 393 起，中间留 2px。
    private const float TagX = 342f;
    private const int DotSize = 4;          // 孤点边长
    private const float DataLineWidth = 2f; // 5 条 ΔT 线，等宽
    private const float TrendLineWidth = 2f;// 趋势线（1px 经 FS 抖动后看不清，实测要求 2px）
    private const int TrendBoxSize = 9;     // 趋势标签实心方块

    private static void DrawFiveLine(Graphics g, EinkSnapshot snap, DateTime? renderedAt = null)
    {
        if (!DrawFurniture(g, snap, snap.SpanDays.ToString(), renderedAt)) return;

        // ---------- 5 条 ΔT 线：等宽 2px；每个有效日都画孤点，相邻两日都有才连线 ----------
        // 孤点是数据不是噪点（满载一天只有几分钟是真实的使用情况）
        var scale = new PlotScale(snap);
        DrawSeries(g, snap, scale, drawDots: true);
        DrawTrend(g, snap, scale);
        DrawAxis(g, scale);
        DrawRightLabels(g, snap, scale);

        // ---------- 推送时间戳：图框外右下 ----------
        DrawTimeStamp(g, snap, renderedAt);
    }

    /// <summary>
    /// 两槽共用的静态版面：切角黑栏（跨度文字由调用方给）、切角外框、无数据分支。
    /// 返回 false 表示没有可绘制的数据，版面已画完。
    /// </summary>
    private static bool DrawFurniture(
        Graphics g, EinkSnapshot snap, string spanText, DateTime? renderedAt)
    {
        // ---------- 硬边：切角、折线、外框都不需要抗锯齿 ----------
        // RenderV2 已统一关掉 SmoothingMode；这里再显式设置一次，防止以后有人把外层
        // 改回 AntiAlias。45° 斜边带灰边只会被 FS 抖动撒成毛刺，不会变平滑。
        g.SmoothingMode = SmoothingMode.None;

        // ---------- 切角黑栏：左「ThermalTrace 30」，右「DAY n」----------
        FillChamfer(g, Brushes.Black, BarBox, Chamfer);
        var prevHint = g.TextRenderingHint;
        g.TextRenderingHint = PixelText;
        float barCy = BarBox.Top + BarBox.Height / 2f;
        using (var brandFont = Px(BrandFont, 25f))
        using (var smallFont = Px(BrandFont, 18f))
        {
            float x = BarBox.Left + 8;
            x += DrawStringVCenter(g, "ThermalTrace", brandFont, Brushes.White, x, barCy) + 5;
            DrawStringVCenter(g, spanText, smallFont, Brushes.White, x, barCy);

            if (snap.CleanDays is { } cd)
            {
                var dayText = $"DAY {cd}";
                var dw = Measure(g, dayText, smallFont);
                DrawStringVCenter(g, dayText, smallFont, Brushes.White,
                    BarBox.Right - 12 - dw, barCy);
            }
        }
        g.TextRenderingHint = prevHint;

        // ---------- 切角外框：2px 描边，不用"外填黑 + 内缩填白" ----------
        // 【为什么】切角矩形的内缩多边形在斜角处与外轮廓**不平行**，角部边框厚度只有
        // 1.41px，FS 抖动后断成虚线（实测会出现实线断开）。
        // 描边的厚度沿整条路径恒为 FrameStroke，没有这个问题。
        using (var framePen = new Pen(Color.Black, FrameStroke))
        using (var framePath = new GraphicsPath())
        {
            framePath.AddPolygon(ChamferPoints(FrameBox, Chamfer));
            g.DrawPath(framePen, framePath);
        }

        // ---------- 无数据 ----------
        if (!snap.HasData)
        {
            // Bahnschrift 没有中文字形，靠系统回落得到的字形不受控；中文一律显式指定雅黑。
            using var emptyFont = Px(FontName, 13f);
            var msg = "当前窗口内没有可绘制的数据";
            var mw = Measure(g, msg, emptyFont);
            g.DrawString(msg, emptyFont, Brushes.Black,
                (Width - mw) / 2, PlotArea.Top + (PlotArea.Height - 16) / 2);
            DrawTimeStamp(g, snap, renderedAt);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 图区 Y/X 映射。Y 范围用**实际极值**、不加 padding：
    /// 原实现上下各留 8%，实际最小 2.4℃ 被压到 0，轴标显示 "0.0"，
    /// 看起来像在宣称"0 是有效下限"（应使用实际值）。
    /// 只在跨度退化到画不出东西时才兜底——5 档分层的数据跨度远大于 3℃，正常不触发。
    /// </summary>
    private readonly struct PlotScale
    {
        private readonly double _lo;
        private readonly double _hi;
        private readonly int _count;

        public PlotScale(EinkSnapshot snap)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var line in snap.Lines)
                foreach (var p in line.Points)
                    if (p.DeltaT is { } v) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            if (snap.Trend is { } t0)
            {
                lo = Math.Min(lo, t0.From); hi = Math.Max(hi, t0.From);
                lo = Math.Min(lo, t0.To); hi = Math.Max(hi, t0.To);
            }

            if (hi - lo < 3.0)
            {
                var mid = (hi + lo) / 2;
                lo = mid - 1.5;
                hi = mid + 1.5;
            }

            _lo = lo;
            _hi = hi;
            _count = snap.Lines[0].Points.Count;
        }

        public double Hi => _hi;

        public double Lo => _lo;

        // 上下端点各留 1px 渲染余量——刻度标的是实际极值，只是不让线贴死边框。
        public float Y(double v) => (float)(PlotArea.Bottom - 1
            - (v - _lo) / (_hi - _lo) * (PlotArea.Height - 2));

        // X 轴用第一条线的日期序列（各线同窗口同日历，索引一致）
        public float X(int i) => _count <= 1
            ? PlotArea.Left
            : PlotArea.Left + (float)i / (_count - 1) * PlotArea.Width;
    }

    /// <summary>5 条负载线。槽 1 每个有效日都画孤点，槽 2 只连线。</summary>
    private static void DrawSeries(
        Graphics g, EinkSnapshot snap, PlotScale scale, bool drawDots)
    {
        for (int li = 0; li < snap.Lines.Count; li++)
        {
            var line = snap.Lines[li];
            using var pen = new Pen(Color.Black, DataLineWidth)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            var run = new List<PointF>();
            for (int i = 0; i < line.Points.Count; i++)
            {
                if (line.Points[i].DeltaT is not { } v)
                {
                    if (run.Count >= 2) g.DrawLines(pen, run.ToArray());
                    run.Clear();
                    continue;
                }

                var pt = new PointF(scale.X(i), scale.Y(v));
                if (drawDots)
                    g.FillRectangle(Brushes.Black,
                        pt.X - DotSize / 2f, pt.Y - DotSize / 2f, DotSize, DotSize);
                run.Add(pt);
            }
            if (run.Count >= 2) g.DrawLines(pen, run.ToArray());
        }
    }

    /// <summary>WLS 总体趋势：2px 虚线，跨全窗口（拟合非数据，允许跨缺失日）。</summary>
    private static void DrawTrend(Graphics g, EinkSnapshot snap, PlotScale scale)
    {
        if (snap.Trend is not { } tr) return;

        using var trendPen = new Pen(Color.Black, TrendLineWidth)
        {
            DashPattern = new[] { 4f, 3f }
        };
        g.DrawLine(trendPen, scale.X(0), scale.Y(tr.From),
            scale.X(snap.Lines[0].Points.Count - 1), scale.Y(tr.To));
    }

    /// <summary>纵轴：只标最低/最高，无单位、无网格；区内左上/左下 + 白底防压线。</summary>
    private static void DrawAxis(Graphics g, PlotScale scale)
    {
        // 12px：与右缘标签同一尺度，墨迹高度 8px（119 PPI 下约 1.7mm）。
        using var axisFont = Px(MonoFont, 12f);
        DrawAxisEnd(g, scale.Hi.ToString("0.0"), axisFont, PlotArea.Left + 1, PlotArea.Top + 1, true);
        DrawAxisEnd(g, scale.Lo.ToString("0.0"), axisFont, PlotArea.Left + 1, PlotArea.Bottom - 1, false);
    }

    /// <summary>右缘标签：「序号·时长h」左对齐 + 趋势实心方块。</summary>
    private static void DrawRightLabels(Graphics g, EinkSnapshot snap, PlotScale scale)
    {
        // 固定 12px：槽 2 数据积累多时会出现四位小时数（如 5·9999h）。
        // 最坏串墨迹宽 46px、起点相对绘制原点再右移 3px，TagX=342 时右缘 390，
        // 距离外框 2px 描边（393 起）还有 2px；14px 时同串会压到框外（2026-11-7 修正）。
        var labels = new List<(float Y, string? Text)>();
        for (int li = 0; li < snap.Lines.Count; li++)
        {
            var pts = snap.Lines[li].Points;
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                if (pts[i].DeltaT is { } v)
                {
                    var hours = (int)Math.Round(snap.Lines[li].TotalHours);
                    labels.Add((scale.Y(v), $"{li + 1}·{hours}h"));
                    break;
                }
            }
        }
        if (snap.Trend is { } t1) labels.Add((scale.Y(t1.To), null));   // null = 趋势方块

        labels.Sort((a, b) => a.Y.CompareTo(b.Y));

        // 顺序下推保证相邻标签不重叠；整体超出底部时一起上移
        const float lineH = 16f;
        for (int i = 1; i < labels.Count; i++)
            if (labels[i].Y < labels[i - 1].Y + lineH)
                labels[i] = (labels[i - 1].Y + lineH, labels[i].Text);
        float overflow = labels.Count > 0 ? labels[^1].Y + lineH - PlotArea.Bottom : 0;
        if (overflow > 0)
            for (int i = 0; i < labels.Count; i++)
                labels[i] = (labels[i].Y - overflow, labels[i].Text);

        using var labelFont = Px(MonoFont, 12f);
        foreach (var (y, text) in labels)
        {
            if (text is null)
            {
                g.FillRectangle(Brushes.Black, TagX, y - TrendBoxSize / 2f,
                    TrendBoxSize, TrendBoxSize);
            }
            else
            {
                DrawStringVCenter(g, text, labelFont, Brushes.Black, TagX, y);
            }
        }
    }

    // ==================================================================
    // 槽 2 · 自清灰以来平滑版面（v1.2）
    //
    // 与趋势版面的差异只在数据层：不叠原始日点、不做等宽桶，5 条线按日历日连续排布，
    // 画居中加权平滑值；另固定画一条池化加权预测线（虚线 + 右端 9×9 实心方块）。
    // 黑栏、外框、时间戳、纵轴、右缘标签沿用趋势版面常量，槽 1 输出不变。
    // ==================================================================
    private static void DrawSlot2(Graphics g, EinkSnapshot snap, DateTime? renderedAt = null)
    {
        // 槽 2 的跨度数以清灰基准日呈现（"since yyyy-MM-dd"），不写跨度天数；
        // 日期只取设置里的清灰日（CleanDate），不按首末数据日推算。
        var spanText = snap.CleanDate is { } cleanDate
            ? $"since {cleanDate:yyyy-MM-dd}"
            : snap.SpanDays.ToString();
        if (!DrawFurniture(g, snap, spanText, renderedAt)) return;

        var scale = new PlotScale(snap);

        // ---------- 5 条平滑线：只连线，不画原始点 ----------
        DrawSeries(g, snap, scale, drawDots: false);

        // ---------- 加权平均预测线：固定 2px 虚线，跨全窗口 ----------
        DrawTrend(g, snap, scale);

        DrawAxis(g, scale);

        DrawMonthLabels(g, snap.Lines[0].Points, PlotArea);

        // ---------- 右缘标签：5 条平滑线 + 预测线实心方块 ----------
        DrawRightLabels(g, snap, scale);

        DrawTimeStamp(g, snap, renderedAt);
    }

    /// <summary>
    /// 槽 2 月份刻度：窗口内每个自然月 1 日，把「N月」写在外框底线上（y=272）。
    ///
    /// 【为什么不进图区】2026-11-7 之前画在图区内部（y=240）且带白底，会切断最低一条
    /// ΔT 线，单独一个「11」浮在线间还会被误读成数据标签。
    /// 【为什么不另开刻度条】把图区压矮再往下排一行等于给一张图做两条基线。要求
    /// 刻度贴着坐标框本身：字跨在 <see cref="FrameBox"/> 的底线上，白底只挖掉这一段框线。
    /// 图区底边 266 与框线 272 之间留 6px，11px 雅黑「11月」墨迹高 10px，上缘约 267，仍在图区之外。
    /// </summary>
    private static void DrawMonthLabels(
        Graphics g, IReadOnlyList<EinkWeightedPoint> axis, RectangleF plot)
    {
        if (axis.Count == 0) return;

        var first = axis[0].Day;
        var last = axis[^1].Day;
        // 写「11月」而不是裸「11」：裸数字在低分辨率图里无法与数据标签区分。
        using var monthFont = Px(FontName, 11f);

        var month = new DateOnly(first.Year, first.Month, 1);
        while (month <= last)
        {
            if (month >= first)
            {
                int idx = month.DayNumber - first.DayNumber;
                float x = axis.Count <= 1
                    ? plot.Left
                    : plot.Left + (float)idx / (axis.Count - 1) * plot.Width;
                var text = $"{month.Month}月";
                var ink = MeasureInk(g, text, monthFont);
                float tx = Math.Clamp(x - ink.Width / 2f, plot.Left, plot.Right - ink.Width);

                // 只白掉框线那一条（2px 描边约 271–272），不碰图区、也不碰下方时间戳
                g.FillRectangle(Brushes.White, tx - 1, FrameBox.Bottom - 2, ink.Width + 2, 4);
                g.DrawString(text, monthFont, Brushes.Black,
                    tx, FrameBox.Bottom - ink.Height / 2f - ink.Top);
            }
            month = month.AddMonths(1);
        }
    }

    /// <summary>
    /// 推送时刻。这是判断"推送是否成功 / 数据代表何时"的唯一依据，
    /// 因此是全图最重要的信息——放在图框外右下，字号 15px（比黑栏里的字更大）。
    /// </summary>
    private static void DrawTimeStamp(Graphics g, EinkSnapshot snap, DateTime? renderedAt)
    {
        using var stampFont = Px(MonoFont, 15f);
        var stamp = renderedAt is { } at
            ? $"{at:yyyy-MM-dd HH:mm}"
            : $"{snap.DataDay:yyyy-MM-dd}";
        var w = Measure(g, stamp, stampFont);
        g.DrawString(stamp, stampFont, Brushes.Black, Width - 10 - w, FrameBox.Bottom + 4);
    }

    /// <summary>
    /// 纵轴一端刻度：白底盖住线再写数。
    ///
    /// <paramref name="atTop"/> 为 true 时 <paramref name="edgeY"/> 是墨迹上缘，
    /// 否则是墨迹下缘——按墨迹而不是字号定位，换字号时白底与文字不会错位。
    /// </summary>
    private static void DrawAxisEnd(
        Graphics g, string s, Font f, float x, float edgeY, bool atTop)
    {
        var ink = MeasureInk(g, s, f);
        float top = atTop ? edgeY : edgeY - ink.Height;
        g.FillRectangle(Brushes.White, x - 1, top - 1, ink.Width + 3, ink.Height + 2);
        g.DrawString(s, f, Brushes.Black, x, top - ink.Top);
    }

    private static void FillChamfer(Graphics g, Brush br, Rectangle box, int c)
    {
        using var path = new GraphicsPath();
        path.AddPolygon(ChamferPoints(box, c));
        g.FillPath(br, path);
    }

    /// <summary>切角矩形八顶点（顺时针，自顶边左侧起）。</summary>
    private static PointF[] ChamferPoints(Rectangle box, int c)
    {
        float x0 = box.Left, y0 = box.Top, x1 = box.Right, y1 = box.Bottom;
        return new[]
        {
            new PointF(x0 + c, y0), new PointF(x1 - c, y0), new PointF(x1, y0 + c),
            new PointF(x1, y1 - c), new PointF(x1 - c, y1), new PointF(x0 + c, y1),
            new PointF(x0, y1 - c), new PointF(x0, y0 + c)
        };
    }

    /// <summary>
    /// 按**墨迹** bbox 垂直居中绘制（黑栏反白字专用），返回墨迹宽度便于串接。
    ///
    /// 【坑】不要用 <c>MeasureString</c> 的字符格高度估算——它含 ascent/descent，
    /// 在 34px 高的黑栏里会把字顶出栏外（早期设计稿第一版就是这么翻车的）。
    /// 必须用 <c>MeasureCharacterRanges</c> 拿真实墨迹。
    /// </summary>
    private static float DrawStringVCenter(
        Graphics g, string s, Font f, Brush br, float x, float centerY)
    {
        var ink = MeasureInk(g, s, f);
        g.DrawString(s, f, br, x, centerY - ink.Height / 2f - ink.Top);
        return ink.Width;
    }

    private static RectangleF MeasureInk(Graphics g, string s, Font f)
    {
        using var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        // 【坑】StringFormat 默认 LineAlignment=Center，于是 MeasureCharacterRanges 返回的
        // bounds.Top 是"整行在测量区里居中之后"的 y（400×300 画布上约 135），而不是墨迹
        // 相对字符格顶部的偏移。直接拿去算居中位置，字会被推到栏外（实测）。
        sf.LineAlignment = StringAlignment.Near;
        sf.SetMeasurableCharacterRanges(new[] { new CharacterRange(0, s.Length) });
        var ranges = g.MeasureCharacterRanges(s, f, new RectangleF(0, 0, Width, Height), sf);
        return ranges.Length > 0 ? ranges[0].GetBounds(g) : new RectangleF(0, 0, 0, 0);
    }

    private static float Measure(Graphics g, string text, Font font)
    {
        using var typo = new StringFormat(StringFormat.GenericTypographic);
        return g.MeasureString(text, font, int.MaxValue, typo).Width;
    }

    // ------------------------------------------------------------------
    // 编码：32bpp → 灰度 → Floyd–Steinberg → 1bpp PNG（失败降级 8bpp）
    // ------------------------------------------------------------------

    private static byte[] EncodePng(Bitmap src, Action<string>? warn)
    {
        int w = src.Width, h = src.Height;
        var gray = ToGray(src);

        var white = Dither(gray, w, h);

        try
        {
            using var mono = BuildMono(white, w, h);
            using var ms = new MemoryStream();
            mono.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            // GDI+ 对 1bpp 索引位图的 PNG 编码并不总是支持，这里不能让整个命令失败。
            // 8bpp 灰度交给云端 dither=true 二值化，内容一分不少，只是清晰度由云端决定。
            warn?.Invoke($"[warn] 1bpp PNG 编码失败，已降级为 8bpp 灰度：{ex.Message}");
            return EncodeGrayPng(gray, w, h);
        }
    }

    private static float[] ToGray(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var gray = new float[w * h];

        try
        {
            var stride = Math.Abs(data.Stride);
            var buf = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    // Format32bppArgb 的字节顺序是 B, G, R, A
                    gray[y * w + x] = 0.299f * buf[i + 2] + 0.587f * buf[i + 1] + 0.114f * buf[i];
                }
            }
        }
        finally
        {
            src.UnlockBits(data);
        }

        return gray;
    }

    /// <summary>
    /// Floyd–Steinberg 误差扩散。
    /// 直接按 128 硬阈值切会把文字的灰色边缘一刀切掉，笔画变细甚至断开；
    /// 抖动把量化误差摊到邻近像素，肉眼看到的是"灰度"，设备看到的是纯黑白——
    /// 这正是 1bpp 屏能显示可辨认中文的关键。
    /// </summary>
    private static bool[] Dither(float[] gray, int w, int h)
    {
        var work = (float[])gray.Clone();
        var white = new bool[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                var old = work[i];
                var neu = old < 128f ? 0f : 255f;
                white[i] = neu >= 128f;

                var err = old - neu;
                if (x + 1 < w) work[i + 1] += err * 7f / 16f;
                if (y + 1 < h)
                {
                    if (x > 0) work[i + w - 1] += err * 3f / 16f;
                    work[i + w] += err * 5f / 16f;
                    if (x + 1 < w) work[i + w + 1] += err * 1f / 16f;
                }
            }
        }

        return white;
    }

    private static Bitmap BuildMono(bool[] white, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format1bppIndexed);

        // 显式写调色板：0 = 黑、1 = 白。不写就得赌 GDI+ 的默认值。
        var pal = bmp.Palette;
        pal.Entries[0] = Color.Black;
        pal.Entries[1] = Color.White;
        bmp.Palette = pal;

        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        try
        {
            var stride = Math.Abs(data.Stride);
            var rowBuf = new byte[stride];

            for (int y = 0; y < h; y++)
            {
                Array.Clear(rowBuf, 0, stride);
                for (int x = 0; x < w; x++)
                {
                    if (white[y * w + x])
                        rowBuf[x >> 3] |= (byte)(0x80 >> (x & 7));
                }
                System.Runtime.InteropServices.Marshal.Copy(
                    rowBuf, 0, data.Scan0 + y * stride, stride);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }

    private static byte[] EncodeGrayPng(float[] gray, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed);

        var pal = bmp.Palette;
        for (int i = 0; i < 256; i++)
            pal.Entries[i] = Color.FromArgb(i, i, i);
        bmp.Palette = pal;

        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
        try
        {
            var stride = Math.Abs(data.Stride);
            var rowBuf = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                Array.Clear(rowBuf, 0, stride);
                for (int x = 0; x < w; x++)
                {
                    var v = gray[y * w + x];
                    rowBuf[x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
                System.Runtime.InteropServices.Marshal.Copy(
                    rowBuf, 0, data.Scan0 + y * stride, stride);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
