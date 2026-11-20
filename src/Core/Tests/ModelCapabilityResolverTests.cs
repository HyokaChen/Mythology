using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>用户配置的上下文窗口是上限，不能被乐观的模型元数据覆盖。</summary>
public sealed class ModelCapabilityResolverTests
{
    [Fact]
    public void ResolveEffectiveContextSize_UserCapWinsOverOptimisticMetadata()
    {
        var resolved = ModelCapabilityResolver.ResolveEffectiveContextSize(
            "DeepSeek-V4-Flash",
            200_000
        );

        Assert.Equal(200_000, resolved);
    }

    [Fact]
    public void ResolveEffectiveContextSize_UnknownModelFallsBackToUserConfig()
    {
        var resolved = ModelCapabilityResolver.ResolveEffectiveContextSize(
            "custom-model-not-in-registry",
            200_000
        );

        Assert.Equal(200_000, resolved);
    }
}
