using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// 能力目录快照测试（2026-10-18 分类消费）：kebab 载荷解析（含 category/workflow-phase/scope/
/// graph-backed/output-sections）、容忍约定（非 JSON / 根非对象 / capabilities 缺失 → null，
/// 畸形条目丢弃）、查询语义（未声明能力、深度与范围的 fail-open / 精确匹配）、
/// store 的 last-known-good 语义。
/// </summary>
public sealed class CapabilityCatalogSnapshotTests
{
    private const string FullJson = """
        {
          "contract-version": "1.0",
          "capabilities": [
            {
              "id": "fiction.story.check",
              "label": "故事检查",
              "execution-plane": "cloud",
              "availability": "free",
              "supported-depths": ["quick", "standard", "deep"],
              "locales": ["zh-CN"],
              "max-characters": 300000,
              "requires-api-key": true,
              "retention": "ephemeral",
              "contract-version": "1.0",
              "category": "review",
              "workflow-phase": "review",
              "scope": "chapter-window",
              "graph-backed": true,
              "output-sections": ["dimensions", "rule-findings"]
            },
            {
              "id": "fiction.scene.plan",
              "category": "planning",
              "workflow-phase": "writing",
              "scope": "target-chapter",
              "supported-depths": ["quick"]
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsTaxonomyFields_AndIgnoresUnknownKeys()
    {
        var snapshot = CapabilityCatalogSnapshot.Parse(FullJson);

        Assert.NotNull(snapshot);
        Assert.Equal("1.0", snapshot!.ContractVersion);

        var check = Assert.Single(snapshot.Capabilities, item => item.Id == "fiction.story.check");
        Assert.Equal("review", check.Category);
        Assert.Equal("review", check.WorkflowPhase);
        Assert.Equal("chapter-window", check.Scope);
        Assert.True(check.GraphBacked);
        Assert.Equal(["dimensions", "rule-findings"], check.OutputSections);
        Assert.Equal(["quick", "standard", "deep"], check.SupportedDepths);
        Assert.Equal(300_000, check.MaxCharacters);
        Assert.Equal("free", check.Availability);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"capabilities\"")]
    [InlineData("""{"contract-version":"1.0"}""")] // 无 capabilities 字段 → 无数据（与「空声明」不同）
    [InlineData("""{"capabilities":"nope"}""")] // 类型不符 → 无数据
    public void Parse_Undecidable_ReturnsNull(string body)
    {
        Assert.Null(CapabilityCatalogSnapshot.Parse(body));
    }

    [Fact]
    public void Parse_EmptyCapabilityArray_IsExplicitDeclaration()
    {
        var snapshot = CapabilityCatalogSnapshot.Parse(
            """{"contract-version":"1.0","capabilities":[]}"""
        );

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Capabilities);
        Assert.False(snapshot.IsAdvertised("fiction.story.check"));
    }

    [Fact]
    public void Parse_DropsMalformedEntries_ToleratesMissingFields()
    {
        var snapshot = CapabilityCatalogSnapshot.Parse(
            """{"capabilities":[{"label":"无 id"},null,"nope",42,{"id":"x"}],"contract-version":"1.0"}"""
        );

        Assert.NotNull(snapshot);
        var item = Assert.Single(snapshot!.Capabilities);
        Assert.Equal("x", item.Id);
        // 未声明字段 → 默认值（无 scope/supported-depths 即 fail-open 的依据）
        Assert.Null(item.Scope);
        Assert.Null(item.SupportedDepths);
        Assert.Null(item.OutputSections);
        Assert.False(item.GraphBacked);
    }

    [Fact]
    public void Queries_FailOpen_WhenCapabilityOrFieldUndeclared()
    {
        var snapshot = CapabilityCatalogSnapshot.Parse(FullJson)!;

        // 未声明能力：IsAdvertised=false（唯一非 fail-open 的判定——调用方据此跳过）
        Assert.False(snapshot.IsAdvertised("fiction.setup.check"));
        // 但深度/范围查询对未声明能力 fail-open（跳过由 IsAdvertised 统一负责）
        Assert.True(snapshot.SupportsDepth("fiction.setup.check", "deep"));
        Assert.True(snapshot.AllowsScope("fiction.setup.check", "chapter-window"));
        // 已声明的能力未声明 scope（scene.plan 有 scope；构造一个无 scope 的验证缺失语义）
        var noScope = new CapabilityCatalogSnapshot("1.0", [new CapabilityDescriptorDto("c")]);
        Assert.True(noScope.AllowsScope("c", "chapter-window"));
        Assert.True(noScope.SupportsDepth("c", "deep"));
    }

    [Fact]
    public void Queries_AreStrict_WhenDeclared()
    {
        var snapshot = CapabilityCatalogSnapshot.Parse(FullJson)!;

        Assert.True(snapshot.IsAdvertised("fiction.story.check"));
        // supported-depths 精确判定：scene.plan 只声明 quick
        Assert.False(snapshot.SupportsDepth("fiction.scene.plan", "standard"));
        Assert.True(snapshot.SupportsDepth("fiction.scene.plan", "quick"));
        // scope 精确判定：scene.plan 是 target-chapter，不是 chapter-window
        Assert.False(snapshot.AllowsScope("fiction.scene.plan", "chapter-window"));
        Assert.True(snapshot.AllowsScope("fiction.scene.plan", "target-chapter"));
        // 大小写敏感（契约枚举值）
        Assert.False(snapshot.SupportsDepth("fiction.story.check", "Quick"));
    }

    [Fact]
    public void Store_KeepsLastKnownGood_AndIgnoresNull()
    {
        var store = new CloudCapabilityCatalogStore();
        Assert.Null(store.Current);

        store.Update(null); // 无数据不覆盖
        Assert.Null(store.Current);

        var first = CapabilityCatalogSnapshot.Parse(FullJson)!;
        store.Update(first);
        Assert.Same(first, store.Current);

        store.Update(null); // last-known-good：离线/失败不清空
        Assert.Same(first, store.Current);

        var second = CapabilityCatalogSnapshot.Parse("""{"capabilities":[]}""")!;
        store.Update(second);
        Assert.Same(second, store.Current);
    }
}
