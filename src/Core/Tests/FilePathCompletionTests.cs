using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// @ 文件路径补全共享口径（TUI 与 Desktop 同一实现）：
/// 基准目录列举、层级导航、含空格目录名（回归锁）、绝对/~/路径、无匹配降级。
/// </summary>
public sealed class FilePathCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"myth-path-completion-{Guid.NewGuid():N}"
    );

    public FilePathCompletionTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "卷一 风起"));
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        File.WriteAllText(Path.Combine(_root, "序章.txt"), "内容");
        File.WriteAllText(Path.Combine(_root, "卷一 风起", "细纲.md"), "内容");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void EmptyQuery_ListsRootEntries()
    {
        var entries = FilePathCompletion.GetEntries("", _root);

        Assert.Contains("卷一 风起/", entries);
        Assert.Contains("settings/", entries);
        Assert.Contains("序章.txt", entries);
    }

    [Fact]
    public void Prefix_FiltersEntries()
    {
        var entries = FilePathCompletion.GetEntries("卷一", _root);

        var entry = Assert.Single(entries);
        Assert.Equal("卷一 风起/", entry);
    }

    [Fact]
    public void Subdirectory_Slash_NavigatesNextLevel()
    {
        // 尾 / 后接子目录前缀 → 列子目录内容（层级导航核心路径）
        var query = $"卷一 风起/细";
        var entries = FilePathCompletion.GetEntries(query, _root);

        var entry = Assert.Single(entries);
        Assert.Equal("细纲.md", entry);
    }

    [Fact]
    public void DirectoryName_WithSpace_ContinuesNavigation()
    {
        // B2 回归锁：含空格目录名插入（尾带 /）后继续输入 → 仍能命中下一级
        var query = $"卷一 风起/";
        var entries = FilePathCompletion.GetEntries(query, _root);

        Assert.Contains("细纲.md", entries);
    }

    [Fact]
    public void DirectoryEntry_MatchesPrefix_WithoutTrailingSlash()
    {
        // 前缀「卷一 风起」无尾 / 时也能命中目录候选「卷一 风起/」
        var entries = FilePathCompletion.GetEntries("卷一 风起", _root);

        Assert.Contains("卷一 风起/", entries);
    }

    [Fact]
    public void AbsolutePath_ListsRootDirectories()
    {
        var entries = FilePathCompletion.GetEntries("/", _root);

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(FilePathCompletion.IsDirectoryEntry(e) || !e.Contains('\\')));
    }

    [Fact]
    public void MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(FilePathCompletion.GetEntries("不存在/", _root));
        Assert.Empty(FilePathCompletion.GetEntries("", Path.Combine(_root, "不存在")));
    }

    [Fact]
    public void IsDirectoryEntry_And_NormalizeSeparator()
    {
        Assert.True(FilePathCompletion.IsDirectoryEntry("abc/"));
        Assert.False(FilePathCompletion.IsDirectoryEntry("abc.txt"));
        var normalized = FilePathCompletion.NormalizeSeparator("abc/");
        Assert.Equal("abc" + Path.DirectorySeparatorChar, normalized);
        Assert.Equal("abc.txt", FilePathCompletion.NormalizeSeparator("abc.txt"));
    }
}
