using Core.Data;
using Core.Shared;
using Plugins.WritingTools;
using Xunit;

namespace WritingTools.Tests;

public sealed class AutoSelectBestTests
{
    private static WritingProfile CreateProfile(
        string enhancementProfile,
        string autoEnhance = "semi"
    )
    {
        var novel = new Novel
        {
            Id = "n1",
            EnhancementProfile = enhancementProfile,
            TargetAudience = enhancementProfile,
            AutoEnhance = autoEnhance,
        };
        return WritingProfile.FromNovel(novel);
    }

    private static List<EnhancementCandidate> HumanizerCandidates(
        bool conservative = true,
        bool moderate = true
    )
    {
        var candidates = new List<EnhancementCandidate>();
        if (conservative)
            candidates.Add(
                new()
                {
                    Id = "conservative",
                    Label = "保守版",
                    Content = "保守内容",
                    Description = "保守",
                }
            );
        if (moderate)
            candidates.Add(
                new()
                {
                    Id = "moderate",
                    Label = "适度版",
                    Content = "适度内容",
                    Description = "适度",
                }
            );
        return candidates;
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

    [Fact]
    public void Select_HumanizerSemiProfile_ReturnsNull()
    {
        var selected = AutoSelectBest.Select(
            HumanizerCandidates(),
            CreateProfile("commercial", "semi"),
            DimensionKeys.Humanizer,
            "formulaic_pattern",
            "A"
        );
        Assert.Null(selected);
    }

    [Fact]
    public void Select_HumanizerFullProfile_LevelA_ReturnsConservative()
    {
        var selected = AutoSelectBest.Select(
            HumanizerCandidates(),
            CreateProfile("commercial", "full"),
            DimensionKeys.Humanizer,
            "formulaic_pattern",
            "A"
        );
        Assert.NotNull(selected);
        Assert.Equal("conservative", selected.Id);
    }

    [Fact]
    public void Select_HumanizerFullProfile_LevelB_ReturnsConservative()
    {
        var selected = AutoSelectBest.Select(
            HumanizerCandidates(),
            CreateProfile("commercial", "full"),
            DimensionKeys.Humanizer,
            "formulaic_pattern",
            "B"
        );
        Assert.NotNull(selected);
        Assert.Equal("conservative", selected.Id);
    }

    [Fact]
    public void Select_HumanizerFullProfile_LevelF_ReturnsNull()
    {
        var selected = AutoSelectBest.Select(
            HumanizerCandidates(),
            CreateProfile("commercial", "full"),
            DimensionKeys.Humanizer,
            "formulaic_pattern",
            "F"
        );
        Assert.Null(selected);
    }

    [Fact]
    public void Select_HumanizerFullProfile_LevelNull_ReturnsNull()
    {
        var selected = AutoSelectBest.Select(
            HumanizerCandidates(),
            CreateProfile("commercial", "full"),
            DimensionKeys.Humanizer,
            "formulaic_pattern"
        );
        Assert.Null(selected);
    }

    [Fact]
    public void Select_HumanizerFullProfile_NoConservative_ReturnsNull()
    {
        var candidates = HumanizerCandidates(conservative: false);
        candidates.Add(
            new()
            {
                Id = "enhanced",
                Label = "增强版",
                Content = "增强内容",
                Description = "增强",
            }
        );

        var selected = AutoSelectBest.Select(
            candidates,
            CreateProfile("commercial", "full"),
            DimensionKeys.Humanizer,
            "formulaic_pattern",
            "A"
        );
        Assert.Null(selected);
    }

    [Fact]
    public void Select_HumanizerFullProfile_SingleModerate_ReturnsNull()
    {
        var selected = AutoSelectBest.Select(
            HumanizerCandidates(conservative: false),
            CreateProfile("commercial", "full"),
            DimensionKeys.Humanizer,
            "formulaic_pattern",
            "A"
        );
        Assert.Null(selected);
    }

    [Fact]
    public void Select_HumanizerFullProfile_SingleConservative_ReturnsIt()
    {
        var selected = AutoSelectBest.Select(
            HumanizerCandidates(moderate: false),
            CreateProfile("commercial", "full"),
            DimensionKeys.Humanizer,
            "formulaic_pattern",
            "A"
        );
        Assert.NotNull(selected);
        Assert.Equal("conservative", selected.Id);
    }
}
