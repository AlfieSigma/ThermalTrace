namespace ThermalTrace.Core;

internal enum CommandKind
{
    /// <summary>无参数：启动后台采样 + 托盘（默认行为）</summary>
    Background,
    Install,
    Uninstall,
    SelfCheck,
    Bind,
    Today,
    Trend,
    /// <summary>--interpret：打印趋势解读全文（调试用，与 --trend 同源同值）</summary>
    Interpret,
    /// <summary>--eink：渲染 400×300 黑白墨水屏快照（本地可预览，不联网、不需要设备）</summary>
    Eink,
    /// <summary>--push：把快照推到极趣 Note 4（需要 API Key；受每日幂等保护）</summary>
    Push,
    Dump,
    /// <summary>--status：开机自启的真实状态 + 启动记录（不再靠"注册表写成功"判断）</summary>
    Status
}

internal sealed class ParsedCommand
{
    public CommandKind Kind { get; init; } = CommandKind.Background;
    public int? Minutes { get; init; }
    public string? ResetCleanDate { get; init; }
    public bool NoOpen { get; init; }

    /// <summary>--force：忽略当日幂等，强制重推（开发期反复测试用）。</summary>
    public bool Force { get; init; }

    /// <summary>--dry-run：推送全真走只跳过 HTTP 发送（D10；验证流程 / 调度 / 幂等，不惊动设备）。</summary>
    public bool DryRun { get; init; }

    /// <summary>--variant：墨水屏版面选型用（a / b / c，默认全部出图对比）。</summary>
    public string? Variant { get; init; }

    /// <summary>
    /// --slot：<c>--eink</c> 出哪个槽。<c>"1"</c> / <c>"2"</c> / <c>"both"</c>（默认 both）。
    /// 只影响本地出图，不涉及推送。
    /// </summary>
    public string Slot { get; init; } = "both";

    /// <summary>--power：分析类命令按哪路供电出数，<c>ac</c>（默认）/ <c>dc</c>。</summary>
    public bool OnAc { get; init; } = true;
}

/// <summary>
/// 命令解析。GUI 主导的设计下，CLI 只承担三类职责：
/// 开机自启入口、安装/卸载自启项、以及开发期的验证命令。
/// 用户日常不需要碰它。
/// </summary>
internal static class CommandLine
{
    public static ParsedCommand Parse(string[] args)
    {
        var kind = CommandKind.Background;
        int? minutes = null;
        string? resetDate = null;
        bool noOpen = false;
        bool force = false;
        bool dryRun = false;
        string? variant = null;
        string slot = "both";
        bool onAc = true;

        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i].TrimStart('-').ToLowerInvariant();
            var eq = token.IndexOf('=');
            var key = eq < 0 ? token : token[..eq];
            var inlineValue = eq < 0 ? null : token[(eq + 1)..];

            switch (key)
            {
                case "install":
                    kind = CommandKind.Install;
                    break;
                case "uninstall":
                    kind = CommandKind.Uninstall;
                    break;
                case "self-check" or "doctor":
                    kind = CommandKind.SelfCheck;
                    break;
                case "bind":
                    kind = CommandKind.Bind;
                    break;
                case "today":
                    kind = CommandKind.Today;
                    break;
                case "trend":
                    kind = CommandKind.Trend;
                    break;
                case "interpret":
                    kind = CommandKind.Interpret;
                    break;
                case "eink":
                    kind = CommandKind.Eink;
                    break;
#if PUSH_ENABLED
                case "push":
                    kind = CommandKind.Push;
                    break;
#else
                case "push":
                    throw new CommandLineException("本版本不包含墨水屏推送功能。");
#endif
                case "variant":
                    variant = Next(args, ref i, inlineValue);
                    break;
                case "slot":
                    slot = ParseSlot(Next(args, ref i, inlineValue));
                    break;
                case "power":
                    onAc = ParsePower(Next(args, ref i, inlineValue));
                    break;
                case "no-open":
                    noOpen = true;
                    break;
                case "force":
                    force = true;
                    break;
                case "dry-run" or "dryrun":
                    dryRun = true;
                    break;
                case "dump":
                    kind = CommandKind.Dump;
                    minutes = ParseInt(Next(args, ref i, inlineValue));
                    break;
                case "status":
                    kind = CommandKind.Status;
                    break;
                case "reset-clean-date":
                    resetDate = Next(args, ref i, inlineValue);
                    break;
                case "help" or "h" or "?":
                    ConsoleHost.WriteLine(HelpText.Text);
                    Environment.Exit(0);
                    break;
                default:
                    throw new CommandLineException($"未知参数: {key}");
            }
        }

        return new ParsedCommand
        {
            Kind = kind,
            Minutes = minutes,
            ResetCleanDate = resetDate,
            NoOpen = noOpen,
            Force = force,
            DryRun = dryRun,
            Variant = variant,
            Slot = slot,
            OnAc = onAc
        };
    }

    /// <summary>--power 取值：ac（默认）/ dc。写错值直接报错，不静默回落。</summary>
    private static bool ParsePower(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "ac" or "交流" => true,
        "dc" or "battery" or "电池" => false,
        _ => throw new CommandLineException($"--power 取值应为 ac / dc，收到: {raw}")
    };

    /// <summary>--slot 取值：1 / 2 / both（all 视同 both）。写错值直接报错，不静默回落。</summary>
    private static string ParseSlot(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "1" => "1",
        "2" => "2",
        "both" or "all" => "both",
        _ => throw new CommandLineException($"--slot 取值应为 1 / 2 / both，收到: {raw}")
    };

    private static string Next(string[] args, ref int i, string? inlineValue)
    {
        if (inlineValue is not null) return inlineValue;
        if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) return args[++i];
        throw new CommandLineException("参数缺少取值");
    }

    private static int? ParseInt(string? s) =>
        int.TryParse(s, out var v) && v > 0 ? v : null;
}

internal sealed class CommandLineException(string message) : Exception(message);

internal static class HelpText
{
    /// <summary>公共帮助 + 按版本拼接的推送命令说明（普通版不出现任何推送入口）。</summary>
    public const string Text = Head + PushHelp + Tail;

    private const string Head =
        """
        ThermalTrace - 笔记本散热长期趋势记录工具（通常无需手动使用）

        日常使用：直接运行 ThermalTrace.exe 即可。
          它在后台常驻、开机自启，按你的真实工作负载记录温度，
          每天只写约 20 KB 数据。实时温度显示请继续用 LiteMonitor。

        以下命令仅用于维护与排查：
          --install注册开机自启（计划任务方式，需要管理员权限）
          --uninstall     取消开机自启
          --status        查看开机自启的真实状态（任务是否注册 / 本次开机是否跑起来过）
          --self-check    传感器自检（排查读数是否可信）
          --bind          重新绑定传感器（换显卡驱动后需要）
          --today         打印今日摘要
          --trend         生成 HTML 散热趋势报告并在浏览器打开（加 --no-open 只生成不打开）
          --interpret     打印趋势解读全文（调试用，结论与 --trend 同一套引擎）
          --eink          渲染 400x300 黑白墨水屏快照（本地可预览；加 --no-open 只生成不打开）
                          默认两槽都出图；--eink --slot 1|2 只出对应一槽，--slot both 等同默认
          --power ac|dc   --trend / --interpret / --eink 按指定供电出数（默认 ac，AC/DC 不混算）
        """ + "\n";

#if PUSH_ENABLED
    private const string PushHelp =
        """
          --push          把快照推到极趣 Note 4（需要 API Key；两槽各推一张图，
                          槽 1 走近 30 天趋势图、槽 2 走自清灰以来平滑图；
                          加 --force 忽略当日幂等强制重推）
          --push --dry-run
                          推送全流程真走、只跳过 HTTP 发送（生成 PNG、写幂等状态与日志照旧），
                          用于验证流程 / 调度 / 幂等而不惊动设备
        """ + "\n";
#else
    private const string PushHelp = "";
#endif

    private const string Tail =
        """
          --dump <分钟>    前台运行采样并打印实时状态，用于验证程序是否工作
          --reset-clean-date <yyyy-MM-dd>   修正清灰基准日
        """;
}
