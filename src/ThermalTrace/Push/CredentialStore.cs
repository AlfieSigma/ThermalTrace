using System.Security.Cryptography;
using System.Text;
using ThermalTrace.Model;

namespace ThermalTrace.Push;

/// <summary>凭据解析结果的四种状态。缺 Key 与解密失败必须分开，界面才能给出可操作的提示。</summary>
internal enum CredentialStatus
{
    /// <summary>拿到了明文。</summary>
    Ok,
    /// <summary>从未保存过（<see cref="PushOptions.ApiKeyEnc"/> 为空）。</summary>
    Missing,
    /// <summary>密文在，但无法解密（密文格式损坏，或换 Windows 用户后 DPAPI 不匹配）。</summary>
    Corrupt,
    /// <summary>本会话 DPAPI 加密失败，明文只留在内存，重启即失效。</summary>
    MemoryOnly
}

/// <summary>
/// 一次凭据解析的结论。<see cref="Message"/> 可直接给用户看，绝不包含明文或密文。
/// </summary>
internal readonly record struct CredentialResolution(
    string? Key, CredentialStatus Status, string Message);

/// <summary>
/// API Key 的持久化与解密。唯一原则：<b>明文永不落盘</b>。
///
/// 正常路径：明文 → <see cref="ProtectedData"/>（DPAPI, CurrentUser）加密 → base64 存
/// <see cref="PushOptions.ApiKeyEnc"/>。解密时反向走一遍。
///
/// 【为什么 DPAPI 可行】
/// 开机自启是 <c>AutoStart.cs</c> 的 <c>LogonTrigger</c> + <c>InteractiveToken</c>，
/// 用户登录后触发、用户配置已加载，CurrentUser 作用域的密文可被同一用户解回。
///
/// 【降级路径】（DPAPI 不可用时）
/// 加密失败时明文只留在<b>本进程内存</b>（<see cref="_memoryKey"/>），本次会话可用，
/// 并让调用方记一条警告、提示"重启后需重新填写"。绝不以明文写磁盘。
/// </summary>
internal static class CredentialStore
{
    // 降级用：DPAPI 不可用（如服务/系统账户上下文）时明文仅存内存，重启即失效。
    private static string? _memoryKey;

    /// <summary>
    /// 把明文 API Key 加密并写回 <paramref name="settings"/> 的 <see cref="PushOptions.ApiKeyEnc"/>。
    /// 返回 false 表示走的是内存降级（加密失败），<paramref name="error"/> 带原因。
    /// 传入空串表示"清空 Key"，这是合法操作、不需要密钥，直接返回 true。
    /// </summary>
    public static bool TryEncryptAndStore(AppSettings settings, string? plaintext, out string error)
    {
        error = "";

        var key = (plaintext ?? "").Trim();
        if (key.Length == 0)
        {
            // 清空：清掉密文与内存副本，不留任何残余
            settings.Push = settings.Push with { ApiKeyEnc = "" };
            _memoryKey = null;
            return true;
        }

        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
            settings.Push = settings.Push with { ApiKeyEnc = Convert.ToBase64String(cipher) };
            _memoryKey = null;
            return true;
        }
        catch (Exception ex)
        {
            // 降级：明文仅留内存，本次会话仍可推送；提示用户重启后重填
            error = ex.Message;
            settings.Push = settings.Push with { ApiKeyEnc = "" };
            _memoryKey = key;
            return false;
        }
    }

    /// <summary>
    /// 解出明文并说明结论。优先级：内存降级副本（若本会话曾因加密失败而暂存） → DPAPI 密文。
    /// “未保存”与“密文无法解密（换用户 / 数据损坏）”分开返回：前者是没填，后者是填过但坏了、
    /// 需要重新保存。任何分支都绝不返回错误的值，<see cref="CredentialResolution.Message"/> 也绝不含明文或密文。
    /// </summary>
    public static CredentialResolution Resolve(AppSettings settings)
    {
        if (_memoryKey is { Length: > 0 } mem)
            return new CredentialResolution(mem, CredentialStatus.MemoryOnly,
                "API Key 只保存在本次会话内存中（DPAPI 加密失败），重启后需重新填写并保存。");

        var enc = settings.Push.ApiKeyEnc;
        if (string.IsNullOrWhiteSpace(enc))
            return new CredentialResolution(null, CredentialStatus.Missing,
                "未配置 API Key。请先在设置页填写，重填后会以密文保存。");

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(enc);
        }
        catch (FormatException)
        {
            return new CredentialResolution(null, CredentialStatus.Corrupt,
                "API Key 密文格式损坏（不是有效的 base64）。需要重新填写并保存 API Key。");
        }

        try
        {
            var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return new CredentialResolution(Encoding.UTF8.GetString(plain), CredentialStatus.Ok, "");
        }
        catch (Exception)
        {
            // 换 Windows 用户 / 配置损坏：宁可解密失败也不返回错误的值
            return new CredentialResolution(null, CredentialStatus.Corrupt,
                "API Key 密文无法解密（可能换了 Windows 用户或密文已损坏）。需要重新填写并保存 API Key。");
        }
    }
}
