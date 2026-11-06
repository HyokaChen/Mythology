namespace Desktop.Services;

/// <summary>
/// 密钥展示脱敏：默认隐藏主体，仅保留末 4 位用于区分不同密钥。
/// 展示态摘要与编辑态输入框共用同一口径。
/// </summary>
internal static class SecretMask
{
    public const int VisibleTailLength = 4;
    private const int MaskLength = 8;

    public static string Format(string? secret, string emptyText = "未填写")
    {
        if (string.IsNullOrWhiteSpace(secret))
            return emptyText;

        return secret.Length <= VisibleTailLength
            ? new string('•', secret.Length)
            : $"{new string('•', MaskLength)}{secret[^VisibleTailLength..]}";
    }
}
