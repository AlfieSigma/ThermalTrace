using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using ThermalTrace.Core;

namespace ThermalTrace.Core;

/// <summary>
/// 联网环境气温（Open-Meteo，免费、无需 API key）。
///
/// 【为什么联网而不是用主板温度】
/// 实测环境中没有主板温度传感器，无法用"主板最低温≈环境温度"做代理。
/// 而跨月/跨季做趋势分析时，环境温度是最大的干扰因素——
/// 夏 35℃ 与冬 20℃ 的绝对温度完全不可比。必须用ΔT 归一化才能得到可信趋势。
///
/// 【缓存策略】
/// 逐小时气温拉到本地 weather\ambient-YYYY-MM.csv，之后离线可用；
/// 且支持按日期区间回补之前缺失的环境温度。
///
/// 【2026-11-07 G10：只补缺口】
/// 旧实现每 30 分钟把 past_days 窗口整段重拉一次：已有数据也照拉，月份 CSV 每次都整读整写。
/// 现改成先查 <see cref="AmbientLookup.HoursCoveredOn"/>，找出窗口内最早未满 24 小时的日期，
/// 只请求 [该日, 今天] 这一段；窗口已完整覆盖时直接返回，零网络请求、零写盘。
/// 缺口补齐的数据仍来自同一个 forecast 端点，数值口径不变。
///
/// 【时间口径】
/// 请求带 timezone=auto，返回的时间串是坐标所在地的本地墙钟；下面按字符串逐字落盘
/// （<see cref="DateTime.TryParse"/> 在无时区信息时不转换，保持原值）。
/// 因此缓存键是“地点本地时间”，读取侧按本机系统时区换算（见 <see cref="AmbientLookup.At"/>）。
/// 系统时区为 UTC+8 时两者一致；本项不改动历史缓存格式。
///
/// 【降级链】
/// 联网气温 -> 当天空闲档最低温(+5℃ 修正)代理（今日视图在联网缺失时回退到此，不再直接显示不可用）
/// -> 仅当连代理也缺（当天完全无数据）时才标注"环境温度不可用"。
/// 任何网络失败都绝不影响主流程（采样与聚合不依赖网络）。
/// </summary>
internal static class AmbientWeather
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public sealed record Geo(double Latitude, double Longitude, string Name);

    /// <summary>通过 IP 粗定位（一次性，结果存进设置）。失败返回 null 让用户手填。</summary>
    public static async Task<Geo?> LocateAsync(CancellationToken ct = default)
    {
        try
        {
            // 【2026-10-09 实机修复】免费版仅 HTTP 端点可用：
            // HTTPS 端点返回 "SSL unavailable for this endpoint, order a key"（需付费 key）。
            // HTTP 端点实测国内可达、无需 key，返回格式与解析代码完全兼容。
            using var resp = await Http.GetAsync("http://ip-api.com/json/", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var st) && st.GetString() != "success")
                return null;
            return new Geo(
                root.GetProperty("lat").GetDouble(),
                root.GetProperty("lon").GetDouble(),
                root.TryGetProperty("city", out var city) ? city.GetString() ?? "未知" : "未知");
        }
        catch
        {
            return null;   // 定位失败不是错误，用户可手填
        }
    }

    /// <summary>拉取逐小时气温并写入本地缓存。返回写入条数，失败返回 0。</summary>
    public static async Task<int> FetchAsync(Geo geo, int pastDays, CancellationToken ct = default)
    {
        try
        {
            var days = Math.Clamp(pastDays, 0, 92);
            var today = DateOnly.FromDateTime(DateTime.Now);
            var windowStart = today.AddDays(-days);

            // 只请求缺失区间：窗口内最早一天未满 24 整点的日期起，到今天为止。
            // 全部齐备说明没有缺口，连请求都不发（旧实现此时仍会整段重拉一次）。
            DateOnly? firstMissing = null;
            for (var d = windowStart; d <= today; d = d.AddDays(1))
            {
                if (AmbientLookup.HoursCoveredOn(d) < 24)
                {
                    firstMissing = d;
                    break;
                }
            }
            if (firstMissing is null) return 0;

            var url = "https://api.open-meteo.com/v1/forecast"
                      + $"?latitude={geo.Latitude:F4}&longitude={geo.Longitude:F4}"
                      + "&hourly=temperature_2m"
                      + $"&start_date={firstMissing.Value:yyyy-MM-dd}&end_date={today:yyyy-MM-dd}"
                      + "&timezone=auto";

            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return 0;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("hourly", out var hourly)) return 0;

            var times = hourly.GetProperty("time").EnumerateArray().Select(x => x.GetString()!).ToList();
            var temps = hourly.GetProperty("temperature_2m").EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.Null ? double.NaN : x.GetDouble()).ToList();

            Directory.CreateDirectory(AppPaths.WeatherDir);
            var c = CultureInfo.InvariantCulture;
            var byMonth = new Dictionary<string, List<string>>();

            for (int i = 0; i < Math.Min(times.Count, temps.Count); i++)
            {
                if (double.IsNaN(temps[i])) continue;
                if (!DateTime.TryParse(times[i], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out var dt)) continue;
                var key = dt.ToString("yyyy-MM", c);
                if (!byMonth.TryGetValue(key, out var lines))
                    byMonth[key] = lines = new List<string>();
                lines.Add($"{dt:yyyy-MM-dd HH:00},{temps[i]:0.0}");
            }

            var written = 0;
            foreach (var (key, lines) in byMonth)
            {
                var path = Path.Combine(AppPaths.WeatherDir, $"ambient-{key}.csv");
                var header = "datetime,celsius\n";
                var existing = File.Exists(path) ? File.ReadAllText(path) : header;
                // 幂等：同一时间点只保留一条
                var set = new HashSet<string>(existing.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                var before = set.Count;
                foreach (var l in lines) set.Add(l);
                if (set.Count == before && existing.Length > 0) continue;
                File.WriteAllText(path, string.Join("\n", set) + "\n", new System.Text.UTF8Encoding(true));
                written += set.Count - before;
            }
            // 本进程刚改写了气温文件：让读取缓存的扫描节流立即失效，
            // 保证下一次 At() 马上吸收（不必等 5 秒节流窗口）。
            if (written > 0) AmbientLookup.Invalidate();
            return written;
        }
        catch
        {
            // 断网 / 超时 / JSON 格式变化 —— 静默失败，不影响主流程
            return 0;
        }
    }
}