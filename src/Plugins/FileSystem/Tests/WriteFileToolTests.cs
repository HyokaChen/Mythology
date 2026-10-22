using Core.Shared;
using Core.Tools;
using Plugins.FileSystem.Tools;
using Xunit;

namespace FileSystem.Tests;

public class WriteFileToolTests : IDisposable
{
    private readonly string _dir;
    private readonly WriteFileTool _tool = new();

    public WriteFileToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "WriteFileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private string FilePath(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task ExecuteAsync_CreatesNewFile()
    {
        var path = FilePath("new.txt");
        var result = await _tool.ExecuteAsync(
            "call_1",
            new WriteFileParams { Path = path, Content = "hello" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.True(File.Exists(path));
        Assert.Equal(
            "hello",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
        );
        Assert.Contains("已写入", GetText(result));
    }

    [Fact]
    public async Task ExecuteAsync_OverwritesExisting()
    {
        var path = FilePath("existing.txt");
        await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);

        await _tool.ExecuteAsync(
            "call_1",
            new WriteFileParams { Path = path, Content = "new" },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            "new",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ExecuteAsync_CreatesParentDirectories()
    {
        var path = FilePath("sub/deep/new.txt");

        var result = await _tool.ExecuteAsync(
            "call_1",
            new WriteFileParams { Path = path, Content = "content" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyContent_CreatesEmptyFile()
    {
        var path = FilePath("empty.txt");

        var result = await _tool.ExecuteAsync(
            "call_1",
            new WriteFileParams { Path = path, Content = "" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.True(File.Exists(path));
        Assert.Equal("", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    private static string GetText(AgentToolResult result) =>
        result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
