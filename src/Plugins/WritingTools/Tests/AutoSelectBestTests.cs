using Core.Data;
using Plugins.WritingTools;
using Xunit;

namespace WritingTools.Tests;

public sealed class AutoSelectBestTests
{
    private static WritingProfile CreateProfile(string enhancementProfile)
    {
        var novel = new Novel
        {
            Id = "n1",
            EnhancementProfile = enhancementProfile,
            TargetAudience = enhancementProfile,
        };
        return WritingProfile.FromNovel(novel);
    }

    [Fact]
    public void Select_CommercialProfile_ReturnsModerate()
    {
        var candidates = new List<EnhancementCandidate>
        {
            new()
            {
                Id = "conservative",
                Label = "保守版",
                Content = "保守内容",
                Description = "保守",
            },
            new()
            {
                Id = "moderate",
                Label = "适度版",
                Content = "适度内容",
                Description = "适度",
            },
        };

        var selected = AutoSelectBest.Select(candidates, CreateProfile("commercial"));
        Assert.NotNull(selected);
        Assert.Equal("moderate", selected.Id);
    }

    [Fact]
    public void Select_LiteraryProfile_ReturnsConservative()
    {
        var candidates = new List<EnhancementCandidate>
        {
            new()
            {
                Id = "conservative",
                Label = "保守版",
                Content = "保守内容",
                Description = "保守",
            },
            new()
            {
                Id = "moderate",
                Label = "适度版",
                Content = "适度内容",
                Description = "适度",
            },
        };

        var selected = AutoSelectBest.Select(candidates, CreateProfile("literary"));
        Assert.NotNull(selected);
        Assert.Equal("conservative", selected.Id);
    }

    [Fact]
    public void Select_BalancedProfile_ReturnsConservative()
    {
        var candidates = new List<EnhancementCandidate>
        {
            new()
            {
                Id = "conservative",
                Label = "保守版",
                Content = "保守内容",
                Description = "保守",
            },
            new()
            {
                Id = "moderate",
                Label = "适度版",
                Content = "适度内容",
                Description = "适度",
            },
        };

        var selected = AutoSelectBest.Select(candidates, CreateProfile("balanced"));
        Assert.NotNull(selected);
        Assert.Equal("conservative", selected.Id);
    }

    [Fact]
    public void Select_SingleCandidate_ReturnsThat()
    {
        var candidates = new List<EnhancementCandidate>
        {
            new()
            {
                Id = "only",
                Label = "唯一版",
                Content = "内容",
                Description = "唯一",
            },
        };

        var selected = AutoSelectBest.Select(candidates, CreateProfile("commercial"));
        Assert.Equal("only", selected!.Id);
    }

    [Fact]
    public void Select_EmptyCandidates_ReturnsNull()
    {
        var selected = AutoSelectBest.Select([], CreateProfile("commercial"));
        Assert.Null(selected);
    }
}
