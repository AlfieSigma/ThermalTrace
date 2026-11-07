using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ThermalTrace.Core;

namespace ThermalTrace.Core;

/// <summary>
/// 从本地缓存读取环境气温。离线可用；缺失时给出明确原因，供报告标注。
///
/// 【缓存策略 v2（2026-10-19 修复）】
/// 旧实现把 CSV 读进静态字典后整个进程只加载一次（_loaded）。而气温文件由
/// <c>RefreshWeatherAsync</c> 异步落盘，今天这一天的行往往是"晚于首次读取"才写进文件的，
/// 导致今天整会话显示"环境温度 不可用"（磁盘已有数据却读不到，重启才恢复）。
///
/// 新实现改为按文件 mtime 惰性重载：读取时比对 <c>weather\</c> 目录下
/// <c>ambient-*.csv</c> 的最新修改时间，发现目录出现 / 消失或任一文件被改写就重建缓存。
/// 这样无论迟到写入来自本进程 fetch、手动编辑还是目录后建，下一次读取都会自动吸收，
/// 不再依赖跨模块信号，也无需重启。比对目录的频次由下面的扫描节流决定。
///
/// 读取加锁：fetch 在采样线程写文件、GUI 在 UI 线程读，必须互斥。
///
/// 【2026-11-07 开窗提速 A 项：扫描节流】
/// <c>At()</c> 每行调用一次，一份趋势报告就是数千次；旧实现每次都枚举 <c>weather\</c>
/// 目录并取文件时间，三份报告叠起来是开窗的主要热点。
/// 现改为：目录扫描结果在 <see cref="ScanInterval"/> 内复用，不再逐行枚举；
/// 本进程落盘后由 <see cref="Invalidate"/> 显式失效，下一次读取立即吸收；
/// 外部改写最多等一个节流周期（5 秒，远小于验收上限 60 秒）后被扫描发现。
/// 数值语义不变：重建条件仍是"目录出现/消失或最新 mtime 前进"。
/// </summary>
internal static class AmbientLookup
{
    /// <summary>两次目录扫描的最小间隔。取值只影响外部改写的发现延迟，不影响本进程落盘。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    private static readonly object _gate = new();
    private static readonly Dictionary<string, double> Cache = new(StringComparer.Ordinal);
    private static DateTime _cacheStamp = DateTime.MinValue; // 见过的最新 mtime（UTC）
    private static bool _initialized;
    private static bool _prevDirExisted;
    private static DateTime _nextScanUtc = DateTime.MinValue;
    private static bool _invalidated = true;   // 首次读取必须扫描
    private static int _invalidations;         // 扫描期间被失效时据此丢弃本轮结果

    /// <summary>
    /// 显式失效：本进程（采样线程 <c>RefreshWeatherAsync</c>）落盘后调用，
    /// 让下一次 <see cref="At"/> 立即重扫，不用等节流窗口。
    /// </summary>
    public static void Invalidate()
    {
        lock (_gate)
        {
            _invalidated = true;
            _invalidations++;
        }
    }

    private static void EnsureLoaded()
    {
        int version;
        lock (_gate)
        {
            // 节流窗口内且没有显式失效 → 直接用现有缓存，不碰磁盘
            if (_initialized && !_invalidated && DateTime.UtcNow < _nextScanUtc) return;
            version = _invalidations;
        }

        var dirExists = Directory.Exists(AppPaths.WeatherDir);
        var files = dirExists
            ? Directory.EnumerateFiles(AppPaths.WeatherDir, "ambient-*.csv").ToList()
            : new List<string>();
        var newest = files.Count == 0
            ? DateTime.MinValue
            : files.Max(f => File.GetLastWriteTimeUtc(f));

        lock (_gate)
        {
            // 扫描期间又被显式失效：本轮结果可能不含新写入，丢弃，保持待扫状态
            if (version != _invalidations) return;

            _nextScanUtc = DateTime.UtcNow + ScanInterval;

            // 未初始化 / 目录出现或消失 / 有文件被改写 → 重建
            if (!_initialized || dirExists != _prevDirExisted || newest > _cacheStamp)
            {
                Cache.Clear();
                foreach (var file in files)
                {
                    try
                    {
                        foreach (var line in File.ReadLines(file, System.Text.Encoding.UTF8))
                        {
                            if (line.StartsWith("datetime", StringComparison.OrdinalIgnoreCase)) continue;
                            var p = line.Split(',');
                            if (p.Length < 2) continue;
                            if (!DateTime.TryParseExact(p[0].Trim(), "yyyy-MM-dd HH:mm",
                                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) continue;
                            if (!double.TryParse(p[1], NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out var celsius)) continue;
                            Cache[dt.ToString("yyyy-MM-dd HH")] = celsius;
                        }
                    }
                    catch
                    {
                        // 单个文件损坏 / 读取中途被 fetch 重写：跳过该文件，不阻断
                    }
                }
                _cacheStamp = newest;
                _prevDirExisted = dirExists;
                _initialized = true;
            }
            _invalidated = false;
        }
    }

    /// <summary>取某时刻的环境气温（℃）。缺失返回 NaN。</summary>
    public static double At(DateTimeOffset when)
    {
        EnsureLoaded();
        lock (_gate)
        {
            // 缓存按 Open-Meteo timezone=auto 返回的地点本地墙钟逐字存储（见 AmbientWeather.FetchAsync），
            // 检索键改用本机系统时区换算，不再假定东八区；本机为 UTC+8 时与旧版固定 +8 完全一致。
            // 已知边界：若机器时区与坐标所在地时区不同，仍会有整小时偏移（本项不改缓存格式）。
            var local = TimeZoneInfo.ConvertTime(when, TimeZoneInfo.Local);
            // 逐小时数据：取整点；缺失则向最近的前后各找 1 小时
            var key = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0)
                .ToString("yyyy-MM-dd HH");
            if (Cache.TryGetValue(key, out var v)) return v;

            for (int dh = 1; dh <= 2; dh++)
            {
                if (Cache.TryGetValue(local.AddHours(-dh).ToString("yyyy-MM-dd HH"), out v)) return v;
                if (Cache.TryGetValue(local.AddHours(dh).ToString("yyyy-MM-dd HH"), out v)) return v;
            }
            return double.NaN;
        }
    }

    /// <summary>
    /// 某日期在缓存里已覆盖的整点数量（0–24）。联网回补前据此判断缺口：
    /// 全窗口都满 24 时不再发起请求，只补最早缺口日到今天这一段。
    /// 缓存键为 <c>yyyy-MM-dd HH</c>，同一天的不同整点各占一个键。
    /// </summary>
    public static int HoursCoveredOn(DateOnly day)
    {
        EnsureLoaded();
        var prefix = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ";
        lock (_gate)
        {
            var count = 0;
            foreach (var key in Cache.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal)) count++;
            return count;
        }
    }

    /// <summary>当日环境气温是否可用。</summary>
    public static bool HasData
    {
        get { EnsureLoaded(); lock (_gate) return Cache.Count > 0; }
    }

    public static int CachedHours
    {
        get { EnsureLoaded(); lock (_gate) return Cache.Count; }
    }
}
