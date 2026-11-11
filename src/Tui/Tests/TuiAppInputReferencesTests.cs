using Xunit;

namespace Tui.Tests;

/// <summary>
/// @ 文件引用保留来源标记：模型只能读取用户当前消息中明确出现的路径，
/// 不能把裸路径当成自己可自由探索的文件系统入口。
/// </summary>
public sealed class TuiAppInputReferencesTests
{
    [Fact]
    public void InlineFileReferences_RelativePath_PreservesAtPrefix()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mythology-at-ref-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "user referenced");
        try
        {
            var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path)
                .Replace('\\', '/');

            var processed = TuiApp.InlineFileReferences($"看看 @{relative}");

            Assert.Contains("@" + relative, processed, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InlineFileReferences_AbsolutePath_DoesNotAddAtPrefix()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mythology-at-ref-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "user referenced");
        try
        {
            var processed = TuiApp.InlineFileReferences($"看看 @{path}");

            Assert.Contains(path, processed, StringComparison.Ordinal);
            Assert.DoesNotContain("@" + path, processed, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
