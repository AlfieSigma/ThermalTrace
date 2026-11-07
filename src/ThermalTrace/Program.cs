using ThermalTrace.Core;

namespace ThermalTrace;

/// <summary>
/// 入口。GUI 主导的设计：无参数运行即启动后台常驻采样 + 托盘图标。
/// 用户不需要接触任何命令行。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ConsoleHost.Ensure();

        ParsedCommand cmd;
        try
        {
            cmd = CommandLine.Parse(args);
        }
        catch (CommandLineException ex)
        {
            ConsoleHost.WriteError($"[错误] {ex.Message}");
            ConsoleHost.WriteLine(HelpText.Text);
            return 2;
        }

        try
        {
            AppPaths.EnsureAll();

            // 每次启动都留一条可观测记录：开机时间、是否提权、exe 路径。
            // "程序到底起来过没有"以前只能靠 CSV 时间戳反推，现在直接看 logs\boot.txt。
            BootLog.Record(args);

            if (cmd.ResetCleanDate is { Length: > 0 } iso)
                return Commands.ResetCleanDate(iso);

            return cmd.Kind switch
            {
                CommandKind.Install => AutoStartCommands.Install(),
                CommandKind.Uninstall => AutoStartCommands.Uninstall(),
                CommandKind.SelfCheck => Commands.SelfCheck(),
                CommandKind.Bind => Commands.Bind(),
                CommandKind.Today => Commands.Today(),
                CommandKind.Trend => Commands.Trend(!cmd.NoOpen, cmd.OnAc),
                CommandKind.Interpret => Commands.Interpret(cmd.OnAc),
                CommandKind.Eink => Commands.Eink(!cmd.NoOpen, cmd.Variant, cmd.Slot, cmd.OnAc),
#if PUSH_ENABLED
                CommandKind.Push => Commands.Push(cmd.Force, cmd.DryRun),
#endif
                CommandKind.Dump => Commands.Dump(cmd),
                CommandKind.Status => AutoStartCommands.Status(),
                _ => Commands.Background()
            };
        }
        catch (UnauthorizedAccessException)
        {
            ConsoleHost.WriteError("[错误] 权限不足。读取 CPU MSR 需要管理员权限。");
            return 740;
        }
        catch (Exception ex)
        {
            ConsoleHost.WriteError($"[异常] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
