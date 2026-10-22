using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClientPlatform;

/// <summary>
/// 云端凭据存储 — TUI/Desktop 保存设备授权获得的 Data Plane API Key。
/// 存储在 ~/.mythology/cloud-session.json（与 config.yaml / sessions 等同目录），权限 600。
/// </summary>
public sealed class CloudSessionStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
    };

    /// <param name="mythologyDir">数据目录覆盖（缺省 ~/.mythology；测试传入）。</param>
    public CloudSessionStore(string? mythologyDir = null)
    {
        // 统一到 ~/.mythology/（UserProfile/.mythology），与 config.yaml / sessions / plugins / input-history 同目录；
        // 显式传入 mythologyDir（测试）时仍以调用方为准。
        var dir =
            mythologyDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mythology"
            );
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "cloud-session.json");
    }

    public CloudSession? Load()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            var bytes = File.ReadAllBytes(_path);
            var json = OperatingSystem.IsWindows()
                ? DecodeWindows(bytes)
                : Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<CloudSession>(json, Json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(CloudSession session)
    {
        var json = JsonSerializer.Serialize(session, Json);
        var bytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser
            )
            : Encoding.UTF8.GetBytes(json);
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        TryChmod600(tempPath);
        File.Move(tempPath, _path, overwrite: true);
        TryChmod600(_path);
    }

    [SupportedOSPlatform("windows")]
    private static string DecodeWindows(byte[] bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(
                    bytes,
                    optionalEntropy: null,
                    DataProtectionScope.CurrentUser
                )
            );
        }
        catch (CryptographicException)
        {
            // One-time compatibility path for legacy plaintext sessions.
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public void Clear()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void TryChmod600(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // 平台不支持时忽略
        }
    }
}

/// <summary>已保存的云端会话。</summary>
public sealed record CloudSession(
    string ApiKey,
    string PublicId,
    string WorkspaceId,
    string Plan,
    DateTimeOffset SavedAt,
    DateTimeOffset? ExpiresAt = null,
    string DisplayName = ""
);
