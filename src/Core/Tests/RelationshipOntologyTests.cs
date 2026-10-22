using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 关系本体（ RelationshipOntology）：词表覆盖、域/值域门、云端两通道 token 口径、
/// 有向性、结构边词汇——这些是候选封锁与证据闸门的单一事实源。
/// </summary>
public sealed class RelationshipOntologyTests
{
    [Fact]
    public void AllowedTokens_AreSubsetOfOntology_ForEveryEntityPair()
    {
        // 契约：扫描判据（RelationshipScanTool.ResolveCriteria）直接由 Allowed 生成，
        // 因此这里锁住 Allowed 的 token 一律来自本体自身，且 none/uncertain 不混进本体。
        var known = RelationshipOntology
            .Relations.Select(r => r.Token)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(RelationshipOntology.NoneToken, known);
        Assert.DoesNotContain(RelationshipOntology.UncertainToken, known);

        foreach (var from in Enum.GetValues<EntityType>())
        {
            foreach (var to in Enum.GetValues<EntityType>())
            {
                foreach (var spec in RelationshipOntology.Allowed(from, to))
                {
                    Assert.Contains(spec.Token, known);
                    Assert.False(string.IsNullOrWhiteSpace(spec.Description));
                }
            }
        }
    }

    [Fact]
    public void Tokens_CoverEveryRelationshipType_AndAreUnique()
    {
        var types = Enum.GetValues<RelationshipType>();
        Assert.Equal(types.Length, RelationshipOntology.Relations.Count);

        var tokens = RelationshipOntology.Tokens;
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            types,
            type => Assert.Contains(type.ToKebabCaseString(), tokens, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void NarrativeRelations_AllowCharacterPairsOnly()
    {
        // 本地 Relationship 只有角色端点 → 叙事关系的域/值域就是角色×角色
        Assert.Equal(
            RelationshipOntology.Relations.Count,
            RelationshipOntology.Allowed(EntityType.Character, EntityType.Character).Count
        );
        Assert.True(RelationshipOntology.Allows(EntityType.Character, EntityType.Character));

        Assert.False(RelationshipOntology.Allows(EntityType.Character, EntityType.Location));
        Assert.False(RelationshipOntology.Allows(EntityType.Faction, EntityType.Character));
        Assert.False(RelationshipOntology.Allows(EntityType.Scene, EntityType.Plotline));
    }

    [Fact]
    public void CloudSetupTokens_KeepLegacyNormalisation()
    {
        // 设定检查通道：主仆/同门有专用 token（云端关系复合表口径），其余与客户端 kebab 同形
        Assert.Equal(
            "master",
            RelationshipOntology.CloudSetupToken(RelationshipType.MasterServant)
        );
        Assert.Equal(
            "sect-mate",
            RelationshipOntology.CloudSetupToken(RelationshipType.FellowDisciple)
        );
        Assert.Equal("parent-of", RelationshipOntology.CloudSetupToken(RelationshipType.ParentOf));
        Assert.Equal("lover", RelationshipOntology.CloudSetupToken(RelationshipType.Love));
        Assert.Equal(
            RelationshipOntology.Relations.Count(spec => spec.CloudSetupToken == spec.Token),
            RelationshipOntology.Relations.Count - 2
        );
    }

    [Fact]
    public void Directed_MatchesLegacyHierarchySet()
    {
        RelationshipType[] directed =
        [
            RelationshipType.Mentor,
            RelationshipType.MasterServant,
            RelationshipType.ParentOf,
            RelationshipType.ChildOf,
            RelationshipType.Disciple,
        ];

        Assert.All(
            RelationshipOntology.Relations,
            spec => Assert.Equal(directed.Contains(spec.Type), spec.Directed)
        );
    }

    [Fact]
    public void StructuralRelations_CoverCloudEdgeVocabulary()
    {
        string[] expected =
        [
            "relationship",
            "faction-relationship",
            "membership",
            "leadership",
            "headquarters",
            "contains",
            "adjacent-to",
            "ownership",
            "appears-in",
            "located-at",
            "belongs-to",
            "in-chapter",
            "in-volume",
            "advances",
            "features",
            "reveals-at",
            "resolves-at",
            "occurs-in",
            "evolves",
            "constrains",
        ];

        var tokens = RelationshipOntology.StructuralRelations.Select(spec => spec.Token).ToList();
        Assert.Equal(expected.Length, tokens.Count);
        Assert.All(expected, token => Assert.Contains(token, tokens));
        Assert.All(
            RelationshipOntology.StructuralRelations,
            spec =>
            {
                Assert.NotEmpty(spec.SourceTypes);
                Assert.NotEmpty(spec.TargetTypes);
                Assert.False(string.IsNullOrWhiteSpace(spec.Description));
            }
        );

        var relationship = Assert.Single(
            RelationshipOntology.StructuralRelations,
            spec => spec.Token == "relationship"
        );
        Assert.Equal([EntityType.Character], relationship.SourceTypes);
        Assert.Equal([EntityType.Character], relationship.TargetTypes);
    }

    [Fact]
    public void ByTokenOrNull_ResolvesKnownAndRejectsUnknown()
    {
        Assert.Equal(RelationshipType.Sworn, RelationshipOntology.ByTokenOrNull("sworn")?.Type);
        Assert.Null(RelationshipOntology.ByTokenOrNull("knight"));
        Assert.Null(RelationshipOntology.ByTokenOrNull(null));
        Assert.Equal("结拜", RelationshipOntology.Find(RelationshipType.Sworn).Label);
    }
}
