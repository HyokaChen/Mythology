using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// VariantResult, AgentToolResult, VariantApplyTarget 测试。
/// </summary>
public class ToolResultTypesTests
{
    // ══════════════════════════════════════════════════════
    //  VariantResult
    // ══════════════════════════════════════════════════════

    [Fact]
    public void VariantResult_Defaults_AreEmpty()
    {
        var v = new VariantResult();
        Assert.Equal("", v.Id);
        Assert.Equal("", v.Label);
        Assert.Equal("", v.Content);
        Assert.Null(v.Description);
        Assert.False(v.IsFailed);
        Assert.Null(v.ReasoningContent);
    }

    [Fact]
    public void VariantResult_WithValues()
    {
        var v = new VariantResult
        {
            Id = "v1",
            Label = "Variant A",
            Content = "Generated content...",
            Description = "A creative variant",
            IsFailed = false,
            ReasoningContent = "I chose this because...",
        };

        Assert.Equal("v1", v.Id);
        Assert.Equal("Variant A", v.Label);
        Assert.Equal("Generated content...", v.Content);
        Assert.Equal("A creative variant", v.Description);
        Assert.False(v.IsFailed);
        Assert.Equal("I chose this because...", v.ReasoningContent);
    }

    [Fact]
    public void VariantResult_Failed_FlagSet()
    {
        var v = new VariantResult { IsFailed = true };
        Assert.True(v.IsFailed);
    }

    [Fact]
    public void VariantResult_RecordEquality()
    {
        var a = new VariantResult { Id = "a", Label = "A" };
        var b = new VariantResult { Id = "a", Label = "A" };
        Assert.Equal(a, b);
    }

    // ══════════════════════════════════════════════════════
    //  AgentToolResult
    // ══════════════════════════════════════════════════════

    [Fact]
    public void AgentToolResult_Defaults()
    {
        var r = new AgentToolResult();
        Assert.Empty(r.Content);
        Assert.Null(r.Details);
        Assert.False(r.IsError);
        Assert.False(r.Terminate);
    }

    [Fact]
    public void AgentToolResult_WithContent()
    {
        var r = new AgentToolResult
        {
            Content = [new Shared.TextContent("result text")],
            IsError = false,
        };
        Assert.Single(r.Content);
        Assert.Equal("result text", (r.Content[0] as Shared.TextContent)!.Text);
    }

    [Fact]
    public void AgentToolResult_ErrorState()
    {
        var r = new AgentToolResult { IsError = true, Terminate = true };
        Assert.True(r.IsError);
        Assert.True(r.Terminate);
    }

    [Fact]
    public void AgentToolResult_WithDetails()
    {
        var details = new { Name = "test" };
        var r = new AgentToolResult { Details = details };
        Assert.Same(details, r.Details);
    }

    // ══════════════════════════════════════════════════════
    //  AgentToolResult<TDetails>
    // ══════════════════════════════════════════════════════

    [Fact]
    public void AgentToolResultTyped_TypedDetails_Accessible()
    {
        var r = new AgentToolResult<string> { Details = "custom detail" };
        Assert.Equal("custom detail", r.Details);
        Assert.Equal("custom detail", r.TypedDetails);
    }

    [Fact]
    public void AgentToolResultTyped_BaseRef_SeesDetails()
    {
        AgentToolResult r = new AgentToolResult<int> { Details = 42 };
        Assert.Equal(42, r.Details);
    }

    [Fact]
    public void AgentToolResultTyped_NullDetails()
    {
        var r = new AgentToolResult<string>();
        Assert.Null(r.TypedDetails);
        Assert.Null(r.Details);
    }

    // ══════════════════════════════════════════════════════
    //  VariantApplyTarget
    // ══════════════════════════════════════════════════════

    [Fact]
    public void VariantApplyTarget_Defaults()
    {
        var t = new VariantApplyTarget();
        Assert.Equal("", t.NovelId);
        Assert.Null(t.ChapterId);
        Assert.Equal("", t.Title);
        Assert.Equal(0, t.Order);
        Assert.Null(t.BaseContentHash);
    }

    [Fact]
    public void VariantApplyTarget_WithChapterTarget()
    {
        var t = new VariantApplyTarget
        {
            NovelId = "n1",
            ChapterId = "c1",
            Title = "第一章",
            Order = 1,
        };
        Assert.Equal("n1", t.NovelId);
        Assert.Equal("c1", t.ChapterId);
    }

    // ══════════════════════════════════════════════════════
    //  ExploreDetails
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ExploreDetails_Defaults()
    {
        var d = new ExploreDetails();
        Assert.Empty(d.Variants);
        Assert.Equal("", d.VariantSetId);
        Assert.Equal("", d.ExploreType);
        Assert.Null(d.ApplyTarget);
    }

    [Fact]
    public void ExploreDetails_WithVariants()
    {
        var d = new ExploreDetails
        {
            VariantSetId = "vs1",
            ExploreType = "plot_variation",
            Variants =
            [
                new VariantResult { Id = "v1", Label = "Option A" },
                new VariantResult { Id = "v2", Label = "Option B" },
            ],
        };
        Assert.Equal(2, d.Variants.Count);
        Assert.Equal("Option A", d.Variants[0].Label);
        Assert.Equal("vs1", d.VariantSetId);
    }

    [Fact]
    public void ExploreDetails_ApplyTarget()
    {
        var target = new VariantApplyTarget { NovelId = "n1", ChapterId = "c1" };
        var d = new ExploreDetails { ApplyTarget = target };
        Assert.Equal("c1", d.ApplyTarget!.ChapterId);
    }
}
