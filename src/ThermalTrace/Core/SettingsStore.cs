using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThermalTrace.Model;

namespace ThermalTrace.Core;

/// <summary>settings.json / sessions.json 的读写。配置损坏时自动回退到默认值而不是崩溃。</summary>
internal static class JsonStore
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 中文直接以可读字符写入，避免 \uXXXX 让人无法手工编辑
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>进程内保存串行化；跨进程靠唯一临时文件名，不靠这把锁。</summary>
    private static readonly object SaveSync = new();

    public static T Load<T>(string path, Func<T> fallback) where T : class
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return fallback();
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback();
        }
        catch (Exception ex)
        {
            var backup = path + ".corrupt";
            TryBackup(path, backup);
            ConsoleHost.WriteError($"[warn] 配置文件解析失败，已备份为 {backup} 并使用默认值: {ex.Message}");
            return fallback();
        }
    }

    /// <summary>
    /// 原子写盘：先写临时文件再整体替换。
    /// 临时文件名带唯一后缀——GUI 与后台线程、或同时跑着的命令行可能保存同一份配置，
    /// 固定 <c>path + ".tmp"</c> 会让两边撞在同一个文件上（写一半 / 替换失败）。
    /// 进程内再加一把锁，避免同一进程里的并发保存互相踩。
    /// </summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, Options);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";

        lock (SaveSync)
        {
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // 失败时不要留下半截临时文件
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* noop */ }
                throw;
            }
        }
    }

    private static void TryBackup(string src, string dst)
    {
        try { File.Copy(src, dst, overwrite: true); } catch { /* noop */ }
    }
}

internal static class SettingsStore
{
    public static AppSettings Load() => JsonStore.Load(AppPaths.SettingsFile, () => new AppSettings());

    public static void Save(AppSettings settings)
    {
        settings.Sensors.DisplayNames ??= new Dictionary<string, string>(StringComparer.Ordinal);
        JsonStore.Save(AppPaths.SettingsFile, settings);
    }

    /// <summary>首次运行时写入清灰基准日（今天）。之后只允许通过 --reset-clean-date 显式修改。</summary>
        public static string EnsureCleanDate(AppSettings settings)
        {
            if (settings.CleanDate is { } existing)
                return existing.ToString("yyyy-MM-dd");

            var today = DateOnly.FromDateTime(DateTime.Today);
            settings.CleanDateIso = today.ToString("yyyy-MM-dd");
            Save(settings);
            return settings.CleanDateIso;
        }

        /// <summary>把自动定位到的经纬度写回配置（AmbientOptions 是 record，需整体替换）。</summary>
        public static void UpdateAmbientLocation(AmbientWeather.Geo geo)
        {
            var s = Load();
            s.Ambient = s.Ambient with
            {
                Latitude = geo.Latitude,
                Longitude = geo.Longitude,
                LocationName = geo.Name
            };
            Save(s);
        }
    }