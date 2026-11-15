namespace NovelTools.Tests;

internal static class ProseTestContent
{
    public static string WithSeparators(int separatorCount) =>
        string.Join(
            "\n\n---\n\n",
            Enumerable.Range(0, separatorCount + 1).Select(i => $"{new string('甲', 100)}{i}")
        );

    public static string WithoutSeparators(string content) =>
        content.Replace("\n\n---\n\n", "\n\n", StringComparison.Ordinal);
}
