using System.Security.Cryptography;
using System.Text;
using Core.Cloud;

namespace CloudDiagnosis;

/// <summary>
/// 章节集指纹（幂等键共用收口）：target + 最近 ≤3 章 previous 的确定性摘要。
/// 长度前缀避免拼接歧义；含 ClientChapterRef/Order/Role 覆盖章节排序与角色变化。
/// DiagnosisScheduler 与 ContextSnapshotCache 两侧同式——前文改写（目标章未动）→ 新键。
/// </summary>
internal static class ChapterSetFingerprint
{
    /// <summary>完整 SHA-256（64 hex）替代 8 hex 截断，消除 32bit 碰撞面（键长仍远低于服务端 200 上限）。</summary>
    internal static string Compute(IReadOnlyList<ChapterInput> chapters)
    {
        var payload = string.Join(
            '\n',
            chapters.Select(c =>
                $"{c.ClientChapterRef}:{c.Order}:{c.Role}:{c.Content.Length}:{c.Content}"
            )
        );
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
