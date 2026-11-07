using System.Security.Cryptography;
using System.Text;
using ThermalTrace.Core;
using ThermalTrace.Hardware;
using ThermalTrace.Model;

namespace ThermalTrace.Analysis;

/// <summary>
/// 硬件指纹 / 传感器绑定口径的历史记录（设计③）。
///
/// 趋势可比的前提是"口径不变"：换显卡驱动、重新执行 --bind、LHM 版本变化，
/// 都可能让同一个温度数字来自不同的传感器。本类把每次出现的口径固化到
/// caliber.json，报告生成时据此判断对比窗口是否跨了口径——
/// 跨口径的温差结论无意义，必须在报告里明确提示"数据口径已变"。
/// </summary>
    /// <summary>某一时刻生效的硬件口径。</summary>
    public sealed record CaliberRecord(
        string FirstSeenIso,
        string Hash,
        string Cpu,
        string PrimaryTempId,
        string PrimaryTempName,
        string PowerId,
        string CoreMaxUtilId,
        string FanId)
    {
        public DateTimeOffset FirstSeen =>
            DateTimeOffset.TryParse(FirstSeenIso, out var d) ? d : DateTimeOffset.MinValue;
    }

internal static class FingerprintStore
{
    /// <summary>
    /// 计算当前口径的 12 位指纹。参与哈希的全是"决定温度数字含义"的字段：
    /// CPU 型号 + 主温度链路三个 ID + 功耗 + 单核峰值 + 风扇。
    /// 显示名变化（不影响口径）不参与。
    /// </summary>
    public static string ComputeHash(AppSettings settings, string cpuName)
    {
        var b = settings.Sensors;
        var canonical = string.Join('|',
            cpuName,
            b.CpuPackageId, b.CpuTdieId, b.CpuCoreMaxId,
            b.CpuPackagePowerId, b.CpuCoreMaxUtilId, b.FanId);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes)[..12];
    }

    /// <summary>
    /// 登记当前口径。新指纹出现则追加一条历史；已存在则原样返回。
    /// 每次采样服务启动时调用一次。
    /// </summary>
    public static CaliberRecord Record(AppSettings settings)
    {
        var cpuName = CpuNameCache.Value;
        var hash = ComputeHash(settings, cpuName);
        var list = Load();

        if (list.FirstOrDefault(r => r.Hash == hash) is { } existing)
            return existing;

        var b = settings.Sensors;
        var primaryId = b.PrimaryTemperatureChain.FirstOrDefault() ?? string.Empty;
        var rec = new CaliberRecord(
            FirstSeenIso: DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm"),
            Hash: hash,
            Cpu: cpuName,
            PrimaryTempId: primaryId,
            PrimaryTempName: b.PrimarySensorName,
            PowerId: b.CpuPackagePowerId,
            CoreMaxUtilId: b.CpuCoreMaxUtilId,
            FanId: b.FanId);

        list.Add(rec);
        list.Sort((a, b2) => a.FirstSeen.CompareTo(b2.FirstSeen));
        JsonStore.Save(AppPaths.CaliberFile, list);
        return rec;
    }

    public static List<CaliberRecord> Load()
    {
        var list = JsonStore.Load(AppPaths.CaliberFile, () => new List<CaliberRecord>());
        list.Sort((a, b) => a.FirstSeen.CompareTo(b.FirstSeen));
        return list;
    }

    /// <summary>某日生效的口径（该日之前最近登记的一条）；无记录返回 null。</summary>
    public static CaliberRecord? ActiveAt(DateOnly date) => ActiveAt(Load(), date);

    /// <summary>
    /// 在已加载的口径列表里查某日生效的一条。
    ///
    /// 【为什么要有批量入口】趋势报告的口径校验要对窗口内每一天调用一次，
    /// 旧实现每次 <see cref="Load"/> 都读一遍 JSON、反序列化并排序。
    /// <paramref name="records"/> 必须按 <see cref="FirstSeen"/> 升序，
    /// <see cref="Load"/> 的返回值满足该约定；查找用二分，语义与逐条扫描一致。
    /// </summary>
    public static CaliberRecord? ActiveAt(IReadOnlyList<CaliberRecord> records, DateOnly date)
    {
        var dayStart = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.FromHours(8));
        var cutoff = dayStart.AddDays(1);

        // 找最后一条 FirstSeen <= cutoff 的记录（原实现的循环扫描等价于这个位置）
        var lo = 0;
        var hi = records.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (records[mid].FirstSeen <= cutoff)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found >= 0 ? records[found] : null;
    }

    /// <summary>
    /// CPU 名只在进程内取一次并缓存（口径指纹每次启动都要用）。
    ///
    /// 【2026-10-13 启动提速】原先走 WMI，启动要同步阻塞约 1.5 秒（实测 1532 ms）；
    /// 改走注册表路径（14 ms）。核数不需要，WMI 那套留给自检与诊断。
    /// 注册表读不到时 <see cref="SystemProbe.ReadCpuName"/> 内部会自动退回 WMI。
    /// </summary>
    private static readonly Lazy<string> CpuNameCache = new(() =>
    {
        try { return SystemProbe.ReadCpuName(); }
        catch { return "未知 CPU"; }
    });
}
