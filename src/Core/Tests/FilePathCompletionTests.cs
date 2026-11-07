using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// @ 文件路径补全共享口径（TUI 与 Desktop 同一实现）：
/// 基准目录列举、层级导航、含空格目录名（回归锁）、绝对/~/路径、无匹配降级、
/// 条目同时带展示用绝对路径（Desktop 弹窗右列）。
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

    /// <summary>条目名清单（插入输入框的文本）。</summary>
    private static string[] Names(FileEntry[] entries) => entries.Select(e => e.Name).ToArray();

    [Fact]
    public void EmptyQuery_ListsRootEntries()
    {
        var names = Names(FilePathCompletion.GetEntries("", _root));

        Assert.Contains("卷一 风起/", names);
        Assert.Contains("settings/", names);
        Assert.Contains("序章.txt", names);
    }

    [Fact]
    public void Prefix_FiltersEntries()
    {
        var names = Names(FilePathCompletion.GetEntries("卷一", _root));

        Assert.Equal("卷一 风起/", Assert.Single(names));
    }

    [Fact]
    public void Subdirectory_Slash_NavigatesNextLevel()
    {
        // 尾 / 后接子目录前缀 → 列子目录内容（层级导航核心路径）
        var names = Names(FilePathCompletion.GetEntries("卷一 风起/细", _root));

        Assert.Equal("细纲.md", Assert.Single(names));
    }

    [Fact]
    public void DirectoryName_WithSpace_ContinuesNavigation()
    {
        // B2 回归锁：含空格目录名插入（尾带 /）后继续输入 → 仍能命中下一级
        var names = Names(FilePathCompletion.GetEntries("卷一 风起/", _root));

        Assert.Contains("细纲.md", names);
    }

    [Fact]
    public void DirectoryEntry_MatchesPrefix_WithoutTrailingSlash()
    {
        // 前缀「卷一 风起」无尾 / 时也能命中目录候选「卷一 风起/」
        var names = Names(FilePathCompletion.GetEntries("卷一 风起", _root));

        Assert.Contains("卷一 风起/", names);
    }

    [Fact]
    public void Entry_CarriesAbsolutePath_ForDisplay()
    {
        // 相对查询：FullPath 落在基准目录下（Desktop 弹窗右列展示）
        var entry = Assert.Single(FilePathCompletion.GetEntries("序章", _root));
        Assert.Equal("序章.txt", entry.Name);
        Assert.Equal(Path.Combine(_root, "序章.txt"), entry.FullPath);

        // 绝对查询：FullPath 落在所列举目录下，而不是错误地挂到基准目录
        var rootEntries = FilePathCompletion.GetEntries("/", _root);
        Assert.NotEmpty(rootEntries);
        Assert.All(rootEntries, e => Assert.True(Path.IsPathRooted(e.FullPath)));
    }

    [Fact]
    public void AbsolutePath_ListsRootDirectories()
    {
        var names = Names(FilePathCompletion.GetEntries("/", _root));

        Assert.NotEmpty(names);
    }

    [Fact]
    public void MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(FilePathCompletion.GetEntries("不存在/", _root));
        Assert.Empty(FilePathCompletion.GetEntries("", Path.Combine(_root, "不存在")));
    }

    [Fact]
    public void Resolve_SplitsDirectoryAndPrefix()
    {
        Assert.Equal((_root, ""), FilePathCompletion.Resolve("", _root));
        Assert.Equal((_root, "卷一"), FilePathCompletion.Resolve("卷一", _root));
        Assert.Equal(
            (Path.Combine(_root, "卷一 风起"), ""),
            FilePathCompletion.Resolve("卷一 风起/", _root)
        );
    }

    [Fact]
    public void IsDirectoryEntry_And_NormalizeSeparator()
    {
        Assert.True(FilePathCompletion.IsDirectoryEntry("abc/"));
        Assert.False(FilePathCompletion.IsDirectoryEntry("abc.txt"));
        Assert.Equal(
            "abc" + Path.DirectorySeparatorChar,
            FilePathCompletion.NormalizeSeparator("abc/")
        );
        Assert.Equal("abc.txt", FilePathCompletion.NormalizeSeparator("abc.txt"));
    }
}
