using System.Runtime.InteropServices;
using System.Text;

namespace ThermalTrace.Core;

/// <summary>
/// OutputType=WinExe 不会自动分配控制台，需要手动接管标准句柄。
/// 目标是同时正确处理三种启动方式：
///   a) 双击运行           -> 无控制台，需要 AllocConsole
///   b) 管理员 PowerShell -> 控制台已存在，Attach 到父进程即可
///   c) 被重定向管道/文件  -> 句柄已就绪，<b>绝不能</b>再Attach/Alloc
///
/// (c) 是最容易踩的坑：某些启动器（IDE 任务、计划任务、日志收集器）会以重定向方式拉起进程。
/// 此时若盲目 AllocConsole，输出会被导到一个谁也看不见的新控制台窗口里，
/// 表现为"程序跑完了但什么都没有"。因此必须先检测重定向。
/// </summary>
internal static class ConsoleHost
{
    private const int AttachParentProcess = -1;
    private const uint StdOutHandle = 0xFFFFFFF5; // (uint)-11
    private const uint StdInHandle = 0xFFFFFFF6;  // (uint)-10
    private const int InvalidHandleValue = -1;

    private const uint FileTypeUnknown = 0x0000;
    private const uint FileTypeDisk = 0x0001;
    private const uint FileTypeChar = 0x0002;
    private const uint FileTypePipe = 0x0003;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleOutputCP();

    private static bool _initialized;
    private static bool _ownsConsole;

    /// <summary>幂等。返回 true 表示后续 Console 写入有有效去处。</summary>
    public static bool Ensure()
    {
        if (_initialized) return HasUsableHandle(StdOutHandle);
        _initialized = true;

        if (IsRedirected(StdOutHandle))
        {
            // 句柄已由父进程重定向，直接使用，不要 Attach/Alloc
            _ownsConsole = false;
        }
        else if (AttachConsole(AttachParentProcess))
        {
            _ownsConsole = false;
        }
        else if (AllocConsole())
        {
            _ownsConsole = true;
        }

        RebindStreams();
        return HasUsableHandle(StdOutHandle);
    }

    /// <summary>标准句柄是否已被重定向到管道或文件（而非控制台）。</summary>
    private static bool IsRedirected(uint stdHandle)
    {
        var type = GetFileType(GetStdHandleSafe(stdHandle));
        return type is FileTypePipe or FileTypeDisk;
    }

    private static IntPtr GetStdHandleSafe(uint stdHandle)
    {
        var h = GetStdHandle(stdHandle);
        return h == new IntPtr(InvalidHandleValue) ? IntPtr.Zero : h;
    }

    private static bool HasUsableHandle(uint stdHandle)
    {
        var h = GetStdHandleSafe(stdHandle);
        return h != IntPtr.Zero && GetFileType(h) != FileTypeUnknown;
    }

    /// <summary>
    /// 显式重绑 .NET 的 Console 流。
    /// .NET 在首次访问 Console 时缓存句柄；Ensure() 必须先于任何 Console 调用执行，
    /// 这里再绑一次以确保拿到 Attach 之后的有效句柄。
    ///
    /// 【S1踩坑】中文乱码：控制台代码页通常是 936(GBK)，而 .NET 默认按UTF-8 输出，
    /// 结果就是"姝ｅ湪杩炵画閲囨牱"这种乱码。必须把输出编码对齐到控制台实际代码页。
    /// 但当输出被重定向到管道/文件时应保持 UTF-8，否则写出的文件在别的工具里也是乱码。
    ///
    /// 【2026-10-09 二次修复】此前用 GetFileType(GetStdHandle) 判定是否控制台，
    /// 在 AllocConsole 新建窗口的场景下句柄判定不可靠，导致仍按 UTF-8 输出而乱码
    /// （双击运行时看到 "ThermalTrace 鍚庡彴閲囨牱宸插惎鍔?"）。
    /// 改用 .NET 的 Console.IsOutputRedirected 判定，并同步设置 Console.OutputEncoding。
    /// </summary>
    private static void RebindStreams()
    {
        try
        {
            var enc = ResolveOutputEncoding();
            Console.OutputEncoding = enc;

            var stdout = GetStdHandleSafe(StdOutHandle);
            if (stdout != IntPtr.Zero)
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true });

            var stdin = GetStdHandleSafe(StdInHandle);
            if (stdin != IntPtr.Zero)
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), enc));
        }
        catch
        {
            // 控制台不可用（例如计划任务以完全隐藏方式启动），后续写入静默丢弃
        }
    }

    /// <summary>输出到真实控制台时跟随其代码页；重定向时固定 UTF-8。</summary>
    private static Encoding ResolveOutputEncoding()
    {
        // 用 .NET 的判定而非手工 GetFileType：在 AllocConsole 新建窗口场景下更可靠
        if (!Console.IsOutputRedirected)
        {
            var cp = GetConsoleOutputCP();
            if (cp != 0)
            {
                try
                {
                    // 部分代码页（如 936）无法映射所有 Unicode 字符，回退时用替换符而不是抛异常
                    return Encoding.GetEncoding((int)cp, EncoderFallback.ReplacementFallback,
                                                DecoderFallback.ReplacementFallback);
                }
                catch (Exception)
                {
                    // 该代码页未安装对应编码器，退回 UTF-8
                }
            }
        }
        return new UTF8Encoding(false);
    }

    /// <summary>是否可以与用户交互（需要读取输入，例如绑定向导）。</summary>
    public static bool IsInteractive =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected && HasUsableHandle(StdOutHandle);

    public static void Write(string text)
    {
        try { Console.Out.Write(text); }
        catch { /* 无输出去处时静默 */ }
    }

    public static void WriteLine(string text = "")
    {
        try { Console.Out.WriteLine(text); }
        catch { /* 无输出去处时静默 */ }
    }

    public static void WriteError(string text)
    {
        try { Console.Error.WriteLine(text); }
        catch { /* noop */ }
    }

    /// <summary>
    /// 托盘模式（S6）调用：释放控制台，避免任务栏残留空窗口。
    /// 只释放自己申请来的控制台，不动父进程继承来的。
    /// </summary>
    public static void ReleaseIfOwned()
    {
        if (!_ownsConsole) return;
        try { FreeConsole(); }
        catch { /* noop */ }
        finally { _ownsConsole = false; }
    }
}