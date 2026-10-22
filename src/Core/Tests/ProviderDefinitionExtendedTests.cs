using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ProviderDefinition 扩展测试：FindByDisplayName、IsCustom、GetCustomName、All 完整性。
/// </summary>
public sealed class ProviderDefinitionExtendedTests
{
    [Fact]
    public void All_ContainsAllKnownProviders()
    {
        Assert.Equal(10, ProviderDefinition.All.Length);
        var keys = ProviderDefinition.All.Select(d => d.ConfigKey).ToHashSet();
        Assert.Contains("openai", keys);
        Assert.Contains("anthropic", keys);
        Assert.Contains("deepseek", keys);
        Assert.Contains("xai", keys);
        Assert.Contains("google", keys);
    }

    [Fact]
    public void All_EachHasLlmProvider()
    {
        Assert.All(ProviderDefinition.All, d => Assert.NotNull(d.LlmProvider));
    }

    [Fact]
    public void All_EachHasBuiltInUrl()
    {
        Assert.All(ProviderDefinition.All, d => Assert.True(d.HasBuiltInUrl));
    }

    [Fact]
    public void All_EachHasDefaultModelHint()
    {
        Assert.All(ProviderDefinition.All, d => Assert.NotNull(d.DefaultModelHint));
    }

    [Fact]
    public void FindByDisplayName_Known_ReturnsCorrect()
    {
        var def = ProviderDefinition.FindByDisplayName("OpenAI");
        Assert.Equal("openai", def.ConfigKey);
    }

    [Fact]
    public void FindByDisplayName_Unknown_ReturnsCustom()
    {
        var def = ProviderDefinition.FindByDisplayName("NonExistent");
        Assert.Equal("custom", def.ConfigKey);
    }

    [Fact]
    public void FindByDisplayName_Custom_ReturnsCustom()
    {
        var def = ProviderDefinition.FindByDisplayName("自定义");
        Assert.Equal("custom", def.ConfigKey);
    }

    [Fact]
    public void FindByDisplayName_Null_ReturnsCustom()
    {
        var def = ProviderDefinition.FindByDisplayName(null!);
        Assert.Equal("custom", def.ConfigKey);
    }

    [Fact]
    public void IsCustom_PlainCustom_ReturnsTrue()
    {
        Assert.True(ProviderDefinition.IsCustom("custom"));
    }

    [Fact]
    public void IsCustom_WithSuffix_ReturnsTrue()
    {
        Assert.True(ProviderDefinition.IsCustom("custom:商汤"));
        Assert.True(ProviderDefinition.IsCustom("custom:my-llm"));
    }

    [Fact]
    public void IsCustom_KnownProvider_ReturnsFalse()
    {
        Assert.False(ProviderDefinition.IsCustom("openai"));
        Assert.False(ProviderDefinition.IsCustom("anthropic"));
    }

    [Fact]
    public void GetCustomName_WithSuffix_ReturnsName()
    {
        Assert.Equal("商汤", ProviderDefinition.GetCustomName("custom:商汤"));
        Assert.Equal("my-llm", ProviderDefinition.GetCustomName("custom:my-llm"));
    }

    [Fact]
    public void GetCustomName_PlainCustom_ReturnsNull()
    {
        Assert.Null(ProviderDefinition.GetCustomName("custom"));
    }

    [Fact]
    public void GetCustomName_Null_ReturnsNull()
    {
        Assert.Null(ProviderDefinition.GetCustomName(null!));
    }

    [Fact]
    public void GetCustomName_Empty_ReturnsNull()
    {
        Assert.Null(ProviderDefinition.GetCustomName(""));
    }

    [Fact]
    public void GetCustomName_NonCustom_ReturnsNull()
    {
        Assert.Null(ProviderDefinition.GetCustomName("openai"));
    }

    [Fact]
    public void FindByConfigKey_Empty_ReturnsCustom()
    {
        var def = ProviderDefinition.FindByConfigKey("");
        Assert.Equal("custom", def.ConfigKey);
    }

    [Fact]
    public void FindByConfigKey_Null_ReturnsCustom()
    {
        var def = ProviderDefinition.FindByConfigKey(null!);
        Assert.Equal("custom", def.ConfigKey);
    }

    [Fact]
    public void FindByConfigKey_CustomPrefix_ReturnsCustom()
    {
        var def = ProviderDefinition.FindByConfigKey("custom:商汤");
        Assert.Equal("custom", def.ConfigKey);
    }

    [Fact]
    public void Custom_HasNoLlmProvider()
    {
        Assert.Null(ProviderDefinition.Custom.LlmProvider);
    }

    [Fact]
    public void Custom_HasNoBuiltInUrl()
    {
        Assert.False(ProviderDefinition.Custom.HasBuiltInUrl);
    }
}
