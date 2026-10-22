using Core.Shared;
using Xunit;

namespace Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public AtomicFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "AtomicFileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "test.txt");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task WriteAllTextAsync_CreatesFile()
    {
        await AtomicFile.WriteAllTextAsync(_file, "hello", TestContext.Current.CancellationToken);
        Assert.True(File.Exists(_file));
        var content = await File.ReadAllTextAsync(_file, TestContext.Current.CancellationToken);
        Assert.Equal("hello", content);
    }

    [Fact]
    public async Task WriteAllTextAsync_OverwritesExisting()
    {
        await File.WriteAllTextAsync(_file, "old content", TestContext.Current.CancellationToken);
        await AtomicFile.WriteAllTextAsync(
            _file,
            "new content",
            TestContext.Current.CancellationToken
        );
        var content = await File.ReadAllTextAsync(_file, TestContext.Current.CancellationToken);
        Assert.Equal("new content", content);
    }

    [Fact]
    public async Task ReadAllTextAsync_ReturnsContent()
    {
        await File.WriteAllTextAsync(_file, "test content", TestContext.Current.CancellationToken);
        var content = await AtomicFile.ReadAllTextAsync(
            _file,
            TestContext.Current.CancellationToken
        );
        Assert.Equal("test content", content);
    }

    [Fact]
    public async Task ReadAllTextAsync_TracksContent()
    {
        await File.WriteAllTextAsync(_file, "test", TestContext.Current.CancellationToken);
        await AtomicFile.ReadAllTextAsync(_file, TestContext.Current.CancellationToken);
        // Should not throw - content is tracked
        await AtomicFile.EnsureUnchangedAsync(_file, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppendLineAsync_AppendsToFile()
    {
        await AtomicFile.WriteAllTextAsync(_file, "line1\n", TestContext.Current.CancellationToken);
        await AtomicFile.AppendLineAsync(_file, "line2", ct: TestContext.Current.CancellationToken);
        var content = await File.ReadAllTextAsync(_file, TestContext.Current.CancellationToken);
        Assert.Equal("line1\nline2\n", content);
    }

    [Fact]
    public async Task AppendLineAsync_NewFile_CreatesAndAppends()
    {
        await AtomicFile.AppendLineAsync(
            _file,
            "first line",
            ct: TestContext.Current.CancellationToken
        );
        Assert.True(File.Exists(_file));
        var content = await File.ReadAllTextAsync(_file, TestContext.Current.CancellationToken);
        Assert.Equal("first line\n", content);
    }

    [Fact]
    public async Task EnsureUnchangedAsync_UntrackedFile_DoesNotThrow()
    {
        await File.WriteAllTextAsync(_file, "content", TestContext.Current.CancellationToken);
        // File not tracked yet, should not throw
        await AtomicFile.EnsureUnchangedAsync(_file, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureUnchangedAsync_DeletedFile_DoesNotThrow()
    {
        await AtomicFile.WriteAllTextAsync(_file, "content", TestContext.Current.CancellationToken);
        File.Delete(_file);
        // File was deleted, should not throw
        await AtomicFile.EnsureUnchangedAsync(_file, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureUnchangedAsync_ModifiedFile_Throws()
    {
        await AtomicFile.WriteAllTextAsync(
            _file,
            "original",
            TestContext.Current.CancellationToken
        );
        // External modification
        await File.WriteAllTextAsync(_file, "modified", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FileConflictException>(() =>
            AtomicFile.EnsureUnchangedAsync(_file, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task WriteAllTextAsync_AtomicWrite_Succeeds()
    {
        await AtomicFile.WriteAllTextAsync(_file, "content", TestContext.Current.CancellationToken);
        Assert.True(File.Exists(_file));
    }

    [Fact]
    public async Task AppendTextLockedAsync_AppendsWithLock()
    {
        await AtomicFile.WriteAllTextAsync(_file, "start\n", TestContext.Current.CancellationToken);
        await AtomicFile.AppendTextLockedAsync(
            _file,
            "appended",
            checkConflict: false,
            ct: TestContext.Current.CancellationToken
        );
        var content = await File.ReadAllTextAsync(_file, TestContext.Current.CancellationToken);
        Assert.Contains("appended", content);
    }

    [Fact]
    public async Task AppendLinesLockedAsync_AppendsMultipleLines()
    {
        await AtomicFile.WriteAllTextAsync(_file, "start\n", TestContext.Current.CancellationToken);
        await AtomicFile.AppendLinesLockedAsync(
            _file,
            ["line1", "line2"],
            checkConflict: false,
            ct: TestContext.Current.CancellationToken
        );
        var content = await File.ReadAllTextAsync(_file, TestContext.Current.CancellationToken);
        Assert.Contains("line1", content);
        Assert.Contains("line2", content);
    }

    [Fact]
    public async Task AppendLinesDurableAsync_AppendsBatch()
    {
        await AtomicFile.WriteAllTextAsync(_file, "start\n", TestContext.Current.CancellationToken);
        await AtomicFile.AppendLinesDurableAsync(
            _file,
            ["line1", "line2"],
            checkConflict: false,
            ct: TestContext.Current.CancellationToken
        );
        var content = await File.ReadAllTextAsync(_file, TestContext.Current.CancellationToken);
        Assert.Equal("start\nline1\nline2\n", content);
    }

    [Fact]
    public async Task AppendLinesDurableAsync_ExternalModification_Throws()
    {
        await AtomicFile.WriteAllTextAsync(_file, "start\n", TestContext.Current.CancellationToken);
        await AtomicFile.AppendLinesDurableAsync(
            _file,
            ["first"],
            checkConflict: true,
            ct: TestContext.Current.CancellationToken
        );
        await File.AppendAllTextAsync(_file, "external\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FileConflictException>(() =>
            AtomicFile.AppendLinesDurableAsync(
                _file,
                ["second"],
                checkConflict: true,
                ct: TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task TrackContent_ForgetPath_RemovesTracking()
    {
        // 追踪后外部修改 → EnsureUnchangedAsync 抛冲突
        AtomicFile.TrackContent(_file, "content");
        await File.WriteAllTextAsync(_file, "modified", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<FileConflictException>(() =>
            AtomicFile.EnsureUnchangedAsync(_file, TestContext.Current.CancellationToken)
        );

        // ForgetPath 清除追踪 → 同一外部修改不再触发冲突校验
        AtomicFile.ForgetPath(_file);
        await AtomicFile.EnsureUnchangedAsync(_file, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentAppends_DoNotInterleave()
    {
        const int taskCount = 20;
        var tasks = new Task[taskCount];
        for (var i = 0; i < taskCount; i++)
        {
            var line = $"line{i}";
            tasks[i] = AtomicFile.AppendLineAsync(
                _file,
                line,
                checkConflict: false,
                ct: TestContext.Current.CancellationToken
            );
        }
        await Task.WhenAll(tasks);

        var content = await File.ReadAllTextAsync(_file, TestContext.Current.CancellationToken);
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(taskCount, lines.Length);
    }
}
