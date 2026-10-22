using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// EditHunk, StringHelper, FileConflictException 综合测试。
/// </summary>
public class SharedUtilitiesTests
{
    // ══════════════════════════════════════════════════════
    //  EditHunk
    // ══════════════════════════════════════════════════════

    [Fact]
    public void EditHunk_DefaultValues()
    {
        var hunk = new EditHunk();
        Assert.Equal(0, hunk.OldStartLine);
        Assert.Equal("", hunk.NewText);
        Assert.Equal("", hunk.OldText);
    }

    [Fact]
    public void EditHunk_WithValues()
    {
        var hunk = new EditHunk
        {
            OldStartLine = 10,
            OldText = "old content",
            NewText = "new content",
        };

        Assert.Equal(10, hunk.OldStartLine);
        Assert.Equal("old content", hunk.OldText);
        Assert.Equal("new content", hunk.NewText);
    }

    [Fact]
    public void EditHunk_FromFullContent_CreatesHunk()
    {
        var hunk = EditHunk.FromFullContent("old", "new");
        Assert.Equal(1, hunk.OldStartLine);
        Assert.Equal("old", hunk.OldText);
        Assert.Equal("new", hunk.NewText);
    }

    // ══════════════════════════════════════════════════════
    //  FileConflictException
    // ══════════════════════════════════════════════════════

    [Fact]
    public void FileConflictException_WithPath()
    {
        var ex = new FileConflictException("/tmp/test.txt");
        Assert.Contains("/tmp/test.txt", ex.Message);
        Assert.Equal("/tmp/test.txt", ex.FilePath);
    }

    [Fact]
    public void FileConflictException_WithPathAndMessage()
    {
        var ex = new FileConflictException("/tmp/test.txt", "custom message");
        Assert.Equal("custom message", ex.Message);
        Assert.Equal("/tmp/test.txt", ex.FilePath);
    }

    // ══════════════════════════════════════════════════════
    //  StringHelper
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        var result = "hello".Truncate(10);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Truncate_LongString_AddsEllipsis()
    {
        var result = "hello world this is long".Truncate(8);
        Assert.True(result.Length <= 8);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void Truncate_Empty_ReturnsEmpty()
    {
        var result = "".Truncate(5);
        Assert.Equal("", result);
    }

    [Fact]
    public void Truncate_ExactLength_ReturnsUnchanged()
    {
        var result = "hello".Truncate(5);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Truncate_CustomSuffix()
    {
        var result = "hello world".Truncate(7, "...");
        Assert.Equal("hell...", result);
    }

    // ══════════════════════════════════════════════════════
    //  ProductInfo
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ProductInfo_Name_IsMythology()
    {
        Assert.Equal("Mythology", ProductInfo.Name);
    }

    [Fact]
    public void ProductInfo_ToolCommandName_IsMythology()
    {
        Assert.Equal("mythology", ProductInfo.ToolCommandName);
    }

    [Fact]
    public void ProductInfo_CurrentVersion_IsNotEmpty()
    {
        Assert.NotEmpty(ProductInfo.CurrentVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+", ProductInfo.CurrentVersion);
    }

    // ══════════════════════════════════════════════════════
    //  ContextOverflowException
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ContextOverflowException_WithMessage()
    {
        var ex = new Agent.ContextOverflowException("context too large");
        Assert.Contains("context too large", ex.Message);
    }

    [Fact]
    public void ContextOverflowException_WithInnerAndHttpCode()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new Agent.ContextOverflowException("overflow", inner, 400);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal(400, ex.HttpStatusCode);
    }

    // ══════════════════════════════════════════════════════
    //  StabilityStatus
    // ══════════════════════════════════════════════════════

    [Fact]
    public void StabilityStatus_EnumValues()
    {
        var values = Enum.GetValues<Stability.StabilityStatus>();
        Assert.Equal(5, values.Length);
        Assert.Contains(Stability.StabilityStatus.Stable, values);
        Assert.Contains(Stability.StabilityStatus.Warning, values);
        Assert.Contains(Stability.StabilityStatus.Unstable, values);
        Assert.Contains(Stability.StabilityStatus.Tripped, values);
        Assert.Contains(Stability.StabilityStatus.Frozen, values);
    }

    // ══════════════════════════════════════════════════════
    //  ToolCategory
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ToolCategory_EnumValues()
    {
        var values = Enum.GetValues<Tools.ToolCategory>();
        Assert.NotEmpty(values);
    }
}
