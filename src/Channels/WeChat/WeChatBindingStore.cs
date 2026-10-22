// 功能：绑定状态落盘 —— 凭证 + 长轮询游标 + 上下文令牌 + local_token_list
//
// 与 LKK 的两点不同（都是刻意的）：
// ① LKK 把凭证放云端 wechat_bindings 表、本地只留可丢缓存；本仓无此表，凭证落本地单文件。
// ② 因为 local_token_list 里存的是历次 bot token（本身就等于凭证），**不能**分成
//    "加密的凭证文件 + 明文的缓存文件"——那等于把密钥写在旁边。所以只有一个文件。
//
// 加密策略与 ClientPlatform.CloudSessionStore 一致：Windows 走 DPAPI，其它平台明文 + chmod 600。

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Channels.WeChat;

/// <summary>落盘的绑定与会话状态。</summary>
public sealed record WeChatBindingState
{
    /// <summary>结构版本。加字段是向后兼容的（缺失即取默认值），改动语义时才需要升版本。</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>绑定凭证；未绑定为 null。</summary>
    public WeChatCredential? Credential { get; init; }

    /// <summary>getupdates 增量游标。不回传会重放或丢消息。</summary>
    public string GetUpdatesBuf { get; init; } = "";

    /// <summary>回复令牌，入站消息更新（回复时必须逐字带回）。</summary>
    public string? ContextToken { get; init; }

    /// <summary>typing 票据（官方 getconfig 取得，缺它 sendtyping 是静默空操作）。</summary>
    public string? TypingTicket { get; init; }

    /// <summary>typing 票据取得时间（超期后重取；服务端会过期）。</summary>
    public DateTimeOffset? TypingTicketAt { get; init; }

    /// <summary>
    /// 历次绑定的 bot token，回传为请求体的 local_token_list。
    /// **不能省**：空列表会让服务端视本机为全新客户端，重新签发并解绑旧绑定（= 每次都要重扫码）。
    /// </summary>
    public IReadOnlyList<string> LocalTokens { get; init; } = [];

    /// <summary>最近一次声明所有权的入口标签（仅诊断展示，正确性由文件锁保证）。</summary>
    public string? OwnerLabel { get; init; }

    /// <summary>最多保留的 local token 数。够覆盖几次重绑即可，避免文件无限增长。</summary>
    private const int MaxLocalTokens = 10;

    /// <summary>是否已绑定到可用状态。</summary>
    public bool IsBound => Credential?.IsUsable == true;

    /// <summary>追加一个 token（新的在前、去重、超上限丢最旧）。</summary>
    public WeChatBindingState WithLocalToken(string token) =>
        string.IsNullOrWhiteSpace(token) || LocalTokens.Contains(token, StringComparer.Ordinal)
            ? this
            : this with
            {
                LocalTokens = LocalTokens.Prepend(token).Take(MaxLocalTokens).ToArray(),
            };
}

/// <summary>
/// 绑定状态的读写。解析失败按"空状态"处理而不是抛异常——损坏的文件不该把用户锁在绑定之外
/// （代价只是重新扫一次码，比通道起不来好得多）。
/// </summary>
public sealed class WeChatBindingStore(string directory, ILogger<WeChatBindingStore>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>状态文件路径（诊断用）。</summary>
    public string FilePath { get; } = Path.Combine(directory, "binding.json");

    /// <summary>读取。文件不存在 / 损坏 / 无法解密 → 空状态（记 Warning，不抛）。</summary>
    public WeChatBindingState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new WeChatBindingState();

            var json = Decode(File.ReadAllBytes(FilePath));
            return JsonSerializer.Deserialize<WeChatBindingState>(json, JsonOptions)
                ?? new WeChatBindingState();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "微信绑定状态读取失败，按未绑定处理: {Path}", FilePath);
            return new WeChatBindingState();
        }
    }

    /// <summary>
    /// 原子写。目录不存在先建；唯一临时名（两个写者不能踩同一个 .tmp）；目标被短暂占用时重试
    /// ——一次写入失败会把状态丢回旧值，长轮询游标回退就会重放消息。
    /// </summary>
    public void Save(WeChatBindingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tmp, Encode(JsonSerializer.Serialize(state, JsonOptions)));
        TryRestrictToOwner(tmp);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, FilePath, overwrite: true);
                TryRestrictToOwner(FilePath);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>清除绑定（解绑）。文件不存在时静默返回。</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "微信绑定状态删除失败: {Path}", FilePath);
        }
    }

    private static byte[] Encode(string json) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser
            )
            : Encoding.UTF8.GetBytes(json);

    private static string Decode(byte[] bytes) =>
        OperatingSystem.IsWindows() ? DecodeWindows(bytes) : Encoding.UTF8.GetString(bytes);

    [SupportedOSPlatform("windows")]
    private static string DecodeWindows(byte[] bytes) =>
        Encoding.UTF8.GetString(
            ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser)
        );

    /// <summary>非 Windows 平台上文件是明文，靠 600 收敛可读范围（Windows 有 DPAPI，无需这步）。</summary>
    private void TryRestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "设置绑定文件权限失败（平台不支持）: {Path}", path);
        }
    }
}
