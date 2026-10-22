namespace Desktop.Services;

/// <summary>
/// 版本号归一化与数值比较 —— 桌面端自动更新的唯一版本比较出口。
/// 本地版本形如 "0.0.9+g60ae392"（AssemblyInformationalVersion），git tag 形如 "v0.0.9"，
/// 必须剥掉构建元数据后按数值比较（字符串比较会把 0.0.9 判成 &gt; 0.0.10）。
///
/// 规则（移植自 LittleKingKnows，实现不得自行发明）：
///   · Normalize：去首尾空白 → 去前导 v/V → 取 + 之前（剥构建元数据）→ 去首尾 .；
///   · Compare：按 . 分段逐段数值比较，缺段补 0；段含非数字后缀（如 9-alpha）取数字前缀
///     （预发布标签不参与排序）；无任何数字前缀的段按 Ordinal 字符串比较兜底。返回 -1/0/1。
/// </summary>
public static class UpdateVersion
{
    /// <summary>归一化版本号：v0.0.9 → 0.0.9；0.0.9+g60ae392 → 0.0.9；null/空白 → 空串。</summary>
    public static string Normalize(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V'))
            value = value[1..];

        var plusIndex = value.IndexOf('+');
        if (plusIndex >= 0)
            value = value[..plusIndex];

        return value.Trim('.');
    }

    /// <summary>比较两个版本（先各自归一化）。返回 -1（a&lt;b）/ 0（相等）/ 1（a&gt;b）。</summary>
    public static int Compare(string? a, string? b)
    {
        var left = Normalize(a).Split('.', StringSplitOptions.RemoveEmptyEntries);
        var right = Normalize(b).Split('.', StringSplitOptions.RemoveEmptyEntries);

        var maxSegments = Math.Max(left.Length, right.Length);
        for (var i = 0; i < maxSegments; i++)
        {
            var leftSegment = i < left.Length ? left[i] : "0";
            var rightSegment = i < right.Length ? right[i] : "0";

            var leftNumber = ParseSegment(leftSegment);
            var rightNumber = ParseSegment(rightSegment);
            if (leftNumber.HasValue && rightNumber.HasValue)
            {
                if (leftNumber.Value != rightNumber.Value)
                    return leftNumber.Value < rightNumber.Value ? -1 : 1;
            }
            else
            {
                // 段不含可解析数字（如 "alpha"）：整段 Ordinal 兜底
                var ordinal = string.Compare(leftSegment, rightSegment, StringComparison.Ordinal);
                if (ordinal != 0)
                    return ordinal < 0 ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// 解析单段：整段数字直接返回；带非数字后缀（9-alpha）取数字前缀（预发布标签不参与比较）；
    /// 无数字前缀返回 null。
    /// </summary>
    private static int? ParseSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty)
            return 0;

        if (int.TryParse(segment, out var whole))
            return whole;

        var digitsEnd = 0;
        while (digitsEnd < segment.Length && char.IsAsciiDigit(segment[digitsEnd]))
            digitsEnd++;

        return digitsEnd == 0 ? null
            : int.TryParse(segment[..digitsEnd], out var prefix) ? prefix
            : null;
    }
}
