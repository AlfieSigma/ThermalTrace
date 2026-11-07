using System.Globalization;
using ThermalTrace.Analysis;
using ThermalTrace.Diagnostics;
using ThermalTrace.Hardware;
using ThermalTrace.Model;
using ThermalTrace.Services;
using ThermalTrace.Storage;
using ThermalTrace.UI;

namespace ThermalTrace.Core;

/// <summary>命令分发。GUI 之外的维护入口，用户日常不需要碰。</summary>
internal static class Commands
{
    /// <summary>默认：后台常驻采样 + 托盘。</summary>
    public static int Background()
    {
        // 【单实例】放在读配置与打开硬件之前：第二个实例不应该再初始化 LHM、
        // 起采样线程或写 CSV。命令行模式（--today / --trend 等）不经过这里，仍可并发。
        using var single = SingleInstance.Acquire();
        if (!single.IsPrimary)
        {
            single.SignalShow();   // 让已有实例把主窗口叫出来，避免双击后看起来毫无反应
            BootLog.Note("检测到已有实例在运行，本次启动退出（未启动采样与写盘）");
            ConsoleHost.WriteLine("ThermalTrace 已在运行，本次启动未重复采样；已请求显示已有窗口。");
            return 0;
        }

        var settings = SettingsStore.Load();
        SettingsStore.EnsureCleanDate(settings);

        using var hw = new HardwareMonitor();
        hw.Open();

        // 【B6 采样前兜底】绑定缺失时先自动推断一份再开采样。
        // 顺序很重要：必须在 SamplerService 构造之前、采样线程起来之前。
        // 未绑定时采样不会失败，只会把整片全 NaN 的行写进 CSV（读不到 id → null → OrNa），
        // 而事后没有任何标记能把这些废数据挑出来。测试环境早已绑好，所以这个洞从没暴露过。
        EnsureSensorBinding(settings, hw);

        using var sampler = new SamplerService(settings, hw);
        // GUI 的「重新绑定传感器」需要采样服务把新绑定热换进正在跑的循环（红线：Toggle 签名不许动，
        // 所以从托盘创建链路上把实例挂上去，而不是给 Toggle 加参数）
        MainWindowHost.Attach(sampler);
        using var tray = new TrayIconHost(settings, sampler, single);
        tray.Start();

        // 【2026-10-09 修复】双击/自启场景下 Ensure() 会 AllocConsole 弹出日志窗口，
        // 用户误以为需要手动关闭（关闭即退出程序）。托盘就绪后立即释放自己申请的控制台，
        // 后台常驻应是无窗口的；从终端启动时 Attach 父控制台则不动。
        ConsoleHost.ReleaseIfOwned();

        // 【首次运行引导】：自启还没开 + 还没问过 → 问一次，之后不再打扰。
        // 放在采样线程起来之前也没关系，真正的弹窗由内部 Timer 推到消息循环之后。
        SetupHint.MaybeShow();

        // 【2026-10-09 修复】采样循环必须放在后台线程：
        // 此前在主线程直接 sampler.Run() 会阻塞消息循环，WinForms Timer（托盘 tooltip
        // 每 10 秒刷新）永不触发，tooltip 永远停在"等待传感器数据…"（实测）。
        // 2026-11-06：线程创建收进 SamplerService.Start()，看门狗才能在异常退出后重拉。
        sampler.Start();

        // 【内存优化】启动 30 秒后主动收一次瞬态垃圾（JIT / LHM 枚举 / WMI 的峰值）。
        // .NET 的 GC 是懒的，不收这一下，启动峰值会永远贴在工作集里 —— 见 Core\StartupGc。
        StartupGc.Schedule();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            ConsoleHost.WriteLine("正在停止…");
            sampler.Cancel();
            Application.Exit();
        };

        // 主线程跑消息循环：托盘 tooltip 刷新、菜单点击、主窗口 Toggle 都依赖它
        Application.Run();
        return 0;
    }

    /// <summary>开发期验证：前台运行并打印实时状态。</summary>
    public static int Dump(ParsedCommand cmd)
    {
        var settings = SettingsStore.Load();
        SettingsStore.EnsureCleanDate(settings);
        var minutes = cmd.Minutes ?? 2;

        using var hw = new HardwareMonitor();
        hw.Open();
        using var sampler = new SamplerService(settings, hw);

        var start = DateTimeOffset.Now;
        // 【2026-10-09 修复】必须用线程池定时器，不能用 WinForms Timer：
        // sampler.Run() 会阻塞主线程，WinForms Timer 依赖消息循环，Tick 永远不会触发。
        using var timer = new System.Threading.Timer(_ =>
        {
            var s = sampler.Latest;
            var elapsed = (DateTimeOffset.Now - start).TotalMinutes;
            if (elapsed >= minutes)
            {
                // 此前 minutes 只显示在提示语里，从未真正触发退出，
                // 导致 --dump 只能靠 Ctrl+C 结束。
                sampler.Cancel();
                return;
            }
            if (s is null) return;
            ConsoleHost.WriteLine(
                $"{elapsed,5:0.0}min  CPU {s.PrimaryCpuC,5:0.0}C  " +
                $"功耗 {s.PackagePowerW,5:0.0}W  总 {s.UtilPct,5:0.0}%  " +
                $"单核 {s.MaxCoreUtilPct,5:0.0}%  档 {sampler.LatestContext.Band.Key(),-6} " +
                $"余量 {(double.IsNaN(s.TjMaxDistanceC) ? double.NaN : s.TjMaxDistanceC),5:0.0}C");
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; sampler.Cancel(); };
        ConsoleHost.WriteLine($"前台验证模式，运行 {minutes} 分钟后自动退出。\n");
        sampler.Run();
        ConsoleHost.WriteLine($"\n共采样 {sampler.SampleCount} 次，失败 {sampler.ErrorCount} 次，写入 {sampler.Aggregator.RowsWritten} 行。");
        return 0;
    }

    /// <summary>
    /// --today：打印今日摘要。
    ///
    /// 【为什么改用 TodaySummary】以前这里是按 10% 细档逐行等权平均，
    /// 与 CatDayStat（趋势报告口径）的 SampleCount 加权不一致，
    /// 同一个人会在终端和 GUI 看到两个不同的数字（GUI 与终端口径一致）。
    /// 现在终端与 GUI 卡片页走同一个 Analysis.TodaySummary，数值逐项一致。
    /// </summary>
    public static int Today()
    {
        var settings = SettingsStore.Load();
        var today = DateOnly.FromDateTime(DateTime.Now);

        var ac = Analysis.TodaySummary.Build(settings, today, true);
        if (!ac.HasAnyData)
        {
            ConsoleHost.WriteLine($"今日（{today:yyyy-MM-dd}）暂无可用温度数据。"
                + "程序需以管理员权限运行；若已提权，请运行 --self-check 确认主温度传感器是否可读。");
            return 0;
        }

        PrintToday(ac);
        if (ac.HasDcData) PrintToday(Analysis.TodaySummary.Build(settings, today, false));
        return 0;
    }

    private static void PrintToday(Analysis.TodaySummaryData d)
    {
        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine($"=== 今日概览 {d.Date:yyyy-MM-dd} · 供电：{(d.OnAc ? "交流" : "电池")} ===");
        ConsoleHost.WriteLine($"环境气温 {(double.IsNaN(d.AmbientC) ? "不可用" : $"{d.AmbientC:0.0}C" + (d.AmbientIsProxy ? "（代理）" : ""))}"
            + $"   已记录 {HoursText(d.RecordedHours)}   样本 {d.SampleCount}   时间片 {d.SliceCount}");
        ConsoleHost.WriteLine("空闲基线 "
            + (double.IsNaN(d.IdleBaseline)
                ? "不可用"
                : $"{d.IdleBaseline:0.0}C（档 {d.IdleBaselineBand}{(d.IdleApproximated ? "，近似" : "")}）"));
        ConsoleHost.WriteLine();

        ConsoleHost.WriteLine("类别    均温     ΔT      时长   平均功耗   超85%  超85时长     样本");
        ConsoleHost.WriteLine(new string('-', 74));
        foreach (var c in d.Cards)
            ConsoleHost.WriteLine(
                $"{Pad(c.Name, 6)} {Num(c.TempMean),6}C {c.DeltaTText,8} {c.HoursText,7} "
                + $"{Num(c.PowerMean),8}W {Num(c.Over85Pct),7}% {(long)Math.Round(c.Over85Minutes),7}分 {c.Samples,8}");

        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine("今日最高 "
            + (double.IsNaN(d.MaxTempC)
                ? "—"
                : $"{d.MaxTempC:0.0}C · {(d.MaxTempAt is null ? "--:--" : d.MaxTempAt.Value.ToString("HH:mm"))}"
                  + (d.MaxTempCategory.Length > 0 ? $" · {d.MaxTempCategory}" : ""))
            + $"      超 85℃ 累计 {(long)Math.Round(d.Over85Minutes)} 分钟");

        // GPU / SSD 两行与 GUI 页脚同一串文案（G4 同源同值）
        ConsoleHost.WriteLine(d.GpuText);
        ConsoleHost.WriteLine(d.SsdText);

        if (d.IdleApproximated)
            ConsoleHost.WriteLine($"注：今日无 0-10% 空闲档数据，ΔT 基线用「{d.IdleBaselineBand}」档近似。");
    }

    private static string Num(double v) =>
        double.IsNaN(v) ? "—" : v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>按显示宽度补空格。中文占两列，用 {x,-6} 会把表头挤歪。</summary>
    private static string Pad(string s, int width)
    {
        var used = s.Sum(ch => ch > 0x2E80 ? 2 : 1);
        return s + new string(' ', Math.Max(1, width - used));
    }

    /// <summary>小时数的紧凑格式。实现收敛到 <see cref="Analysis.TodaySummaryData"/>，两处不再各写一份。</summary>
    private static string HoursText(double hours) => Analysis.TodaySummaryData.HoursText(hours);

    /// <summary>
    /// 生成自包含 HTML 趋势报告并返回文件路径与有效天数。
    ///
    /// <c>--trend</c> 与解读页「生成并打开 HTML 报告」按钮共用这一份：
    /// 报告排版、取数与落盘都不在 UI 层另写一份。窗口口径与 <c>--trend</c> 相同
    /// （<see cref="Analysis.TrendAnalyzer.Build"/> 不传 window，结论区恒按近 30 天）；
    /// 供电由调用方决定，GUI 传入页面当前的交流 / 电池选择。
    /// </summary>
    public static (string File, int DaysWithData) WriteTrendReport(AppSettings settings, bool onAc = true)
    {
        // 即使本次没启动采样，也登记当前口径，保证 caliber 历史连续
        Analysis.FingerprintStore.Record(settings);

        var report = Analysis.TrendAnalyzer.Build(settings, null, onAc);
        // 硬件章节与结论区解读同按 30 天窗口（--trend 不传 window 的默认行为），
        // 保证报告内各处口径一致（3.5）。
        var hardware = Analysis.HardwareTrend.Build(settings, Analysis.TrendWindow.Month);
        var html = Analysis.HtmlTrendReport.Render(report, settings, hardware);

        var file = Path.Combine(AppPaths.ReportsDir,
            $"trend-{DateTime.Now:yyyyMMdd-HHmm}.html");
        File.WriteAllText(file, html, new System.Text.UTF8Encoding(true));

        // 报告已落盘后再清旧份，失败静默（G15）；GUI 走后台线程，不拖慢界面
        Retention.PruneReports();
        return (file, report.DaysWithData);
    }

    /// <summary>--trend：生成自包含 HTML 趋势报告，默认在浏览器打开。</summary>
    /// <param name="onAc">按哪路供电出数（--power，默认交流）。</param>
    public static int Trend(bool open, bool onAc = true)
    {
        var settings = SettingsStore.Load();
        var (file, daysWithData) = WriteTrendReport(settings, onAc);

        ConsoleHost.WriteLine($"[OK] 趋势报告已生成：{file}");
        if (daysWithData < 2)
            ConsoleHost.WriteLine("提示：当前数据不足 2 天，报告结论区会明确提示，连续运行后再生成即可。");

        if (open)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = file,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ConsoleHost.WriteError($"[warn] 无法自动打开浏览器，请手动打开报告文件: {ex.Message}");
            }
        }
        return 0;
    }

    /// <summary>
    /// --interpret：终端打印趋势解读全文（调试用，不进 README 命令表）。
    ///
    /// 【为什么不把 AI 解读做进程序本体】LLM 解读需要 key、需要联网、
    /// 还要把温度与使用习惯数据传出本机，与项目"断网可用、零隐私上传、无外部依赖"
    /// 的一贯哲学冲突。这里输出的是<b>本地规则引擎</b>的解读全文，附一份 AI 摘要，
    /// 要不要把它粘贴给外部模型，由用户自己决定。
    ///
    /// 【为什么这里的数字与 --trend 必然一致】两者都走
    /// <c>TrendAnalyzer.Build(settings)</c> + <c>Interpreter.Build(settings, report)</c>，
    /// 同一个引擎、同一份数字。
    /// </summary>
    public static int Interpret(bool onAc = true)
    {
        var settings = SettingsStore.Load();

        // 与 --trend 一致：即使本次没启动采样也登记当前口径，保证 caliber 历史连续
        Analysis.FingerprintStore.Record(settings);

        var report = Analysis.TrendAnalyzer.Build(settings, null, onAc);
        // 与 --trend 一致：解读恒按 30 天窗口（不传 window 的默认行为），
        // hardware 也按同一窗口构建，GPU 混杂因素证据才有前后对比（3.4）。
        var hardware = Analysis.HardwareTrend.Build(settings, Analysis.TrendWindow.Month);
        var it = Analysis.Interpreter.Build(settings, report, null, hardware);

        PrintInterpretation(settings, report, it);
        return 0;
    }

    /// <summary>
    /// --eink：把趋势渲染成极趣 Note 4 的 400×300 黑白快照，存到 <c>eink\</c> 并打开预览。
    ///
    /// 【为什么这一步完全不联网、不需要设备】P1 的验收全靠它：排版、二值化效果、
    /// 数字对不对，在电脑上就能看完——不必等 Note 4 到货，也不必先申请 API Key。
    /// 这也是选"全自绘"而不是交给设备渲染文字的核心收益。
    ///
    /// 【2026-10-16 起改走新链】：5 条负载档 ΔT + WLS 总体趋势，
    /// 不吃 Interpreter（解读不上图，图只吃 TrendReport）。旧链（A/B/C 版面）已无人使用。
    ///
    /// 【双槽】2026-11-05 起默认两槽都出图：槽 1 近 30 天趋势图，槽 2 自清灰以来平滑图
    /// （见推送双槽位说明）。--slot 只筛选出哪一槽；本命令始终不联网、不写推送状态。
    /// </summary>
    public static int Eink(bool open, string? variant = null, string slot = "both", bool onAc = true)
    {
        var settings = SettingsStore.Load();

        // 与 --trend / --interpret 一致：即使本次没启动采样也登记当前口径
        Analysis.FingerprintStore.Record(settings);

        var report = Analysis.TrendAnalyzer.Build(settings, null, onAc);
        Directory.CreateDirectory(AppPaths.EinkDir);

        var files = new List<string>();
        if (slot is "both" or "1")
            files.Add(EinkSlot1(settings, report));
        if (slot is "both" or "2")
        {
            var path = EinkSlot2(settings, report);
            if (path is not null) files.Add(path);
        }

        // 本次出图完成后清掉窗口外的旧快照，失败静默（G15）
        Retention.PruneEink(DateOnly.FromDateTime(DateTime.Now));

        // 只做本地出图验证：不联网、不写推送状态、不碰设备
        if (open)
            foreach (var file in files)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = file,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    ConsoleHost.WriteError($"[warn] 无法自动打开图片，请手动打开：{ex.Message}");
                }
            }

        return 0;
    }

    /// <summary>
    /// 槽 1：近 30 天趋势版面（5 档 ΔT + WLS 总体趋势）。
    /// 与推送槽 1 同一条链，一个数都不另算；数据不足时出"已积累 N 天"模板图。
    /// </summary>
    private static string EinkSlot1(AppSettings settings, Analysis.TrendReport report)
    {
        if (report.DaysWithData < Analysis.EinkSnapshotBuilder.MinDaysToDraw)
        {
            // 模板图按天数缓存：天数没变就直接复用，一次都不重画
            var cached = Path.Combine(AppPaths.EinkDir, $"nodata-{report.DaysWithData}.png");
            if (File.Exists(cached))
            {
                ConsoleHost.WriteLine($"[OK] 槽 1 复用已缓存的无数据模板图：{cached}");
            }
            else
            {
                File.WriteAllBytes(cached, Export.EinkRenderer.RenderNoDataPng(report.DaysWithData));
                ConsoleHost.WriteLine($"[OK] 槽 1 已生成无数据模板图：{cached}");
            }

            ConsoleHost.WriteLine(
                $"提示：已积累 {report.DaysWithData} 天，"
                + $"满 {Analysis.EinkSnapshotBuilder.MinDaysToDraw} 天后开始出趋势图。");
            return cached;
        }

        // 5 档 ΔT + WLS 总体趋势。槽 1 的唯一版面，推送走的是同一条链。
        var snap = Analysis.EinkSnapshotBuilder.Build(settings, report);

        var path = Path.Combine(AppPaths.EinkDir, $"p6-v9-{report.LastDay:yyyyMMdd}.png");
        File.WriteAllBytes(path,
            Export.EinkRenderer.RenderV2(snap, w => ConsoleHost.WriteLine(w), DateTime.Now));
        ConsoleHost.WriteLine($"[OK] 槽 1 趋势图：{path}");

        ConsoleHost.WriteLine($"负载线：{snap.Lines.Count} 条（{string.Join("/", snap.Lines.Select(l => l.Name))}）"
            + $"　距清灰：{(snap.CleanDays is { } d ? d + " 日" : "未设基准日")}"
            + $"　总体趋势：{(snap.Trend is null ? "不显著，未画" : $"{snap.Trend.PerDay:+0.000;-0.000}℃/天")}");

        // 逐档逐点打印：与 GUI 趋势页对数用（同按报告供电口径取数）
        foreach (var line in snap.Lines)
        {
            ConsoleHost.WriteLine($"  {line.Name}："
                + string.Join(" ", line.Points.Select(p =>
                    $"{p.Day:MM-dd}={(p.DeltaT is { } v ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "—")}(n={p.Weight})")));
        }
        return path;
    }

    /// <summary>
    /// 槽 2：自清灰以来的平滑图（5 条按样本数加权的居中移动平均 + 一条固定加权预测线）。
    /// 不适用（无清灰日 / 清灰日晚于末数据日 / 有效日不足 3 天）时只打印原因，不出图、返回 null；
    /// 这是"不适用"而不是失败，退出码仍为 0。
    /// </summary>
    private static string? EinkSlot2(AppSettings settings, Analysis.TrendReport report)
    {
        var snap = Analysis.EinkSnapshotBuilder.Build(settings, report, Analysis.EinkSlot.Slot2);
        if (!snap.IsApplicable)
        {
            ConsoleHost.WriteLine($"[--] 槽 2 不适用：{snap.SkipReason ?? "未知原因"}");
            return null;
        }

        var path = Path.Combine(AppPaths.EinkDir, $"p6-slot2-{report.LastDay:yyyyMMdd}.png");
        File.WriteAllBytes(path,
            Export.EinkRenderer.RenderV2(snap, w => ConsoleHost.WriteLine(w), DateTime.Now));
        ConsoleHost.WriteLine($"[OK] 槽 2 平滑图：{path}");

        ConsoleHost.WriteLine($"负载线：{snap.Lines.Count} 条（{string.Join("/", snap.Lines.Select(l => l.Name))}）"
            + $"　跨度：{snap.SpanDays} 天"
            + $"　平滑窗口：{snap.SmoothWindowDays} 天"
            + $"　距清灰：{(snap.CleanDays is { } d ? d + " 日" : "未设基准日")}"
            + $"　加权预测线：{(snap.Trend is null ? "有效平滑点不足 3，未画" : $"{snap.Trend.PerDay:+0.000;-0.000}℃/天")}");

        // 逐档逐点打印平滑值（与图上折线同源）：渲染成"日期=0.0(n=窗口样本数合计)"
        foreach (var line in snap.Lines)
        {
            ConsoleHost.WriteLine($"  {line.Name}："
                + string.Join(" ", line.Points.Select(p =>
                    $"{p.Day:MM-dd}={(p.DeltaT is { } v ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "—")}(n={p.Weight})")));
        }
        return path;
    }

#if PUSH_ENABLED
    /// <summary>
    /// --push：把趋势快照推到极趣 Note 4。加 <c>--dry-run</c> 走全流程只跳过 HTTP 发送（D10）。
    ///
    /// 【为什么是同步外壳包一层 Task】PushService 是 async（HTTP + 重试），
    /// 而 CLI 入口是同步的。这里用 <c>GetAwaiter().GetResult()</c> 把异步跑完，
    /// 异常（如被取消）会原样抛出给 Main 的兜底——但 PushService 自身已吞掉所有网络错误，
    /// 正常情况下这里只会拿到一个带明确 Message 的 PushOutcome，不会崩。
    /// </summary>
    public static int Push(bool force, bool dryRun = false)
    {
        var settings = SettingsStore.Load();
        var outcome = Task.Run(() =>
            ThermalTrace.Push.PushService.PushAsync(settings, force, dryRun, s => ConsoleHost.WriteLine(s))
        ).GetAwaiter().GetResult();

        if (outcome.Ok)
            ConsoleHost.WriteLine($"[OK] {outcome.Message}");
        else if (outcome.Skipped)
            // 当日幂等跳过：不是失败，不该染成红色、也不该当错误退出码语义宣传
            ConsoleHost.WriteLine($"[--] {outcome.Message}");
        else
            ConsoleHost.WriteError($"[ERR] {outcome.Message}");

        return outcome.Ok ? 0 : 1;
    }
#endif

    private static void PrintInterpretation(
        AppSettings settings, Analysis.TrendReport report, Analysis.InterpretationReport it)
    {
        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine("=== 趋势解读（调试输出）===");
        ConsoleHost.WriteLine(report.FirstDay is { } f && report.LastDay is { } l
            ? $"数据范围 {f:yyyy-MM-dd} ~ {l:yyyy-MM-dd}（{report.DaysWithData} 个有效天数）"
            : "数据范围：无（0 个有效天数）");

        // 口径警告要在结论之前——它是"后面这些数字能不能放在一起比"的前提
        foreach (var w in report.CaliberWarnings)
        {
            ConsoleHost.WriteLine();
            ConsoleHost.WriteLine("⚠ " + w);
        }

        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine($"健康度：{it.Health.Text()}");
        ConsoleHost.WriteLine($"总结：{it.Summary}");

        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine("证据：");
        if (it.Evidence.Count == 0)
            ConsoleHost.WriteLine("  （数据不足或无显著变化）");
        else
            for (int i = 0; i < it.Evidence.Count; i++)
                ConsoleHost.WriteLine($"  {i + 1}. {it.Evidence[i].Text}");

        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine("可能原因（含验证方法）：");
        if (it.Attributions.Count == 0)
            ConsoleHost.WriteLine(it.Health == Analysis.HealthLevel.Insufficient
                ? "  （数据不足，不做归因）"
                : "  （无明显异常模式）");
        else
            foreach (var a in it.Attributions)
                ConsoleHost.WriteLine($"  - {a.Text}");

        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine("建议：");
        for (int i = 0; i < it.Suggestions.Count; i++)
            ConsoleHost.WriteLine($"  {i + 1}. {it.Suggestions[i]}");

        // Clean 为 null 有两种完全不同的原因："用户没填"和"数据不足暂不评估"。
        // 一律打印"未设置"会把后者说成前者——这正是项目一贯要避免的把缺失说成事实。
        ConsoleHost.WriteLine();
        if (it.Clean is not null)
            ConsoleHost.WriteLine($"清灰基准：{it.Clean.Text}");
        else if (settings.CleanDate is { } d)
            ConsoleHost.WriteLine($"清灰基准：{d:yyyy-MM-dd}（数据不足，暂不评估衰减）");
        else
            ConsoleHost.WriteLine("清灰基准：未设置（设置页可填）");

        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine($"可信度：{it.ConfidenceNote}");

        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine("--- AI 数据摘要（可复制给外部模型）---");
        ConsoleHost.WriteLine(it.AiDigest);
    }

    public static int SelfCheck()
    {
        var settings = SettingsStore.Load();
        SettingsStore.EnsureCleanDate(settings);
        using var hw = new HardwareMonitor();
        hw.Open();
        ConsoleHost.WriteLine($"正在连续采样 {SensorStabilityProbe.EffectiveTicks + SensorStabilityProbe.WarmupTicks} 秒，请稍候…");
        var report = Diagnostics.SelfCheck.Run(settings, hw);
        ConsoleHost.WriteLine(Diagnostics.SelfCheck.Format(report));
        return report.Passed ? 0 : 1;
    }

    /// <summary>
    /// 采样前自动绑定兜底（见 <see cref="Background"/> 里的说明）。
    /// 任何失败都只告警不抛：绑不上是"降级"，不该让程序起不来。
    /// </summary>
    private static void EnsureSensorBinding(AppSettings settings, HardwareMonitor hw)
    {
        if (settings.Sensors.IsUsable) return;

        try
        {
            if (PersistAutoBind(settings, hw.EnumerateSensors(), "采样前自动绑定") is null)
                ConsoleHost.WriteError(
                    "[warn] 尚未绑定传感器，且自动推断未找到可用的 CPU 温度传感器。\n"
                    + "       此时采样只会写入空值。可在 GUI『今日概览』页点『重新绑定传感器』重试。");
        }
        catch (Exception ex)
        {
            ConsoleHost.WriteError($"[warn] 自动绑定失败（不影响启动）: {ex.Message}");
        }
    }

    /// <summary>
    /// 把自动推断出的绑定固化下来：写 settings.json → 登记口径 → 记启动日志。
    ///
    /// 【红线】推断失败（IsUsable 为假）时返回 null 且**一个字节都不写**。
    /// 把空绑定写盘比不写更糟：用户看到"绑定已保存"却依旧没有数据，比直接报错更难排查。
    ///
    /// 【为什么要三个调用方共用】采样前兜底、--bind、GUI 按钮三处以前各写各的，
    /// 已经出现过"命令写了 FingerprintStore、GUI 忘了"这类分叉。口径登记漏一次，
    /// 趋势页就会把本次变更当成跨口径并报警。
    /// </summary>
    public static SensorCatalog.BindResult? PersistAutoBind(
        AppSettings settings, IReadOnlyList<SensorDescriptor> sensors, string source)
    {
        var result = SensorCatalog.AutoBind(sensors);
        if (!result.Binding.IsUsable) return null;

        settings.Sensors = result.Binding;
        SettingsStore.Save(settings);

        // 漏了会让本次绑定变更被当成"跨口径"，趋势页误报警（SamplerService.Run 同样在这里登记）
        try { Analysis.FingerprintStore.Record(settings); }
        catch { /* 口径登记失败不影响采样 */ }

        BootLog.Note($"{source}：识别到 {sensors.Count} 个可见传感器，"
            + $"主温度 {result.Binding.PrimarySensorName}");

        ConsoleHost.WriteLine($"[OK] 传感器绑定已保存（{source}），可见传感器 {sensors.Count} 个。");
        return result;
    }

    /// <summary>把绑定结果排成人能读的清单（--bind 与 GUI 弹窗共用）。</summary>
    public static string DescribeRoles(SensorCatalog.BindResult result)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var role in result.Roles)
            sb.AppendLine($"  {role.Label,-16}: "
                + (string.IsNullOrEmpty(role.ChosenId)
                    ? "缺失"
                    : result.Binding.DisplayNames.GetValueOrDefault(role.ChosenId, role.ChosenId)));
        return sb.ToString().TrimEnd();
    }

    public static int Bind()
    {
        var settings = SettingsStore.Load();
        using var hw = new HardwareMonitor();
        hw.Open();

        // 失败时不再写盘（旧实现是先 Save 再判 IsUsable，会留下一份空绑定）
        var result = PersistAutoBind(settings, hw.EnumerateSensors(), "手动执行 --bind");
        if (result is null)
        {
            ConsoleHost.WriteError(
                "[ERR] 未找到可用的 CPU 温度传感器，绑定未保存。\n"
                + "      请确认以管理员身份运行，且安全软件没有拦截 WinRing0 驱动。");
            return 1;
        }

        ConsoleHost.WriteLine("绑定已保存：");
        ConsoleHost.WriteLine(DescribeRoles(result));
        return 0;
    }

    /// <summary>
    /// GUI 用：跑一次自检并把报告文本取回来。
    ///
    /// 【为什么必须有】<see cref="SelfCheck"/> 只往控制台写，而程序是 WinExe（GUI 子系统），
    /// GUI 模式下根本没有控制台。托盘菜单与两个页签都曾因此"点了没反应"。
    /// ConsoleHost 每次输出都读 Console.Out，临时接管一次就能拿到完整报告，
    /// 不必为了拿文本去改 SelfCheck 的签名。
    /// </summary>
    public static string SelfCheckText()
    {
        var previous = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            SelfCheck();
        }
        finally
        {
            Console.SetOut(previous);
        }
        return buffer.ToString().Trim();
    }

    public static int ResetCleanDate(string iso)
    {
        var s = SettingsStore.Load();
        if (!DateOnly.TryParse(iso, out var d))
        {
            ConsoleHost.WriteError($"[错误] 日期格式应为 yyyy-MM-dd，收到: {iso}");
            return 2;
        }
        s.CleanDateIso = d.ToString("yyyy-MM-dd");
        SettingsStore.Save(s);
        ConsoleHost.WriteLine($"[OK] 清灰基准日已设为 {s.CleanDateIso}");
        return 0;
    }
}

internal static class AutoStartCommands
{
    public static int Install()
    {
        var result = AutoStart.Enable();
        if (!result.Ok)
        {
            ConsoleHost.WriteError("[ERR] 注册开机自启失败");
            ConsoleHost.WriteError("      " + result.Message.Replace("\n", "\n      "));
            return 1;
        }

        ConsoleHost.WriteLine("[OK] " + result.Message);

        // 建完立刻跑一次：让"任务到底能不能启动"当场可见，而不是等下次重启才发现。
        // 重复执行时 MultipleInstancesPolicy=IgnoreNew，不会起第二个实例。
        if (AutoStart.StartNow(out var runMsg))
            ConsoleHost.WriteLine($"[OK] 已立即触发一次运行（{runMsg}），托盘图标应很快出现。");
        else
            ConsoleHost.WriteLine($"[warn] {runMsg}（任务已创建，下次登录时会自动启动）");

        ConsoleHost.WriteLine($"状态：{AutoStart.Status().Describe()}");
        return 0;
    }

    public static int Uninstall()
    {
        var ok = AutoStart.Disable();
        ConsoleHost.WriteLine(ok
            ? "[OK] 已取消开机自启（计划任务已删除，当前运行中的实例不受影响）"
            : "[ERR] 取消失败：删除计划任务被拒绝。请以管理员身份运行。");
        return ok ? 0 : 1;
    }

    /// <summary>--status：真实状态 + 启动记录。判断依据必须是可观测的运行事实。</summary>
    public static int Status()
    {
        var st = AutoStart.Status();
        ConsoleHost.WriteLine("=== 开机自启状态 ===");
        ConsoleHost.WriteLine($"计划任务：\\{AutoStart.TaskName} — {(st.TaskExists ? "已注册" : "未注册")}");
        ConsoleHost.WriteLine($"结论：{st.Describe()}");
        ConsoleHost.WriteLine($"最近开机：{st.BootTime:yyyy-MM-dd HH:mm:ss}"
            + $"（已运行 {(int)(DateTime.Now - st.BootTime).TotalMinutes} 分钟）");
        ConsoleHost.WriteLine($"上次运行：{(st.LastRun is null ? "从未运行" : st.LastRun.Value.ToString("yyyy-MM-dd HH:mm:ss"))}");
        ConsoleHost.WriteLine($"上次结果：{st.LastResultText}");
        if (st.Command.Length > 0) ConsoleHost.WriteLine($"任务命令：{st.Command}");
        ConsoleHost.WriteLine($"当前进程：{(Elevation.IsElevated ? "已提权" : "未提权")}  {AutoStart.ExePath}");
        if (AutoStart.HasLegacyRunValue())
            ConsoleHost.WriteLine("[warn] 仍存在旧的 HKCU 启动项（与提权互斥，永远不会启动），"
                + "运行 --install 重建任务时会顺手清理。");

        ConsoleHost.WriteLine();
        ConsoleHost.WriteLine($"=== 最近启动记录（{BootLog.FilePath}）===");
        var lines = BootLog.Recent(5);
        if (lines.Count == 0)
            ConsoleHost.WriteLine("（无记录：程序还没启动过）");
        else
            foreach (var l in lines) ConsoleHost.WriteLine("  " + l);
        return 0;
    }
}
