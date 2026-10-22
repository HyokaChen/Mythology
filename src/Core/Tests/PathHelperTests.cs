using Core.Shared;
using Xunit;

namespace Core.Tests;

public class PathHelperTests
{
    [Fact]
    public void ResolvePath_AbsolutePath_ReturnsAsIs()
    {
        // Use an OS-appropriate absolute path
        var absPath = Path.GetFullPath("/tmp");
        var result = PathHelper.ResolvePath(absPath);
        Assert.Equal(absPath, result);
    }

    [Fact]
    public void ResolvePath_RootedUnixPath_ReturnsAsIs()
    {
        var result = PathHelper.ResolvePath("/usr/local/file.txt");
        // On Unix, /usr/local/file.txt is rooted → returned as-is
        // On Windows, it's not rooted → combined with CWD
        Assert.Contains("usr", result);
    }

    [Fact]
    public void ResolvePath_AtPrefix_ResolvesToWorkingDirectory()
    {
        var result = PathHelper.ResolvePath("@file.txt");
        var cwd = Directory.GetCurrentDirectory();
        Assert.StartsWith(cwd, result);
        Assert.EndsWith("file.txt", result);
    }

    [Fact]
    public void ResolvePath_AtSlashPrefix_StripsLeadingSlash()
    {
        var result = PathHelper.ResolvePath("@/file.txt");
        var cwd = Directory.GetCurrentDirectory();
        Assert.StartsWith(cwd, result);
        Assert.EndsWith("file.txt", result);
    }

    [Fact]
    public void ResolvePath_AtBackslashPrefix_StripsLeadingBackslash()
    {
        var result = PathHelper.ResolvePath("@\\file.txt");
        var cwd = Directory.GetCurrentDirectory();
        Assert.StartsWith(cwd, result);
        Assert.EndsWith("file.txt", result);
    }

    [Fact]
    public void ResolvePath_AtPrefixWithSubdirectory_ResolvesCorrectly()
    {
        var relative = Path.Combine("subdir", "file.txt");
        var result = PathHelper.ResolvePath("@" + relative);
        var cwd = Directory.GetCurrentDirectory();
        Assert.StartsWith(cwd, result);
        Assert.EndsWith(relative, result);
    }

    [Fact]
    public void ResolvePath_RelativePath_ResolvesToWorkingDirectory()
    {
        var relative = Path.Combine("relative", "path.txt");
        var result = PathHelper.ResolvePath(relative);
        var cwd = Directory.GetCurrentDirectory();
        Assert.StartsWith(cwd, result);
        Assert.EndsWith(relative, result);
    }

    [Fact]
    public void ResolvePath_EmptyPath_ReturnsCwd()
    {
        var result = PathHelper.ResolvePath("");
        var cwd = Directory.GetCurrentDirectory();
        Assert.Equal(cwd, result);
    }

    [Fact]
    public void ResolvePath_AtOnly_ReturnsWorkingDirectory()
    {
        var result = PathHelper.ResolvePath("@");
        var cwd = Directory.GetCurrentDirectory();
        Assert.Equal(cwd, result);
    }
}
