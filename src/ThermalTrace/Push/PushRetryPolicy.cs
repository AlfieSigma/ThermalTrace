using System.Diagnostics;

namespace ThermalTrace.Push;

/// <summary>
/// 推送投递的重试与失败分类。
///
/// 口径：4xx 一律视为永久错误，第一次失败即返回并记录；只有网络异常、超时与 5xx
/// 进入退避重试，间隔 10 秒、20 秒，最多两次。408 与 429 也按 4xx 处理，不特殊放行。
/// 退避节奏取短：用户要的是失败后尽快反馈，30 s + 60 s 会让离线时的总耗时反而高于改造前。
///
/// 与 HTTP 细节无关：调用方只传"一次尝试"的委托，本类负责次数、退避、日志与结果汇总。
/// 槽 1 / 槽 2 各自调用一次，一个槽失败不影响另一个。
/// </summary>
internal static class PushRetryPolicy
{
    /// <summary>
    /// 单次 HTTP 的上限。只推一张 400×300 的小图，10 秒足够；
    /// 离线时靠它把单次尝试封顶，避免超时把单槽最坏时长拉长。
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>首次失败后的等待。数组长度即最大重试次数（2 次，共 3 次尝试）。</summary>
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20)
    };

    /// <summary>退避合计（30 秒），写进日志让用户知道最坏要等多久。</summary>
    private static readonly TimeSpan TotalBackoff =
        TimeSpan.FromSeconds(Backoff.Sum(d => d.TotalSeconds));

    /// <summary>
    /// 执行一次带分类与退避的投递。<paramref name="send"/> 抛出的异常按
    /// <see cref="ZectrixApiException.IsPermanent"/> 分类；调用方主动取消时原样抛出。
    /// </summary>
    public static async Task<PushDeliveryResult> DeliverAsync(
        Func<CancellationToken, Task> send, string tag, Action<string>? log, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        Exception? lastErr = null;

        for (var attempt = 1; attempt <= Backoff.Length + 1; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await send(ct).ConfigureAwait(false);
                return PushDeliveryResult.Success(attempt, started.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用方主动取消，不是推送失败；超时（ct 未取消）走下面的 transient 分支
                throw;
            }
            catch (Exception ex)
            {
                lastErr = ex;

                if (ex is ZectrixApiException { IsPermanent: true })
                {
                    log?.Invoke($"[warn] {tag} 第 {attempt} 次推送失败（永久错误，不重试）：{ex.Message}");
                    return PushDeliveryResult.Failure(true, ex.Message, attempt, started.Elapsed);
                }

                if (attempt <= Backoff.Length)
                {
                    var wait = Backoff[attempt - 1];
                    log?.Invoke($"[warn] {tag} 第 {attempt} 次推送失败：{ex.Message}；{wait.TotalSeconds:0} 秒后重试"
                              + $"（最多重试 {Backoff.Length} 次，退避合计 {TotalBackoff.TotalSeconds:0} 秒）");
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                else
                {
                    log?.Invoke($"[warn] {tag} 第 {attempt} 次推送失败：{ex.Message}；已达重试上限");
                }
            }
        }

        return PushDeliveryResult.Failure(
            false, lastErr?.Message ?? "未知错误", Backoff.Length + 1, started.Elapsed);
    }
}

/// <summary>
/// 单槽投递的结果：是否成功、是否永久错误、尝试次数与总耗时。
/// 日志与 GUI 的失败文案都从这里取口径，避免两边各算一套重试次数。
/// </summary>
internal readonly record struct PushDeliveryResult(
    bool Ok, bool Permanent, string Error, int Attempts, TimeSpan Elapsed)
{
    public static PushDeliveryResult Success(int attempts, TimeSpan elapsed) =>
        new(true, false, "", attempts, elapsed);

    public static PushDeliveryResult Failure(bool permanent, string error, int attempts, TimeSpan elapsed) =>
        new(false, permanent, error, attempts, elapsed);

    /// <summary>失败原因的中性描述（不含具体错误信息，便于和 <see cref="Error"/> 拼在不同位置）。</summary>
    public string Describe() => Permanent
        ? "永久错误，未重试"
        : $"重试 {Math.Max(Attempts - 1, 0)} 次后仍失败";
}
