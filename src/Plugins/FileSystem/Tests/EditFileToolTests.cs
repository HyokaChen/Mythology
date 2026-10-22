using Plugins.FileSystem.Tools;
using Xunit;

namespace FileSystem.Tests;

public class EditFileToolTests : IDisposable
{
    private readonly string _dir;
    private readonly EditFileTool _tool = new();

    public EditFileToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "EditFileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private string FilePath(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task ExecuteAsync_SingleEdit_ReplacesText()
    {
        var path = FilePath("edit.txt");
        await File.WriteAllTextAsync(path, "hello world", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new EditFileParams
            {
                Path = path,
                Edits = [new EditOperation { OldText = "world", NewText = "there" }],
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Equal(
            "hello there",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ExecuteAsync_FileNotFound_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new EditFileParams
            {
                Path = FilePath("nonexistent.txt"),
                Edits = [new EditOperation { OldText = "a", NewText = "b" }],
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_NoEdits_ReturnsError()
    {
        var path = FilePath("no_edits.txt");
        await File.WriteAllTextAsync(path, "content", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new EditFileParams { Path = path, Edits = [] },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
        Assert.Equal(
            "content",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ExecuteAsync_OldTextNotFound_ReportsFailure()
    {
        var path = FilePath("nomatch.txt");
        await File.WriteAllTextAsync(path, "hello", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new EditFileParams
            {
                Path = path,
                Edits = [new EditOperation { OldText = "nonexistent", NewText = "replacement" }],
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError); // Tool doesn't error overall, reports per-edit
        Assert.Equal(
            "hello",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
        ); // Content unchanged
    }

    [Fact]
    public async Task ExecuteAsync_MultipleEdits_AppliesAll()
    {
        var path = FilePath("multi.txt");
        await File.WriteAllTextAsync(path, "a b c", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new EditFileParams
            {
                Path = path,
                Edits =
                [
                    new EditOperation { OldText = "a", NewText = "x" },
                    new EditOperation { OldText = "c", NewText = "z" },
                ],
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Equal(
            "x b z",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ExecuteAsync_EmptyOldText_SkipsEdit()
    {
        var path = FilePath("empty_old.txt");
        await File.WriteAllTextAsync(path, "content", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new EditFileParams
            {
                Path = path,
                Edits = [new EditOperation { OldText = "", NewText = "new" }],
            },
            TestContext.Current.CancellationToken
        );

        // Content unchanged (empty oldText is skipped)
        Assert.Equal(
            "content",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ExecuteAsync_CrlfFile_LfOldText_MatchesAndPreservesCrlf()
    {
        // 回归：Windows 下文件为 CRLF，LLM 的 oldText 用 \n —— 必须能匹配且写回保持 CRLF
        var path = FilePath("crlf.txt");
        await File.WriteAllTextAsync(
            path,
            "第一行\r\n\r\n重复段。\r\n\r\n重复段。\r\n\r\n结尾",
            TestContext.Current.CancellationToken
        );

        var result = await _tool.ExecuteAsync(
            "call_crlf",
            new EditFileParams
            {
                Path = path,
                Edits =
                [
                    new EditOperation
                    {
                        OldText = "重复段。\n\n重复段。",
                        NewText = "去重后的段落。",
                    },
                ],
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var after = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("第一行\r\n\r\n去重后的段落。\r\n\r\n结尾", after);
    }

    [Fact]
    public async Task ExecuteAsync_CrlfFile_MixedLineEndingsInOldText_Matches()
    {
        var path = FilePath("crlf_mixed.txt");
        await File.WriteAllTextAsync(path, "甲\r\n乙\r\n丙", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_mixed",
            new EditFileParams
            {
                Path = path,
                Edits = [new EditOperation { OldText = "甲\n乙", NewText = "甲乙" }],
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Equal(
            "甲乙\r\n丙",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
        );
    }
}
