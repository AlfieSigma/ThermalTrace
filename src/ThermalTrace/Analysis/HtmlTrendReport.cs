using System.Globalization;
using System.Text;
using ThermalTrace.Model;

namespace ThermalTrace.Analysis;

/// <summary>
/// 自包含 HTML 趋势报告：全部样式内联、图表用内联 SVG 手绘，
/// 不依赖任何外部 JS/CSS/CDN，断网可开，单文件可转发。
/// </summary>
public static class HtmlTrendReport
{
    public static string Render(TrendReport r, AppSettings settings, HardwareTrendReport? hardware = null)
    {
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ThermalTrace 散热趋势报告</title>
<style>
  :root { --ink:#23272e; --sub:#6b7280; --line:#e3e6ea; --bg:#f5f6f8; --card:#fff;
""" + "\n");
        // 语义色取自 VisualTones：报告与 GUI 各用自己的一列，互不覆盖。
        sb.Append("          --accent:").Append(VisualTones.Accent.Report)
            .Append("; --hot:").Append(VisualTones.Hot.Report)
            .Append("; --warn-bg:#fdf3e2; --warn-bd:#e3b878; }\n");
        sb.Append("""
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--ink);
         font-family:"Microsoft YaHei UI","Microsoft YaHei",sans-serif; font-size:14px; }
  .wrap { max-width:980px; margin:0 auto; padding:24px 20px 60px; }
  h1 { font-size:22px; margin:0 0 4px; }
  .meta { color:var(--sub); font-size:13px; margin-bottom:18px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:10px;
          padding:18px 20px; margin-bottom:16px; }
  .card h2 { font-size:15px; margin:0 0 12px; }
  .headline { font-size:16px; line-height:1.7; }
  .headline b.hot { color:var(--hot); } .headline b.cool { color:var(--accent); }
  /* 健康度徽标与分层解读（与趋势页卡片同一份 InterpretationReport） */
  .lv { display:inline-block; padding:1px 10px; border-radius:10px; font-size:12px;
        color:#fff; margin-right:8px; vertical-align:2px; }
  .lv-good, .lv-stable { background:var(--accent); }
""" + "\n");
        sb.Append("  .lv-watch { background:").Append(VisualTones.ReportWatchBadge).Append("; }\n");
        sb.Append("  .lv-warning { background:var(--hot); }\n");
        sb.Append("  .lv-insufficient { background:").Append(VisualTones.ReportNeutral).Append("; }\n");
        sb.Append("""
  .layers { margin-top:10px; }
  .layers h3 { font-size:13px; margin:10px 0 4px; color:var(--sub); }
  .layers ul, .layers ol { margin:0 0 2px; padding-left:22px; }
  .layers li { margin:3px 0; line-height:1.6; }
  .layers .dim { color:var(--sub); font-size:12.5px; }
  .warn { background:var(--warn-bg); border:1px solid var(--warn-bd); border-radius:10px;
          padding:12px 16px; margin-bottom:16px; font-size:13px; line-height:1.6; }
  .legend { display:flex; flex-wrap:wrap; gap:14px; margin:4px 0 8px; font-size:12px; color:var(--sub); }
  .legend span { display:inline-flex; align-items:center; gap:6px; }
  .legend i { width:14px; height:3px; border-radius:2px; display:inline-block; }
  table { width:100%; border-collapse:collapse; font-size:13px; }
  th, td { padding:8px 10px; text-align:right; border-bottom:1px solid var(--line); }
  th:first-child, td:first-child { text-align:left; }
  th { color:var(--sub); font-weight:600; }
  tr.hot td { color:var(--hot); }
  .note { color:var(--sub); font-size:12.5px; line-height:1.7; }
  .muted { color:var(--sub); }
  .foot { margin-top:8px; }
  /* AI 摘要：折叠卡片 + 等宽预格式（保留 markdown 原样） */
  details.digest > summary { cursor:pointer; font-size:15px; font-weight:600; color:var(--ink); }
  details.digest > summary:hover { color:var(--accent); }
  details.digest pre { margin:12px 0 0; padding:12px 14px; background:var(--bg);
        border:1px solid var(--line); border-radius:8px; font-size:12.5px; line-height:1.7;
        white-space:pre-wrap; word-break:break-word; font-family:Consolas,"Cascadia Mono",monospace; }
  /* 非 CPU 信号章节的数值小表 */
  .kv { width:100%; border-collapse:collapse; font-size:13px; margin-top:8px; }
  .kv th, .kv td { padding:6px 10px; text-align:left; border-bottom:1px solid var(--line); }
  .kv th { color:var(--sub); font-weight:600; white-space:nowrap; }
  .kv td { font-variant-numeric:tabular-nums; }
</style></head><body><div class="wrap">
""");
        sb.AppendLine();

        sb.AppendLine("<h1>ThermalTrace 散热趋势报告</h1>");
        sb.AppendLine($"<div class=\"meta\">生成时间 {r.GeneratedAt:yyyy-MM-dd HH:mm}　"
            + $"数据范围 {(r.FirstDay is null ? "无" : $"{r.FirstDay:yyyy-MM-dd} ~ {r.LastDay:yyyy-MM-dd}")}"
            + $"　有效天数 {r.DaysWithData} 天　清灰基准日 {settings.CleanDateIso}</div>");

        // 口径警告
        foreach (var w in r.CaliberWarnings)
            sb.AppendLine($"<div class=\"warn\">⚠ {Esc(w)}</div>");

        // 结论（分层解读：总结 → 证据 → 归因 → 建议 → 清灰基准，与 GUI 卡片 / --interpret 同源同值）
        // 引擎在这里只跑一次，结论区与文末 AI 摘要共用同一份 InterpretationReport
        var interp = Interpreter.Build(settings, r, TrendWindow.Month, hardware);
        sb.AppendLine("<div class=\"card\"><h2>结论</h2>");
        sb.AppendLine(BuildInterpretation(interp));
        sb.AppendLine("</div>");

        // 图表
        var cats = ActiveCategories(r);
        sb.AppendLine($"<div class=\"card\"><h2>ΔT 归一化温升（℃，同等负载下的相对发热，{(r.OnAc ? "交流" : "电池")}供电）</h2>");
        sb.AppendLine(Legend(cats, settings));
        sb.AppendLine(LineChart(r.Days, cats, s => s.DeltaT, "℃", false, r.OnAc));
        sb.AppendLine("<div class=\"note\">ΔT = 该负载类别均温 − 当日空闲档最低温，消除夏冬室温差异后跨期可比。");
        var approxDays = r.Days.Where(d => d.IdleApproximated).ToList();
        if (approxDays.Count > 0)
            sb.AppendLine("<br>⚠ 以下日期无 0-10 空闲档数据，基线用当日最低档（"
                + string.Join("、", approxDays.Select(d => $"{d.Date:MM-dd} 用 {d.IdleBaselineBand}")) + "）近似，ΔT 口径略宽。");
        sb.AppendLine("</div></div>");

        sb.AppendLine("<div class=\"card\"><h2>每日超 85℃ 时长（小时）</h2>");
        sb.AppendLine(Legend(cats, settings));
        sb.AppendLine(LineChart(r.Days, cats, s => s.HoursOver85, "h", true, r.OnAc));
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"card\"><h2>每瓦温升（K/W，散热效率物理指标）</h2>");
        sb.AppendLine(Legend(cats, settings));
        sb.AppendLine(LineChart(r.Days, cats, s => s.KPerW, "K/W", false, r.OnAc));
        sb.AppendLine("<div class=\"note\">数值越大表示同样功耗下温度越高，散热效率越差。</div></div>");

        // 非 CPU 信号（GPU 温度 / SSD 寿命）——3.5。数字直接读 HardwareTrend，
        // 与 GUI「硬件」视图、今日页两行同一个取数入口（G4）。
        sb.AppendLine(HardwareSection(hardware));

        // 对比表
        ComparisonTable(sb, "近 7 天 vs 前 7 天", r.Week);
        ComparisonTable(sb, "近 30 天 vs 前 30 天", r.Month);

        // 方法说明
        sb.AppendLine("""
<div class="card"><h2>方法与口径说明</h2><div class="note">
        1. 数据全部来自真实使用时的后台采样，不做合成负载测试；合盖休眠/关机时段自然缺数据，不补造。<br>
        2. 负载按"单核峰值利用率"划档（总利用率会低估单核算满的发热），存储层为 10% 细粒度，展示层按设置里的类别边界合并；调整边界后历史数据立即重算。<br>
        3. 跨日趋势以 ΔT 与每瓦温升为准；绝对温度受季节影响，不直接用于跨月对比。<br>
        4. 环境气温来自 Open-Meteo（约数），缺失时用空闲档最低温代理。<br>
        5. 样本数过少（低置信度）的时间片已按权重自然稀释；对比结论需两窗口都有足够样本才可信。
        </div></div>
""");

        // AI 摘要：GUI 卡片与 --interpret 都有这份导出，
        // HTML 报告此前没有——而报告恰恰是最常被整份转发的形态，缺了它就得回 GUI 再抄一遍。
        // 折叠呈现：默认收起不打断阅读流，要整段复制时展开全选即可。
        sb.AppendLine("<details class=\"card digest\"><summary>AI 摘要（可整段复制，贴给外部模型解读）</summary>");
        sb.AppendLine("<pre>" + Esc(interp.AiDigest) + "</pre></details>");

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // 结论区：分层解读
    // ------------------------------------------------------------------

    /// <summary>
    /// 结论区整体改走 <see cref="Interpreter"/>：总结 → 证据 → 归因 → 建议 → 清灰基准。
    ///
    /// 【为什么换掉原来的 BuildHeadline】原来这里自己拼 3~5 行结论，用的是
    /// <c>r.Week</c>（近 7 天 vs 前 7 天）自己算的阈值。于是同一个"重度负载"在
    /// GUI 页脚、解读卡片、HTML 报告里可能出现三个不同说法——结论只能有一个引擎
    /// 现在本方法只做排版，一个数都不自己算，全取自调用方传进来的
    /// <see cref="InterpretationReport"/>。
    ///
    /// 【window 写进页面】报告恒按 30 天窗口解读，而 GUI 解读卡片跟随趋势页选择的范围
    /// （默认近 7 天）——不把窗口标在页面上，同一个人拿卡片和报告对数会以为数字对不上
    /// （2026-10-14 修复：与卡片同源同值的实证缺口）。
    /// </summary>
    private static string BuildInterpretation(InterpretationReport it)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"<div class=\"note\">解读窗口：{Esc(TrendWindow.Month.Label)}"
            + "（GUI 解读卡片跟随趋势页选择的范围，此处固定为报告口径）</div>");
        sb.AppendLine($"<div class=\"headline\"><span class=\"lv lv-{LevelSlug(it.Health)}\">"
            + Esc(it.Health.Text()) + "</span>" + Esc(it.Summary) + "</div>");

        sb.AppendLine("<div class=\"layers\">");

        if (it.Evidence.Count > 0)
        {
            sb.AppendLine("<h3>证据</h3><ul>");
            foreach (var e in it.Evidence) sb.AppendLine($"<li>{Esc(e.Text)}</li>");
            sb.AppendLine("</ul>");
        }

        if (it.Attributions.Count > 0)
        {
            sb.AppendLine("<h3>可能原因（含验证方法）</h3><ul>");
            foreach (var a in it.Attributions)
            {
                var line = Esc(a.Hypothesis);
                if (a.Confidence.Length > 0) line += $"（置信度：{Esc(a.Confidence)}）";
                if (a.Support.Length > 0)
                    line += $"<br><span class=\"dim\">依据：{Esc(a.Support)}</span>";
                if (a.Verification is { Length: > 0 } v)
                    line += $"<br><span class=\"dim\">验证：{Esc(v)}</span>";
                sb.AppendLine($"<li>{line}</li>");
            }
            sb.AppendLine("</ul>");
        }

        if (it.Suggestions.Count > 0)
        {
            sb.AppendLine("<h3>建议</h3><ol>");
            foreach (var s in it.Suggestions) sb.AppendLine($"<li>{Esc(s)}</li>");
            sb.AppendLine("</ol>");
        }

        if (it.Clean is { } clean)
            sb.AppendLine($"<h3>清灰基准</h3><div class=\"note\">{Esc(clean.Text)}</div>");

        sb.AppendLine("</div>");
        sb.AppendLine($"<div class=\"note\">数据可信度：{Esc(it.ConfidenceNote)}</div>");
        return sb.ToString();
    }

    /// <summary>健康度 → CSS 类名。颜色语义与趋势页卡片一致。</summary>
    private static string LevelSlug(HealthLevel level) => level switch
    {
        HealthLevel.Good => "good",
        HealthLevel.Stable => "stable",
        HealthLevel.Watch => "watch",
        HealthLevel.Warning => "warning",
        _ => "insufficient"
    };

    // ------------------------------------------------------------------
    // 非 CPU 信号章节（GPU / SSD）——3.5
    // ------------------------------------------------------------------

    /// <summary>一条硬件折线（左轴或右轴，量纲由 <see cref="Unit"/> 标注）。</summary>
    private sealed record HwSeries(
        string Name, string Color, IReadOnlyList<double?> Values, string Unit, bool RightAxis, bool ZeroBased);

    /// <summary>
    /// GPU / SSD 章节。数字全部取自 <see cref="HardwareTrendReport"/>
    /// （与 GUI「硬件」视图、今日页两行同一个取数入口，G4），本方法只做排版。
    /// </summary>
    private static string HardwareSection(HardwareTrendReport? hw)
    {
        if (hw is null || !hw.HasAnyData)
        {
            return "<div class=\"card\"><h2>GPU 与 SSD（非 CPU 信号）</h2><div class=\"note\">"
                + "暂无数据。GPU 温度与 SSD 寿命计数器自 2026-10-13 起才开始记录，"
                + "程序连续运行几天后这里会出现曲线。</div></div>";
        }

        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"card\"><h2>GPU 与 SSD（非 CPU 信号）</h2>");

        var axis = hw.Axis;
        static double? V(double v) => double.IsNaN(v) ? null : v;
        // 忙时/闲时均温在旧口径数据里为 NaN —— 渲染「—」，绝不填 0℃（R5）
        // （不与下方 SSD 段落里的局部函数 S 重名：嵌套作用域同名会 CS0136）
        static string T(double v) => double.IsNaN(v) ? "—" : v.ToString("0.0", CultureInfo.InvariantCulture);

        // ---- GPU：均温（左轴，绝对温度不纳 0 基线）+ 使用时长（右轴）----
        var gpuTemp = axis.Select(d => V(hw.GpuTempOn(d))).ToList();
        var gpuHours = axis.Select(d => V(hw.GpuHoursOn(d))).ToList();
        if (gpuTemp.Any(v => v is not null) || gpuHours.Any(v => v is not null))
        {
            sb.AppendLine(HardwareChart(axis,
                new HwSeries("GPU 均温", VisualTones.Warm.Report, gpuTemp, "℃", false, false),
                new HwSeries("GPU 使用", VisualTones.Accent.Report, gpuHours, "h", true, true)));
            sb.AppendLine("<div class=\"note\">均温按样本数加权；没用显卡的日子线断开。"
                + "使用时长 = GPU 负载 &gt; 0 的累计时长（2026-10-16 起；此前口径为「采到温度即计入」，"
                + "在独显待机也报温度的机器上等于开机时长，跨该日期的时长对比无意义）。"
                + "GPU 与 CPU 共用散热模组——ΔT 上升时要同时看这根线，排除「更常用显卡」的干扰。</div>");
        }

        // ---- SSD：累计写入（左轴，累计量不纳 0 基线）+ 剩余寿命（右轴）----
        var write = axis.Select(d => hw.SsdOn(d) is { } r && !double.IsNaN(r.DataWrittenGb) ? r.DataWrittenGb : (double?)null).ToList();
        var life = axis.Select(d => hw.SsdOn(d) is { } r2 && !double.IsNaN(r2.Life) ? r2.Life : (double?)null).ToList();
        if (write.Any(v => v is not null) || life.Any(v => v is not null))
        {
            sb.AppendLine(HardwareChart(axis,
                new HwSeries("累计写入", VisualTones.ReportSeriesBlue, write, " GB", false, false),
                new HwSeries("剩余寿命", VisualTones.Hot.Report, life, "%", true, false)));
            sb.AppendLine("<div class=\"note\">累计写入与剩余寿命各走左右轴。寿命 % 是整数粒度，连日不动属正常；"
                + "程序没跑的日子没有行，线照实断开。</div>");
        }

        // ---- 窗口汇总 ----
        sb.AppendLine("<table class=\"kv\">");
        var gpuCell = hw.GpuDays.Count == 0
            ? "本期无数据"
            : hw.GpuLoadDaysCount == 0
                ? "本期无负载数据（2026-10-16 前为旧口径，时长不可比）"
                : $"本期 {hw.GpuHours:0.0}h（负载 &gt; 0）· 忙时 {T(hw.GpuActiveTempMean)}℃ · 闲时 {T(hw.GpuIdleTempMean)}℃"
                  + (hw.GpuDaysPrevCount > 0 ? $"（上期 {hw.GpuHoursPrev:0.0}h，{hw.GpuDays.Count} 天有数据）" : "");
        sb.AppendLine($"<tr><th>GPU</th><td>{Esc(gpuCell)}</td></tr>");

        var ssd = hw.Ssd;
        if (ssd.Latest is { } l)
        {
            string S(double v, string fmt) => double.IsNaN(v) ? "—" : v.ToString(fmt, CultureInfo.InvariantCulture);
            var latest = $"最新记录 {l.Date:yyyy-MM-dd}：寿命 {S(l.Life, "0")}% · 已用 {S(l.PercentageUsed, "0")}%"
                + $" · 累计写入 {S(l.DataWrittenGb, "0")} GB · 通电 {S(l.PowerOnHours, "0")} h";
            sb.AppendLine($"<tr><th>SSD</th><td>{Esc(latest)}</td></tr>");

            var rate = ssd.HasTrend
                ? $"日均写入 {ssd.DataWrittenPerDay:0.#} GB · 预计还有 {(long)ssd.ProjectedLifeDays} 天到寿命耗尽"
                : $"窗口内 {ssd.RowCount} 行，还差 {Math.Max(0, SsdWindowStat.MinRowsForTrend - ssd.RowCount)} 天"
                  + $"（≥{SsdWindowStat.MinRowsForTrend} 天才给写入速率与寿命预估）";
            if (ssd.LifeFlat) rate += " · 寿命 % 连日不动：计数器粒度粗，属正常";
            sb.AppendLine($"<tr><th></th><td>{Esc(rate)}</td></tr>");
        }
        else
        {
            sb.AppendLine("<tr><th>SSD</th><td>暂无记录</td></tr>");
        }
        sb.AppendLine("</table>");

        sb.AppendLine("</div>");
        return sb.ToString();
    }

    /// <summary>
    /// 硬件章节的双轴 SVG 折线图：X = 日期，两条线各走一根 Y 轴。
    /// 【为什么不用上面那张 LineChart】那张按 CPU 类别组织（cats × days × pick），
    /// 硬件信号没有负载类别，且两根线量纲不同必须分轴——挤一根轴上大量纲的
    /// 会把小量纲的压成贴地直线。
    /// </summary>
    private static string HardwareChart(IReadOnlyList<DateOnly> axis, HwSeries left, HwSeries? right)
    {
        const int W = 920, H = 220, PL = 56, PR = 56, PT = 14, PB = 38;
        int plotW = W - PL - PR, plotH = H - PT - PB;

        var series = right is null ? new[] { left } : new[] { left, right };

        (double Lo, double Hi) Range(HwSeries s)
        {
            var vs = s.Values.Where(v => v is not null).Select(v => v!.Value).ToList();
            if (vs.Count == 0) return (0, 1);
            double lo = s.ZeroBased ? Math.Min(0, vs.Min()) : vs.Min();
            double hi = s.ZeroBased ? Math.Max(0, vs.Max()) : vs.Max();
            double span = hi - lo;
            if (span < 1e-6) span = Math.Max(1.0, Math.Abs(hi) * 0.2);
            return (lo - span * 0.08, hi + span * 0.08);
        }

        var (loL, hiL) = Range(left);
        var (loR, hiR) = right is null ? (loL, hiL) : Range(right);

        double X(int i) => axis.Count == 1 ? PL + plotW / 2.0 : PL + (double)i * plotW / (axis.Count - 1);
        double Y(double v, bool onRight) =>
            PT + plotH - (v - (onRight ? loR : loL)) / ((onRight ? hiR : hiL) - (onRight ? loR : loL)) * plotH;

        var svg = BeginChart(W, H);

        // 网格 + 左右轴刻度（各 4 档）
        for (int side = 0; side < 2; side++)
        {
            bool onRight = side == 1;
            if (onRight && right is null) continue;
            var (lo, hi) = onRight ? (loR, hiR) : (loL, hiL);
            for (int g = 0; g <= 4; g++)
            {
                var yv = lo + (hi - lo) * g / 4;
                AppendGridLine(svg, PL, W - PR, Y(yv, onRight),
                    onRight ? W - PR + 8 : PL - 8, onRight ? "start" : "end", yv);
            }
        }

        // X 轴日期：最多 8 个
        AppendXLabels(svg, axis.Count, H, X, i => axis[i]);

        // 图例（含单位）
        double lx = PL;
        foreach (var s in series)
        {
            svg.AppendLine($"<rect x=\"{lx:0}\" y=\"{PT + 2}\" width=\"14\" height=\"3\" rx=\"1.5\" fill=\"{s.Color}\"/>");
            svg.AppendLine($"<text x=\"{lx + 20:0}\" y=\"{PT + 7}\" font-size=\"11\" fill=\"#6b7280\">"
                + $"{Esc(s.Name + s.Unit)}</text>");
            lx += 20 + (s.Name.Length + s.Unit.Length) * 7 + 22;
        }

        // 各系列折线（NaN = 没数据，断开，不伪造连线）
        foreach (var s in series)
            AppendSeries(svg, axis.Count, i => s.Values[i], X, v => Y(v, s.RightAxis), s.Color);

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    // ------------------------------------------------------------------
    // 对比表
    // ------------------------------------------------------------------

    private static void ComparisonTable(StringBuilder sb, string title, List<CategoryComparison> rows)
    {
        sb.AppendLine($"<div class=\"card\"><h2>{title}</h2>");
        if (rows.Count == 0)
        {
            sb.AppendLine("<div class=\"note\">暂无数据。</div></div>");
            return;
        }

        sb.AppendLine("<table><thead><tr><th>负载类别</th><th>ΔT 本期</th><th>ΔT 上期</th><th>温差</th>"
            + "<th>超85℃ 本期</th><th>超85℃ 上期</th><th>K/W 本期</th><th>K/W 上期</th>"
            + "<th>样本 本期</th><th>样本 上期</th></tr></thead><tbody>");

        foreach (var c in rows)
        {
            var hot = c.Comparable && c.DeltaTDiff is >= TrendThresholds.DeltaTSignificant;
            sb.AppendLine($"<tr class=\"{(hot ? "hot" : "")}\"><td>{c.Category}</td>"
                + $"<td>{F(c.CurDeltaT, "0.0")}</td><td>{F(c.PrevDeltaT, "0.0")}</td>"
                + $"<td>{Diff(c.DeltaTDiff, "0.0")}</td>"
                + $"<td>{F(c.CurOver85Hours, "0.00")}h</td><td>{F(c.PrevOver85Hours, "0.00")}h</td>"
                + $"<td>{F(c.CurKPerW, "0.00")}</td><td>{F(c.PrevKPerW, "0.00")}</td>"
                + $"<td>{(c.CurSamples?.ToString() ?? "—")}</td><td>{(c.PrevSamples?.ToString() ?? "—")}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
        if (!rows.Exists(c => c.Comparable))
            sb.AppendLine("<div class=\"note\">两窗口未同时覆盖同一类别，尚无法对比。</div>");
        sb.AppendLine("</div>");
    }

    private static string F(double? v, string fmt) =>
        v is null ? "—" : v.Value.ToString(fmt, CultureInfo.InvariantCulture);

    private static string Diff(double? v, string fmt)
    {
        if (v is null) return "—";
        var s = v.Value.ToString(fmt, CultureInfo.InvariantCulture);
        return v.Value > 0 ? "+" + s : s;
    }

    // ------------------------------------------------------------------
    // SVG 图表公共件：折线图（按负载类别）与硬件图（双轴）共用网格、X 轴与折线路径。
    // 两图只保留各自的量程、轴数与配色，排版逻辑不再各写一套。
    // ------------------------------------------------------------------

    private static StringBuilder BeginChart(int w, int h) =>
        new StringBuilder().AppendLine($"<svg viewBox=\"0 0 {w} {h}\" width=\"100%\" role=\"img\">");

    /// <summary>一条水平网格线 + 轴刻度文字。左右轴只差文字位置与对齐方式。</summary>
    private static void AppendGridLine(
        StringBuilder svg, int x1, int x2, double y, int labelX, string anchor, double value)
    {
        var ys = y.ToString("0.0", CultureInfo.InvariantCulture);
        svg.AppendLine($"<line x1=\"{x1}\" y1=\"{ys}\" x2=\"{x2}\" y2=\"{ys}\" stroke=\"#eef0f3\"/>");
        svg.AppendLine($"<text x=\"{labelX}\" y=\"{ys}\" dy=\"4\" text-anchor=\"{anchor}\" font-size=\"11\" fill=\"#9aa1ab\">"
            + $"{value.ToString("0.##", CultureInfo.InvariantCulture)}</text>");
    }

    /// <summary>X 轴日期标签：最多 8 个。</summary>
    private static void AppendXLabels(
        StringBuilder svg, int count, int h, Func<int, double> x, Func<int, DateOnly> day)
    {
        int step = Math.Max(1, (int)Math.Ceiling(count / 8.0));
        for (int i = 0; i < count; i += step)
            svg.AppendLine($"<text x=\"{x(i):0}\" y=\"{h - 12}\" text-anchor=\"middle\" font-size=\"11\" fill=\"#9aa1ab\">"
                + $"{day(i).ToString("MM-dd", CultureInfo.InvariantCulture)}</text>");
    }

    /// <summary>一条折线 + 每个有效点的圆点；null = 没数据，断开，不伪造连线。</summary>
    private static void AppendSeries(
        StringBuilder svg, int count, Func<int, double?> value,
        Func<int, double> x, Func<double, double> y, string color)
    {
        var path = new StringBuilder();
        bool drawing = false;
        for (int i = 0; i < count; i++)
        {
            var v = value(i);
            if (v is null) { drawing = false; continue; }
            path.Append((drawing ? "L" : "M") + x(i).ToString("0.0", CultureInfo.InvariantCulture)
                + "," + y(v.Value).ToString("0.0", CultureInfo.InvariantCulture));
            drawing = true;
        }
        svg.AppendLine($"<path d=\"{path}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" "
            + "stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");

        for (int i = 0; i < count; i++)
        {
            var v = value(i);
            if (v is null) continue;
            svg.AppendLine($"<circle cx=\"{x(i):0.0}\" cy=\"{y(v.Value):0.0}\" r=\"2.6\" fill=\"{color}\"/>");
        }
    }

    // ------------------------------------------------------------------
    // SVG 折线图
    // ------------------------------------------------------------------

    private static string LineChart(List<DailyTrend> days, int[] cats,
        Func<CatDayStat, double> pick, string unit, bool zeroBased, bool onAc)
    {
        if (days.Count == 0)
            return "<div class=\"muted\">暂无数据。</div>";

        const int W = 920, H = 260, PL = 52, PR = 16, PT = 14, PB = 38;
        int plotW = W - PL - PR, plotH = H - PT - PB;

        double? Value(int cat, DailyTrend d)
        {
            var s = d.Get(cat, onAc);
            if (s is null) return null;
            var v = pick(s);
            return double.IsNaN(v) ? null : v;
        }

        var all = cats.SelectMany(c => days.Select(d => Value(c, d))).OfType<double>().ToList();
        if (all.Count == 0)
            return "<div class=\"muted\">暂无数据。</div>";

        double yMin = zeroBased ? 0 : Math.Floor(all.Min() - 0.5);
        double yMax = Math.Ceiling(all.Max() + 0.5);
        if (yMax - yMin < 1) yMax = yMin + 1;

        double X(int i) => days.Count == 1 ? PL + plotW / 2.0 : PL + (double)i * plotW / (days.Count - 1);
        double Y(double v) => PT + plotH - (v - yMin) / (yMax - yMin) * plotH;

        var svg = BeginChart(W, H);

        // 网格 + Y 轴标签
        for (int g = 0; g <= 4; g++)
        {
            var yv = yMin + (yMax - yMin) * g / 4;
            AppendGridLine(svg, PL, W - PR, Y(yv), PL - 8, "end", yv);
        }

        // X 轴标签：最多 8 个
        AppendXLabels(svg, days.Count, H, X, i => days[i].Date);

        // 各系列
        for (int ci = 0; ci < cats.Length; ci++)
        {
            var color = VisualTones.ReportSeries[ci % VisualTones.ReportSeries.Length];
            var cat = cats[ci];
            AppendSeries(svg, days.Count, i => Value(cat, days[i]), X, Y, color);
        }

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static int[] ActiveCategories(TrendReport r)
    {
        var set = new HashSet<int>();
        foreach (var d in r.Days)
        {
            foreach (var k in d.Ac.Keys) set.Add(k);
            foreach (var k in d.Dc.Keys) set.Add(k);
        }
        var list = set.ToList();
        list.Sort();
        return list.ToArray();
    }

    private static string Legend(int[] cats, AppSettings settings)
    {
        var sb = new StringBuilder("<div class=\"legend\">");
        for (int i = 0; i < cats.Length; i++)
            sb.AppendLine($"<span><i style=\"background:{VisualTones.ReportSeries[i % VisualTones.ReportSeries.Length]}\"></i>"
                + Esc(settings.CategoryName(cats[i])) + "</span>");
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
