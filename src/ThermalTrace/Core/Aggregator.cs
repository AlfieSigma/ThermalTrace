using System.Globalization;
using ThermalTrace.Model;
using ThermalTrace.Storage;

namespace ThermalTrace.Core;

/// <summary>
/// 在线聚合引擎。这是整个程序的核心。
///
/// 职责：
///  1. 每收到一个采样点，按 (时间片, 负载档, 交流/电池) 累加到内存
///  2. 每 5 分钟把当前片状态刷进工作快照，启动时读回来接着累加
///  3. 时间片结束（默认 30 分钟）时，把结果落盘一行并删除快照
///
/// 【断电安全】最坏丢 5 分钟（快照间隔），不再是整个时间片。
/// 【内存上限】一个时间片最多 30min x 12次/min x 10 档，理论 3600 个 double，可忽略。
/// </summary>
internal sealed class Aggregator
{
    private readonly int _sliceMinutes;
    /// <summary>落盘目录。null = 正式数据目录；探针可指向临时目录，避免触碰真实数据。</summary>
    private readonly string? _dailyDir;
    private readonly Dictionary<(LoadBand Band, bool Ac), SliceAggregate> _current = new();
    private DateTimeOffset _sliceStart;
    private DateOnly _currentDay;
    private readonly HashSet<DateOnly> _flushedDays = new();

    public Aggregator(int sliceMinutes = 30, string? dailyDir = null)
    {
        _sliceMinutes = Math.Clamp(sliceMinutes, 5, 120);
        _dailyDir = dailyDir;
        _sliceStart = FloorToSlice(DateTimeOffset.Now);
        _currentDay = DateOnly.FromDateTime(_sliceStart.LocalDateTime);
        RestoreSnapshot();
    }

    /// <summary>时间片长度（分钟）。</summary>
    public int SliceMinutes => _sliceMinutes;

    /// <summary>当前时间片起点。</summary>
    public DateTimeOffset CurrentSliceStart => _sliceStart;

    /// <summary>已写入的行数（本次运行）。</summary>
    public int RowsWritten { get; private set; }

    /// <summary>启动时是否从快照恢复了当前片（供启动日志留痕）。</summary>
    public bool RestoredFromSnapshot { get; private set; }
    public int RestoredGroups { get; private set; }
    public int RestoredSamples { get; private set; }

    private DateTimeOffset FloorToSlice(DateTimeOffset t)
    {
        var mins = t.Minute - (t.Minute % _sliceMinutes);
        return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, mins, 0, t.Offset);
    }

    /// <summary>
    /// 启动恢复。落盘成功的路径会删快照，所以快照存在就意味着"这一片还没写进 CSV"。
    /// 片起点仍是当前片 → 接着累加；已是更早的片 → 启动时先补写再丢弃，
    /// 这样长时间停机、跨天、改过片粒度都不会把数据丢掉。
    /// </summary>
    private void RestoreSnapshot()
    {
        var snapshot = SliceSnapshotStore.Load();
        if (snapshot is null || snapshot.SliceMinutes != _sliceMinutes
            || snapshot.Aggregates.Count == 0)
        {
            SliceSnapshotStore.Delete();
            return;
        }

        DateTimeOffset savedStart;
        try { savedStart = SliceSnapshotStore.ParseStart(snapshot.SliceStart); }
        catch { SliceSnapshotStore.Delete(); return; }

        var nowSlice = FloorToSlice(DateTimeOffset.Now);
        if (savedStart == nowSlice)
        {
            foreach (var st in snapshot.Aggregates)
            {
                try
                {
                    var agg = SliceAggregate.FromState(st);
                    _current[(agg.Band, agg.OnAcPower)] = agg;
                }
                catch { /* 单组坏数据跳过，不拖垮启动 */ }
            }
            if (_current.Count == 0) { SliceSnapshotStore.Delete(); return; }
            _sliceStart = savedStart;
            _currentDay = DateOnly.FromDateTime(savedStart.LocalDateTime);
            RestoredFromSnapshot = true;
            RestoredGroups = _current.Count;
            RestoredSamples = _current.Values.Sum(a => a.Count);
            return;
        }

        if (savedStart < nowSlice)
        {
            var restored = new List<SliceAggregate>();
            foreach (var st in snapshot.Aggregates)
            {
                try { restored.Add(SliceAggregate.FromState(st)); }
                catch { /* 单组坏数据跳过 */ }
            }
            if (restored.Count > 0)
            {
                try
                {
                    var day = DateOnly.FromDateTime(savedStart.LocalDateTime);
                    var rows = DailyStore.WriteSlice(day, restored, _dailyDir);
                    RowsWritten += rows;
                    SamplerLog.Write("snapshot",
                        $"启动时补写未完成片 {savedStart:yyyy-MM-dd HH:mm}，{rows} 行");
                }
                catch (Exception ex)
                {
                    // 保留快照，下次启动再试；数据仍在文件里，不会丢。
                    SamplerLog.Write("snapshot", $"补写未完成片失败，保留快照: {ex.Message}");
                    return;
                }
            }
        }

        SliceSnapshotStore.Delete();
    }

    /// <summary>把当前片状态刷进快照。失败只留痕，不打断采样。</summary>
    public void SaveSnapshot()
    {
        try
        {
            var snapshot = new SliceSnapshot
            {
                SliceMinutes = _sliceMinutes,
                SliceStart = _sliceStart.ToString("o", CultureInfo.InvariantCulture)
            };
            foreach (var agg in _current.Values)
                snapshot.Aggregates.Add(agg.Export());
            SliceSnapshotStore.Save(snapshot);
        }
        catch (Exception ex)
        {
            SamplerLog.Write("snapshot", $"写入失败: {ex.Message}");
        }
    }

    /// <summary>累加一个采样点。到时间片边界时先落盘上一片。</summary>
    public bool Add(Sample s, bool onAcPower)
    {
        var now = s.Timestamp;
        var target = FloorToSlice(now);

        if (target != _sliceStart)
        {
            FlushCurrent();
            _sliceStart = target;
            _current.Clear();
        }

        var day = DateOnly.FromDateTime(now.LocalDateTime);
        if (day != _currentDay)
        {
            // 跨天：上一片属于旧的一天，Add 里已按 now 的日期归属，这里只切标记
            _currentDay = day;
        }

        // 主温度缺失的采样直接忽略，不进任何统计
        if (double.IsNaN(s.PrimaryCpuC)) return false;

        var band = LoadBandExtensions.FromMaxCoreUtil(s.MaxCoreUtilPct);
        var key = (band, onAcPower);

        if (!_current.TryGetValue(key, out var agg))
        {
            agg = new SliceAggregate { SliceStart = _sliceStart, Band = band, OnAcPower = onAcPower };
            _current[key] = agg;
        }
        agg.Add(s);
        return true;
    }

    /// <summary>
    /// 落盘当前时间片。顺序关键：先刷快照 → 写 CSV 成功 → 删快照。
    /// 写 CSV 失败时快照仍在，下次启动会补写，不会退回"整片丢失"。
    /// </summary>
    public int FlushCurrent()
    {
        if (_current.Count == 0) return 0;
        SaveSnapshot();
        var day = DateOnly.FromDateTime(_sliceStart.LocalDateTime);
        var rows = DailyStore.WriteSlice(day, _current.Values, _dailyDir);
        RowsWritten += rows;
        _current.Clear();
        SliceSnapshotStore.Delete();
        return rows;
    }

    /// <summary>
    /// 退出时只刷快照，不写 CSV。由下次启动接着累加、片边界一次性落盘，
    /// 避免"退出补写"被重启后的按片覆盖抹掉（见 [[done/slice_persistence_redesign_2026-11-06]]）。
    /// </summary>
    public void Shutdown() => SaveSnapshot();

    /// <summary>
    /// 某天的空闲基线 —— 用作环境温度代理（空闲时 CPU 温度趋近环境温度）。
    /// 趋势分析用它做 ΔT 归一化，抵消季节与室温变化。
    ///
    /// 【兜底】当天没有 0-10 档数据时（真实使用中单核峰值可能长期高于 10%），
    /// 退而取当天出现的最低档最低温，并标记 Approximated——
    /// 报告需明确提示该日 ΔT 为近似口径。
    /// </summary>
    public static (double Baseline, bool Approximated, string Band) DailyIdleBaseline(
        IEnumerable<SliceRow> rows, DateOnly day)
    {
        var list = rows as IReadOnlyList<SliceRow> ?? rows.ToList();

        var idleMin = list
            .Where(r => r.Date == day
                        && r.BandKey == LoadBand.B00_10.Key()
                        && !double.IsNaN(r.TempMin))
            .Select(r => r.TempMin)
            .ToList();
        if (idleMin.Count > 0)
            return (idleMin.Min(), false, LoadBand.B00_10.Key());

        var lowest = list
            .Where(r => r.Date == day && !double.IsNaN(r.TempMin))
            .GroupBy(r => r.BandKey)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (lowest is null) return (double.NaN, false, "");
        return (lowest.Select(r => r.TempMin).Min(), true, lowest.Key);
    }
}
