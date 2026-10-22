using Core.Shared;
using Core.Tools;
using Plugins.FileSystem.Tools;
using Xunit;

namespace FileSystem.Tests;

public class ReadFileToolTests : IDisposable
{
    private readonly string _dir;
    private readonly ReadFileTool _tool = new();

    public ReadFileToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ReadFileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private string FilePath(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task ExecuteAsync_TextFile_ReturnsContent()
    {
        var path = FilePath("test.txt");
        await File.WriteAllTextAsync(path, "hello world", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new ReadFileParams { Path = path },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("hello world", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_FileNotFound_ReturnsError()
    {
        var path = FilePath("nonexistent.txt");

        var result = await _tool.ExecuteAsync(
            "call_1",
            new ReadFileParams { Path = path },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
        Assert.Contains("不存在", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_Directory_ReturnsListing()
    {
        await File.WriteAllTextAsync(FilePath("a.txt"), "a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(FilePath("b.txt"), "b", TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new ReadFileParams { Path = _dir },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("a.txt", text);
        Assert.Contains("b.txt", text);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyDirectory_ShowsEmpty()
    {
        var emptyDir = FilePath("empty");
        Directory.CreateDirectory(emptyDir);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new ReadFileParams { Path = emptyDir },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("空目录", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_WithOffset_ReadsFromLine()
    {
        var path = FilePath("lines.txt");
        var lines = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line{i}"));
        await File.WriteAllTextAsync(path, lines, TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new ReadFileParams { Path = path, Offset = 6 },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        // ReadFileTool header shows starting line, body should have line6
        Assert.Contains("line6", text);
        // The first content line should be line6 (not line1 or line5)
        var contentLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var firstContentLine = contentLines.FirstOrDefault(l => l.StartsWith("line"));
        Assert.StartsWith("line6", firstContentLine);
    }

    [Fact]
    public async Task ExecuteAsync_BinaryFile_DetectsBinary()
    {
        var path = FilePath("binary.bin");
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        var result = await _tool.ExecuteAsync(
            "call_1",
            new ReadFileParams { Path = path },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("二进制文件", GetText(result));
    }

    private static string GetText(AgentToolResult result) =>
        result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
