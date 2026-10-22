using System.Security.Cryptography;
using System.Text;

namespace Core.Vector;

/// <summary>
/// 稳定 ID 生成：基于业务键确定性推导，不依赖运行时字符串哈希。
/// 相同输入永远产生相同 ID；不同输入产生不同 ID（SHA-256 截断）。
/// </summary>
public static class StableId
{
    /// <summary>
    /// 从业务键组件生成稳定 document_id。
    /// 格式：domain:kind:novelId:entityType:entityId
    /// </summary>
    public static string DocumentId(
        RetrievalDomain domain,
        DocumentKind kind,
        string? novelId,
        string? sourceEntityType,
        string sourceEntityId
    )
    {
        var parts =
            $"{(int)domain}:{(int)kind}:{novelId ?? ""}:{sourceEntityType ?? ""}:{sourceEntityId}";
        return DeriveId(parts, prefix: "doc");
    }

    /// <summary>
    /// 从 document_id + chunk ordinal 生成稳定 chunk_id。
    /// </summary>
    public static string ChunkId(string documentId, int chunkOrdinal)
    {
        return $"{documentId}_c{chunkOrdinal}";
    }

    /// <summary>
    /// 从文本计算内容哈希（SHA-256 前 16 字符）。
    /// 用于增量判断：内容哈希未变则跳过重建。
    /// </summary>
    public static string ContentHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    /// <summary>
    /// 从业务键字符串确定性推导 ID（SHA-256 前 12 字符 + 前缀）。
    /// </summary>
    private static string DeriveId(string businessKey, string prefix)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(businessKey));
        var hex = Convert.ToHexString(bytes[..6]).ToLowerInvariant();
        return $"{prefix}_{hex}";
    }
}
