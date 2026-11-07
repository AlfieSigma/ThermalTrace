using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace ThermalTrace.Hardware;

/// <summary>
/// 系统与环境信息探针。补充 LHM 读不到的部分：管理员状态、电源方案、
/// 交流电连接、电池电量、BIOS 版本、物理核数，以及是否有第三方监控程序在争抢 MSR。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SystemProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;       // 0=离线(DC) 1=在线(AC) 255=未知
        public byte BatteryFlag;
        public byte BatteryLifePercent; // 0~100，255=未知
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static (bool OnAc, double BatteryPct) ReadPowerState()
    {
        try
        {
            if (GetSystemPowerStatus(out var s))
            {
                var battery = s.BatteryLifePercent == 255 ? double.NaN : s.BatteryLifePercent;
                return (s.ACLineStatus == 1, battery);
            }
        }
        catch
        {
            // 某些精简系统上该 API 不可用，退化为"假设在 AC"
        }
        return (true, double.NaN);
    }

    /// <summary>当前电源方案。不同方案下 CPU 的 PL 与频率策略不同，必须记录。</summary>
    public static string ReadPowerScheme()
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };
            p.Start();
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { /* noop */ } }
            const string marker = "Power Scheme GUID:";
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return idx < 0 ? text.Trim() : text[(idx + marker.Length)..].Trim();
        }
        catch
        {
            return "(未知)";
        }
    }

    /// <summary>
    /// 只读 CPU 型号名 —— 走注册表，毫秒级。
    ///
    /// 【为什么单独开这一个方法】WMI 查一次 Win32_Processor 实测 **1532 ms**，
    /// 而 <see cref="ReadCpuIdentity"/> 是在采样服务<b>启动路径上同步</b>调用的
    /// （FingerprintStore 登记口径指纹），托盘图标要等它查完才出现。
    /// 核数不是启动路径必需的信息，仍留在 <see cref="ReadCpuIdentity"/> 里给自检/诊断用。
    ///
    /// 【兜底】注册表读不到（精简系统 / 权限异常 / 键缺失）就退回 WMI 那条路，
    /// 绝不为了省 1.5 秒而拿不到 CPU 型号 —— 型号是口径指纹的一部分，
    /// 丢了会让趋势页把"换了 CPU"误判成同一台机器。
    /// </summary>
    public static string ReadCpuName()
    {
        try
        {
            const string key = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
            if (Registry.GetValue(key, "ProcessorNameString", null) is string s
                && !string.IsNullOrWhiteSpace(s))
                return s.Trim();
        }
        catch
        {
            // 注册表不可读不值得打断启动，退回 WMI
        }
        return ReadCpuIdentity().CpuName;
    }

    public static (string CpuName, int PhysicalCores, int LogicalCores) ReadCpuIdentity()
    {
        string name = "未知 CPU";
        int physical = 0;
        int logical = Environment.ProcessorCount;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                name = obj["Name"]?.ToString()?.Trim() ?? name;
                physical = Convert.ToInt32(obj["NumberOfCores"]);
                logical = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
                break;
            }
        }
        catch
        {
            // WMI 不可用时退回估算
        }
        if (physical <= 0) physical = Math.Max(1, Environment.ProcessorCount / 2);
        return (name, physical, logical);
    }

    public static string ReadBiosVersion()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            foreach (ManagementObject obj in searcher.Get())
                return obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "未知";
        }
        catch
        {
            // noop
        }
        return "未知";
    }

    /// <summary>
    /// 探测正在运行的第三方硬件监控程序。它们会争抢 MSR 独占访问，
    /// 造成本程序读数间歇为空或跳变——这是"数据显示很怪"的常见原因。
    /// </summary>
    public static IReadOnlyList<string> DetectForeignMonitors()
    {
        string[] suspects =
        [
            "LibreHardwareMonitor", "OpenHardwareMonitor", "AIDA64", "LiteMonitor",
            "HWiNFO", "RTSS", "FanControl", "IntelXTU", "Intel Extreme Tuning",
            "DragonShare", "ThrottleStop"
        ];

        var found = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var pn = p.ProcessName;
                foreach (var s in suspects)
                    if (pn.Contains(s, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!found.Contains(s)) found.Add(s);
                        break;
                    }
            }
            catch
            {
                // 访问受保护进程会抛异常，忽略
            }
            finally
            {
                try { p.Dispose(); } catch { /* noop */ }
            }
        }
        return found;
    }

    public static HardwareProbeInfo Probe(Computer? computer)
    {
        var (cpuName, physical, logical) = ReadCpuIdentity();
        var gpus = new List<string>();
        int sensorCount = 0;
        bool canReadCpu = false;

        try
        {
            if (computer is not null)
            {
                foreach (var hw in computer.Hardware)
                {
                    // 【S1踩坑】Sensors 是 IEnumerable<ISensor>，没有 Count 属性，必须用 Count()
                    sensorCount += hw.Sensors.Count();
                    // 【S1踩坑】HardwareType 枚举里没有 Gpu 成员（Gpu 会编译失败），
                    // 只能用 ToString() 做大小写不敏感比较
                    var ht = hw.HardwareType.ToString();
                    // 【实测】显卡节点是 "GpuIntel" / "GpuNvidia"，没有裸 "gpu" 这个取值，
                    // 因此必须前缀匹配，否则显卡会整体漏掉（曾导致 GPU 显示为"未识别"）
                    if (ht.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase)
                        || ht.StartsWith("Tpu", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!gpus.Contains(hw.Name)) gpus.Add(hw.Name);
                    }
                    if (hw.HardwareType == HardwareType.Cpu) canReadCpu = true;
                }
            }
        }
        catch
        {
            // 枚举失败不阻断 Probe
        }

        return new HardwareProbeInfo(
            CpuName: cpuName,
            LogicalCores: logical,
            PhysicalCores: physical,
            BiosVersion: ReadBiosVersion(),
            GpuList: gpus.Count > 0 ? string.Join(" + ", gpus) : "(未识别)",
            TotalSensorCount: sensorCount,
            CanReadMsr: canReadCpu);
    }
}