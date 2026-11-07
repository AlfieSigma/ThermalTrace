using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ThermalTrace.Push;

/// <summary>
/// 极趣（Zectrix）云开放 API 的薄封装。只有两个接口被本项目用到：
/// <c>GET /devices</c>（连通性自检 + 拿 deviceId）与
/// <c>POST /devices/{deviceId}/display/image</c>（推图到图片页副页）。
///
/// 认证统一用 <c>X-API-Key</c> 请求头（形如 <c>zt_xxx</c>）。
/// 见极趣云开放接口的设备清单。
///
/// 【释放所有权】<see cref="Dispose"/> 释放的永远只有构造函数内部创建的 <see cref="HttpClient"/>；
/// 外部注入的实例归调用方，包装释放不会动它。两个槽共用同一个包装，也就共用同一个连接池。
/// </summary>
internal sealed class ZectrixClient : IDisposable
{
    private const string BaseUrl = "https://cloud.zectrix.com/open/v1/";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <param name="http">外部注入的 HttpClient（探针 / 单例）。注入实例的所有权始终归调用方，
    /// 本包装的 <see cref="Dispose"/> 不会释放它。</param>
    /// <param name="timeout">仅对内部新建的 HttpClient 生效；不传则 30 秒。</param>
    public ZectrixClient(HttpClient? http = null, TimeSpan? timeout = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
    }

    public void Dispose()
    {
        // 只释放自己创建的；外部注入的实例可能被多处共用，所有权在调用方。
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>
    /// 连通性自检：拉设备列表。返回 200 即说明 API Key 有效、网络可达、账号可用。
    /// 设备未到货也能跑通——它验证的是云端通道，不是具体某台设备。
    /// </summary>
    public async Task<ZectrixDevices> GetDevicesAsync(
        string apiKey, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "devices");
        req.Headers.Add("X-API-Key", apiKey);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ZectrixApiException((int)resp.StatusCode, body);

        return ZectrixDevices.Parse(body);
    }

    /// <summary>
    /// 推一张图到指定设备的图片页副页。
    ///
    /// 【dither=false】本地已经在 <see cref="Export.EinkRenderer"/> 里手抖成 1bpp 黑白，
    /// 再让云端二值化只会把已经调好的字再糊一遍——显式关掉，本地预览即真机所见（的核心收益）。
    /// 设备未到货时这一调用会返回设备不存在类错误，属预期；通道本身（鉴权 / 网络）已通过自检。
    /// </summary>
    public async Task PushImageAsync(
        string apiKey, string deviceId, string pageId,
        byte[] png, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(pageId), "pageId");
        form.Add(new StringContent("false"), "dither");

        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "images", fileName);

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"devices/{deviceId}/display/image");
        req.Headers.Add("X-API-Key", apiKey);
        req.Content = form;

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ZectrixApiException((int)resp.StatusCode, body);
    }
}

/// <summary>极趣云非 2xx 时抛出，带上状态码与响应体方便排查。</summary>
internal sealed class ZectrixApiException(int status, string body)
    : Exception($"极趣云返回 {status}: {body}")
{
    public int StatusCode { get; } = status;

    /// <summary>
    /// 4xx 视为永久错误：设备标识 / 图片页 / 凭据被云端拒绝，重试多少次结果都一样。
    /// 408 与 429 也按 4xx 处理；5xx 与网络异常才是暂时性失败。
    /// </summary>
    public bool IsPermanent => StatusCode is >= 400 and < 500;
}

/// <summary>
/// 一台设备的摘要信息（GUI「获取设备列表」用：选中即回填 MAC）。
/// </summary>
internal sealed record ZectrixDeviceInfo(string DeviceId, string Alias);

/// <summary>
/// 设备列表的自卫解析。
/// 【2026-10-14 实测修正】响应形状已确认为 <c>{"code":0,"msg":"success","data":[...]}</c>——
/// 设备数组在 <b>data</b> 字段，不是原计划猜测的 devices。空账号返回 <c>data:[]</c>。
/// 两个名字都试：data 为准（实测），devices 兜底（万一不同环境/版本 schema 不同）。
/// <see cref="Count"/> 只要解析且不空就认为通道可用；<see cref="Devices"/> 供 GUI 展示与回填，
/// 逐条尽力取 <c>deviceId</c> / <c>alias</c>，缺 <c>deviceId</c> 的条目跳过（没有 MAC 就没法推）。
/// </summary>
internal sealed class ZectrixDevices
{
    public int Count { get; init; }
    public string Raw { get; init; } = "";
    public IReadOnlyList<ZectrixDeviceInfo> Devices { get; init; } = Array.Empty<ZectrixDeviceInfo>();

    public static ZectrixDevices Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // data 为准（实测），devices 兜底（原计划猜测的名字）
            var count = ArrayCount(doc.RootElement, "data");
            var devices = ParseArray(doc.RootElement, "data");
            if (count == 0)
            {
                count = ArrayCount(doc.RootElement, "devices");
                devices = ParseArray(doc.RootElement, "devices");
            }
            return new ZectrixDevices { Count = count, Devices = devices, Raw = json };
        }
        catch
        {
            // 解析不了也别让自检崩：把原文带回去，至少是"收到了响应"
            return new ZectrixDevices { Count = -1, Raw = json };
        }
    }

    /// <summary>取指定字段的数组长度；字段缺失或不是数组返回 0。</summary>
    private static int ArrayCount(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
            ? el.GetArrayLength()
            : 0;

    /// <summary>逐条抽 deviceId / alias；缺 deviceId 的条目跳过（没有 MAC 就没有推送目标）。</summary>
    private static List<ZectrixDeviceInfo> ParseArray(JsonElement root, string name)
    {
        var list = new List<ZectrixDeviceInfo>();
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("deviceId", out var idEl)) continue;
            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;

            var alias = item.TryGetProperty("alias", out var aEl) ? aEl.GetString() : null;
            list.Add(new ZectrixDeviceInfo(id.Trim(), alias?.Trim() ?? ""));
        }
        return list;
    }
}
