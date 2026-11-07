using System.Text.Json;
using System.Text.Json.Serialization;
using ThermalTrace.Analysis;
using ThermalTrace.Core;
using ThermalTrace.Export;
using ThermalTrace.Model;

namespace ThermalTrace.Push;

/// <summary>
/// 一次推送的结果。GUI / CLI / 调度三处共用，便于分别给出反馈。
/// </summary>
internal sealed record PushOutcome(bool Ok, string Message)
{
    /// <summary>
    /// 因当日幂等而跳过（既非成功也非失败）。GUI 据此给中性提示而不是红色"失败"——
    /// 手动按钮遵循每日一次，跳过是预期行为。
    /// </summary>
    public bool Skipped { get; init; }
}

/// <summary>
/// 图片页页位的判定结果。<see cref="PushService.ResolvePageId"/> 是唯一判定入口，
/// 设置页摘要 / 适用性行、推送与 CLI 共用，避免同一非法输入在各处得到不同结论。
/// </summary>
internal readonly record struct PageIdResolution(int Value, string? Problem)
{
    /// <summary>合法（或空值按槽位默认页回落），<see cref="Problem"/> 为 null。</summary>
    public bool Ok => Problem is null;

    /// <summary>投递用的规范文本（去空白后的整数），非法时是回落值。</summary>
    public string Page => Value.ToString();
}

/// <summary>
/// 墨水屏推送编排：生成快照 → 推到极趣云 → 写幂等状态 → 失败写日志。
///
/// 三个入口共用本类：
///   - GUI「立即推送一次」
///   - CLI <c>--push</c>
///   - <c>SamplerService</c> 每日检查
///
/// 设计底线：<b>推送失败绝不拖累采样主循环</b>——本类不抛未处理异常，
/// 任何网络 / 设备错误都被吞掉、记到 <c>logs\push.txt</c>，调用方照常继续。
/// </summary>
internal static class PushService
{
    /// <summary>
    /// 执行一次推送。
    ///
    /// <paramref name="force"/> 为 true 时忽略当日幂等（开发期反复测试用）。
    /// 每日自动推送由调度层在调用前先判 <see cref="PushOptions.Enabled"/>，本方法不判 Enabled，
    /// 因此手动 <c>--push</c> 即便关闭了自动推送也能推。
    /// <paramref name="dryRun"/> 为 true 时（D10）：除 HTTP 发送外全真走——生成 PNG、写
    /// <c>push-state.json</c>、写 <c>logs\push.txt</c> 照旧，只跳过网络请求。用于设备不在场 /
    /// 不想惊动设备时，端到端验证 GUI 按钮、每日调度与成功路径的幂等。
    /// </summary>
    public static async Task<PushOutcome> PushAsync(
        AppSettings settings, bool force, bool dryRun = false,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var push = settings.Push;

        // 1) 配置校验：缺任何一项都明确报错、不静默、绝不发无意义请求
        if (string.IsNullOrWhiteSpace(push.DeviceId))
            return Fail("未配置设备 MAC（DeviceId）。推送前请先在设置页填写，或 --push 仅用于开发联调。");

        // 页位与设置页、CLI 共用同一判定：非法值拒绝推送并给出原因，不再原样发给云端
        var page1 = ResolvePageId(push.PageId, 1);
        var page2 = ResolvePageId(push.PageId2, 2);
        if (!page1.Ok || !page2.Ok)
        {
            var problems = new List<string>(2);
            if (!page1.Ok) problems.Add($"槽 1 {page1.Problem}");
            if (!page2.Ok) problems.Add($"槽 2 {page2.Problem}");
            return Fail($"推送页位配置无效：{string.Join("；", problems)}。请在设置页改为 1–5 的整数后重试。");
        }

        var credential = CredentialStore.Resolve(settings);
        if (credential.Key is not { } apiKey)
        {
            // 解密失败（换用户 / 密文损坏）要留可诊断日志；缺 Key 属于"不该发请求"，不写失败日志。
            // 日志只记原因，不写明文与密文。
            if (credential.Status == CredentialStatus.Corrupt)
                AppendLog($"{Stamp()} 凭据不可用：{credential.Message}");
            return Fail(credential.Message);
        }

        // 2) 幂等（D13）：逐槽判。键是"应推业务日期"T（已到推送时刻的最近一天），不是执行时刻——
        // 补推次日执行也记 T，否则会把当晚正式推送的名额挤掉（dry-run 白天跑一次就幂等掉当晚真推，同病）。
        var due = DueDate(settings);
        var state = LoadState();

        // 两槽页位相同 → 槽 2 不适用：后推的图会覆盖前一张，不能静默覆盖
        var pageConflict = page1.Value == page2.Value;

        var slot1Due = IsSlotDue(state, EinkSlot.Slot1, due, force);
        var slot2Due = !pageConflict && IsSlotDue(state, EinkSlot.Slot2, due, force);

        // 报告只在至少一个槽要出图时才构建——两槽共用同一份 TrendReport，解析口径必然一致
        TrendReport? report;
        try
        {
            report = slot1Due || slot2Due ? TrendAnalyzer.Build(settings) : null;
        }
        catch (Exception ex)
        {
            return Fail($"读取趋势数据失败：{ex.Message}");
        }

        // 两个槽共用同一个 ZectrixClient：连接池与 TLS 会话复用，少一次握手；
        // dry-run 不发请求，不建客户端。生命周期由本方法持有，DeliverAsync 只借用。
        using var client = !dryRun && (slot1Due || slot2Due)
            ? new ZectrixClient(timeout: PushRetryPolicy.RequestTimeout)
            : null;

        var results = new List<string>();
        bool anyFailure = false;
        bool anyAttempted = false;

        // ---------- 槽 1：近 30 天趋势图 ----------
        if (!slot1Due)
        {
            results.Add($"槽1：业务日期 {due:yyyy-MM-dd} 已推送过，跳过");
        }
        else
        {
            anyAttempted = true;
            try
            {
                var (png, what) = BuildSlot1Image(settings, report!, log);
                var delivery = await DeliverAsync(
                    EinkSlot.Slot1, page1.Page, push.DeviceId.Trim(), apiKey, png, what,
                    "last-push.png", due, dryRun, client, log, ct).ConfigureAwait(false);
                results.Add(delivery.Ok
                    ? $"槽1：已推送{what}"
                    : $"槽1：推送失败（{delivery.Describe()}：{delivery.Error}）");
                anyFailure |= !delivery.Ok;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 生成或投递的意外异常只判本槽失败，不能拖垮槽 2
                AppendLog($"{Stamp()} 槽1 异常：{ex.Message}");
                log?.Invoke($"[ERR] 槽1 异常：{ex.Message}");
                results.Add($"槽1：异常（{ex.Message}）");
                anyFailure = true;
            }
        }

        // ---------- 槽 2：自清灰以来平滑图 ----------
        if (!slot2Due)
        {
            results.Add(pageConflict
                ? $"槽2：跳过（图片页与槽 1 相同 {page1.Page}，后推会覆盖前一页）"
                : $"槽2：业务日期 {due:yyyy-MM-dd} 已推送过，跳过");
            if (pageConflict)
                log?.Invoke($"[warn] 槽 1 与槽 2 的图片页相同（{page1.Page}），槽 2 跳过推送。");
        }
        else
        {
            try
            {
                var snap = EinkSnapshotBuilder.Build(settings, report!, EinkSlot.Slot2);
                if (!snap.IsApplicable)
                {
                    // 不适用（无清灰日 / 有效日不足等）：不出图、不推送、不写状态。
                    // 不写状态的用意是"槽 2 不因不适用反复被调度判定为到期"，槽 1 仍按原规则走。
                    var reason = snap.SkipReason ?? "槽 2 不适用";
                    AppendLog($"{Stamp()} 槽2 跳过推送：{reason}");
                    log?.Invoke($"[--] 槽2 不适用：{reason}");
                    results.Add($"槽2：不适用（{reason}）");
                }
                else
                {
                    anyAttempted = true;
                    var png = EinkRenderer.RenderV2(snap, log, DateTime.Now);
                    var what = $"平滑图（自清灰以来 · {snap.Lines.Count} 档 · 平滑窗口 {snap.SmoothWindowDays} 天 · 跨度 {snap.SpanDays} 天）";
                    var delivery = await DeliverAsync(
                        EinkSlot.Slot2, page2.Page, push.DeviceId.Trim(), apiKey, png, what,
                        "last-push-slot2.png", due, dryRun, client, log, ct).ConfigureAwait(false);
                    results.Add(delivery.Ok
                        ? $"槽2：已推送{what}"
                        : $"槽2：推送失败（{delivery.Describe()}：{delivery.Error}）");
                    anyFailure |= !delivery.Ok;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppendLog($"{Stamp()} 槽2 异常：{ex.Message}");
                log?.Invoke($"[ERR] 槽2 异常：{ex.Message}");
                results.Add($"槽2：异常（{ex.Message}）");
                anyFailure = true;
            }
        }

        var message = string.Join("；", results);
        if (anyFailure) return new PushOutcome(false, message);
        return new PushOutcome(true, message) { Skipped = !anyAttempted };
    }

    /// <summary>
    /// 槽 1 的图：数据不足时推"已积累 N 天"模板图（按天数缓存，不反复重画），
    /// 否则走趋势图（5 档 ΔT + 加权总体趋势）。与 <c>--eink --slot 1</c> 同一条链，一个数都不另算。
    /// </summary>
    private static (byte[] Png, string What) BuildSlot1Image(
        AppSettings settings, TrendReport report, Action<string>? log)
    {
        if (report.DaysWithData < EinkSnapshotBuilder.MinDaysToDraw)
        {
            var cache = Path.Combine(AppPaths.EinkDir, $"nodata-{report.DaysWithData}.png");
            var png = File.Exists(cache)
                ? File.ReadAllBytes(cache)
                : EinkRenderer.RenderNoDataPng(report.DaysWithData);
            return (png, $"无数据模板图（已积累 {report.DaysWithData} 天）");
        }

        var snap = EinkSnapshotBuilder.Build(settings, report);
        return (EinkRenderer.RenderV2(snap, log, DateTime.Now),
            snap.HasData
                ? $"趋势图（{snap.Lines.Count} 档 ΔT · {snap.Lines[0].Points.Count} 天）"
                : "趋势图（窗口内无可绘制数据）");
    }

    /// <summary>
    /// 单槽投递：本地预览 →（dry-run 则止步于此）→ 带失败分类与退避的 HTTP → 逐槽写状态。
    /// 两个槽各自调用一次，异常只影响本槽；客户端由 <see cref="PushAsync"/> 持有并复用。
    /// </summary>
    private static async Task<PushDeliveryResult> DeliverAsync(
        EinkSlot slot, string pageId, string deviceId, string apiKey, byte[] png,
        string what, string previewFile, DateOnly due, bool dryRun,
        ZectrixClient? client, Action<string>? log, CancellationToken ct)
    {
        var tag = slot == EinkSlot.Slot2 ? "槽2" : "槽1";

        // 本地留一份"最后一次推送图"，方便电脑上预览真机将显示什么
        try
        {
            Directory.CreateDirectory(AppPaths.EinkDir);
            File.WriteAllBytes(Path.Combine(AppPaths.EinkDir, previewFile), png);
        }
        catch (Exception ex) { log?.Invoke($"[warn] {tag} 无法写入本地预览图：{ex.Message}"); }

        // dry-run（D10）：除 HTTP 外全真走——不建客户端、不发请求，状态与日志照写
        if (dryRun)
        {
            SaveSlotResult(due, slot, "ok", "");
            AppendLog($"{Stamp()} {tag} [dry-run] 跳过 HTTP 发送（其余全真）：{what} → 设备 {deviceId} 图片页 {pageId}");
            log?.Invoke($"[dry-run] {tag} 跳过 HTTP 发送（其余全真）。已推送{what} → 设备 {deviceId} 图片页 {pageId}。");
            return PushDeliveryResult.Success(0, TimeSpan.Zero);
        }

        var uploadName = slot == EinkSlot.Slot2
            ? $"thermaltrace-slot2-{due:yyyyMMdd}.png"
            : $"thermaltrace-{due:yyyyMMdd}.png";

        // 失败分类与退避在 PushRetryPolicy：4xx 一次即回，网络 / 超时 / 5xx 退避 10 s、20 s
        var result = await PushRetryPolicy.DeliverAsync(
            token => client!.PushImageAsync(apiKey, deviceId, pageId, png, uploadName, token),
            tag, log, ct).ConfigureAwait(false);

        if (result.Ok)
        {
            SaveSlotResult(due, slot, "ok", "");
            log?.Invoke($"[OK] {tag} 已推送{what} → 设备 {deviceId} 图片页 {pageId}。");
            return result;
        }

        // 彻底失败：记日志 + 记本槽状态（本业务日期不再重复重试，避免刷接口），下一周期覆盖内容
        var summary = $"{result.Describe()}，{result.Attempts} 次尝试，总耗时 {result.Elapsed.TotalSeconds:0.0} 秒";
        AppendLog($"{Stamp()} {tag} 推送失败（{summary}）：{result.Error}");
        SaveSlotResult(due, slot, "error", result.Error);
        log?.Invoke($"[ERR] {tag} 推送失败（{summary}）：{result.Error}");
        return result;
    }

    private static string Stamp() => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}";

    /// <summary>
    /// 图片页页位的唯一判定入口（设置页、推送、CLI 共用）。
    /// 空值按槽位默认页回落（槽 1 = 1、槽 2 = 2）；合法值为 1–5 的整数（忽略首尾空白），
    /// 返回规范化文本。非法值同样回落到默认页，但 <see cref="PageIdResolution.Problem"/>
    /// 会给出可读原因——推送据此拒绝执行，设置页据此明示，绝不再静默按某个值继续。
    /// </summary>
    public static PageIdResolution ResolvePageId(string? raw, int fallback)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return new PageIdResolution(fallback, null);
        if (int.TryParse(text, out var page) && page is >= 1 and <= 5)
            return new PageIdResolution(page, null);

        var why = int.TryParse(text, out _) ? "超出范围（有效 1–5）" : "不是整数";
        return new PageIdResolution(fallback, $"页位“{text}”无效：{why}");
    }

    // ------------------------------------------------------------------
    // D13 调度判定：应推业务日期 T
    // ------------------------------------------------------------------

    /// <summary>
    /// 当前"应推"的业务日期 T（D13）：已到当日推送时刻 <see cref="PushOptions.Hour"/> 则是今天，
    /// 否则是昨天。
    ///
    /// 【为什么不是"今天"】23:00–24:00 没推成（没开机 / 休眠）时，次日 0:00–23:00 之间
    /// "今天"永远还没到点——用今天当键，漏推的那一帧永远补不上（"错过则下次运行时补推"
    /// 从不发生）。用 T 当键，次日第一次检查就发现 T 仍停在昨天，当即补推，且必然在下一个
    /// 推送时刻之前。
    /// <paramref name="now"/> 可注入以便离线验时序（测试 / 排查）。
    /// </summary>
    public static DateOnly DueDate(AppSettings settings, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        var today = DateOnly.FromDateTime(t);
        var hour = Math.Clamp(settings.Push.Hour, 0, 23);
        return t.Hour >= hour ? today : today.AddDays(-1);
    }

    /// <summary>
    /// 纯判定（D13）：启用了、且上次推送的业务日期早于应推日期 → 现在该推。
    /// 抽成纯函数是为了能把三种时序（到点即推 / 漏推即补 / 推完惰性）离线逐条验，
    /// 不用真等到 23:00。<paramref name="lastPushed"/> 为 null 表示从未推过（首日即推）。
    /// </summary>
    public static bool IsDue(DateOnly? lastPushed, AppSettings settings, DateTime now) =>
        settings.Push.Enabled && (lastPushed is not { } last || last < DueDate(settings, now));

    /// <summary>
    /// 调度入口：读一次状态文件 + 判定，供 <c>SamplerService</c> 每 60 秒调用。
    /// 只读不写，成本是一次小文件读加两次比较。
    ///
    /// 【为什么只看槽 1】槽 1 恒适用（近 30 天趋势图），槽 2 可能因无清灰日 / 有效日不足长期
    /// 不适用。若把槽 2 也算进"到期"，它会每天把判定顶成真、反复重推槽 1。槽 2 的补推
    /// 由槽 1 的到期带动；两槽都推成功后各自记 T，不会重复。
    /// </summary>
    public static bool ShouldPushNow(AppSettings settings)
    {
        var (last, _, _) = GetLastStatus(EinkSlot.Slot1);
        return IsDue(ParseDate(last), settings, DateTime.Now);
    }

    // ------------------------------------------------------------------
    // 幂等状态（push-state.json）
    // ------------------------------------------------------------------

    /// <summary>
    /// 读一次某个槽的上次推送结果（GUI 设置页「上次推送」行用，P3.3）。
    /// 文件不存在 / 损坏时返回空值，不抛——没推过就不是错误。
    /// 默认槽 1，兼容原单槽调用方；逐槽展示由第 3 批设置页接入。
    /// </summary>
    public static (string Date, string Result, string Error) GetLastStatus(
        EinkSlot slot = EinkSlot.Slot1)
    {
        var s = LoadState().Slot(slot);
        return (s.LastPushedDate, s.LastResult, s.LastError);
    }

    /// <summary>
    /// <c>push-state.json</c>：逐槽幂等状态。
    ///
    /// <code>
    /// { "slots": { "slot1": { "lastPushedDate": "2026-11-04", "lastResult": "ok", ... },
    ///              "slot2": { ... } } }
    /// </code>
    ///
    /// 旧版只有顶层 <c>lastPushedDate</c> / <c>lastResult</c> / <c>lastError</c>：
    /// 读取时只迁移给槽 1（槽 2 首日没有历史，不能凭空继承），写盘时不再输出顶层字段。
    /// </summary>
    private sealed class PushState
    {
        [JsonPropertyName("lastPushedDate")]
        public string? LastPushedDate { get; set; }

        [JsonPropertyName("lastResult")]
        public string? LastResult { get; set; }

        [JsonPropertyName("lastError")]
        public string? LastError { get; set; }

        [JsonPropertyName("slots")]
        public Dictionary<string, SlotState>? Slots { get; set; }

        public SlotState Slot(EinkSlot slot)
        {
            var key = SlotKey(slot);
            return Slots is not null && Slots.TryGetValue(key, out var found) && found is not null
                ? found
                : new SlotState();
        }

        /// <summary>
        /// 旧文件只有顶层记录：迁进 <c>slots.slot1</c>（槽 2 首日没有历史，不能凭空继承）。
        /// 迁移在读取时完成，之后任何一次写盘都不会再丢掉槽 1 的历史。
        /// </summary>
        public void MigrateLegacy()
        {
            if (string.IsNullOrWhiteSpace(LastPushedDate)) return;
            if (Slots is not null && Slots.ContainsKey("slot1")) return;

            Slots ??= new Dictionary<string, SlotState>();
            Slots["slot1"] = new SlotState
            {
                LastPushedDate = LastPushedDate!,
                LastResult = LastResult ?? "",
                LastError = LastError ?? ""
            };
        }

        public void Set(EinkSlot slot, string date, string result, string error)
        {
            Slots ??= new Dictionary<string, SlotState>();
            Slots[SlotKey(slot)] = new SlotState
            {
                LastPushedDate = date,
                LastResult = result,
                LastError = error
            };
        }
    }

    private sealed class SlotState
    {
        [JsonPropertyName("lastPushedDate")]
        public string LastPushedDate { get; set; } = "";

        [JsonPropertyName("lastResult")]
        public string LastResult { get; set; } = "";   // "ok" | "error"

        [JsonPropertyName("lastError")]
        public string LastError { get; set; } = "";
    }

    private static string SlotKey(EinkSlot slot) =>
        slot == EinkSlot.Slot2 ? "slot2" : "slot1";

    private static bool IsSlotDue(PushState state, EinkSlot slot, DateOnly due, bool force) =>
        force || ParseDate(state.Slot(slot).LastPushedDate) is not { } last || last < due;

    private static PushState LoadState()
    {
        var path = AppPaths.PushStateFile;
        if (!File.Exists(path)) return new PushState();
        try
        {
            var s = JsonSerializer.Deserialize<PushState>(File.ReadAllText(path)) ?? new PushState();
            s.MigrateLegacy();
            return s;
        }
        catch
        {
            return new PushState();
        }
    }

    /// <summary>
    /// 记一个槽的结果。每次重新读盘再写，避免槽 2 的保存覆盖槽 1 刚写入的记录。
    /// </summary>
    private static void SaveSlotResult(DateOnly due, EinkSlot slot, string result, string error)
    {
        var state = LoadState();
        state.Set(slot, due.ToString("yyyy-MM-dd"), result, error);
        SaveState(state);
    }

    private static void SaveState(PushState state)
    {
        // 旧顶层字段已迁进 slots，写盘时不再保留，避免同一信息两份来源
        state.LastPushedDate = null;
        state.LastResult = null;
        state.LastError = null;

        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            JsonStore.Save(AppPaths.PushStateFile, state);
        }
        catch (Exception ex)
        {
            // 状态写不进去也不能让推送结果变"失败"——它已经成功/已经尽力了
            ConsoleHost.WriteError($"[warn] 推送状态写盘失败（不影响已完成的推送）: {ex.Message}");
        }
    }

    private static DateOnly? ParseDate(string s) =>
        DateOnly.TryParse(s, out var d) ? d : null;

    // ------------------------------------------------------------------
    // 失败日志（logs\push.txt）
    // ------------------------------------------------------------------

    /// <summary>与采样日志同一口径：写前按体积判断，超过 1 MB 轮转，归档最多 3 份。</summary>
    private const long PushLogRotateBytes = 1024 * 1024;

    private const int PushLogMaxArchives = 3;

    private static readonly object LogGate = new();

    private static void AppendLog(string line)
    {
        lock (LogGate)
        {
            try
            {
                var dir = AppPaths.LogsDir;
                Directory.CreateDirectory(dir);

                var path = AppPaths.PushLogFile;
                if (File.Exists(path) && new FileInfo(path).Length >= PushLogRotateBytes)
                    RotateLog(path, dir);

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { /* 连日志都写不了时只能放弃，绝不抛 */ }
        }
    }

    /// <summary>
    /// 把当前日志移成带时间戳的归档，再按由新到旧只留 <see cref="PushLogMaxArchives"/> 份。
    /// 单份归档删不掉只跳过，不影响下一次写入。
    /// </summary>
    private static void RotateLog(string path, string dir)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var archive = Path.Combine(dir, $"push.{stamp}.txt");
        for (var i = 1; File.Exists(archive); i++)
            archive = Path.Combine(dir, $"push.{stamp}-{i}.txt");
        File.Move(path, archive);

        var archives = Directory.GetFiles(dir, "push.*.txt")
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .ToArray();
        foreach (var old in archives.Skip(PushLogMaxArchives))
        {
            try { File.Delete(old); }
            catch { /* 归档清理失败不影响后续写入 */ }
        }
    }

    private static PushOutcome Fail(string message)
    {
        // 配置缺失属于"不该发请求"的情况：不写成功状态、不写失败日志（不是运行期故障）。
        // 只返回失败，由调用方（CLI 的 Commands.Push / GUI 的按钮反馈）负责展示与退出码——
        // 本类不直接写控制台，否则 GUI 模式下既无控制台可写，又会被打印两次。
        return new PushOutcome(false, message);
    }
}
