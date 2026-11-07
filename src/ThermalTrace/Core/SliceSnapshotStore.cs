using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThermalTrace.Model;

namespace ThermalTrace.Core;

/// <summary>
/// 工作快照的读写。写走"临时文件 + 原子替换"，进程在写一半时崩溃不会留下半截 JSON。
/// 读失败一律当"没有快照"，坏文件由 Aggregator 启动时清掉。
/// </summary>
internal static class SliceSnapshotStore
{
    // 温度缺失用 NaN 表示，默认序列化器不接受非有限数，必须显式允许。
    private static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static string FilePath => AppPaths.SliceSnapshotFile;

    public static SliceSnapshot? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var snapshot = JsonSerializer.Deserialize<SliceSnapshot>(File.ReadAllText(FilePath), Options);
            return snapshot is { Version: SliceSnapshot.CurrentVersion } ? snapshot : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(SliceSnapshot snapshot)
    {
        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Options), new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* 临时文件清理失败不影响快照本身 */ }
        }
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* 删除失败下次启动会再试，不影响采样 */ }
    }

    public static DateTimeOffset ParseStart(string text) =>
        DateTimeOffset.ParseExact(text, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
