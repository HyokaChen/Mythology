using Core.Shared;
using Core.Stability;
using LlmTornado.Chat;
using LlmTornado.Code;
using Xunit;

namespace Core.Tests;

/// <summary>溢出后必须按 API 实测上下文收紧 profile，而不是继续相信乐观的模型元数据。</summary>
public sealed class ContextWindowAdaptationTests
{
    [Fact]
    public void LastObservedContextTokens_UsesNewestAnchor()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatMessageRoles.User, "第一条") { Tokens = 50_000 },
            new(ChatMessageRoles.Assistant, "第二条") { Tokens = 126_000 },
            new(LlmTornado.Code.ChatMessageRoles.User, "还没有实测锚点"),
        };

        Assert.Equal(126_000, ContextWindowAdaptation.LastObservedContextTokens(messages));
    }

    [Fact]
    public void ReduceToObserved_ShrinksWindowAndOutputBudget()
    {
        var profile = new ModelContextProfile { ContextWindowTokens = 1_000_000 };

        var adapted = ContextWindowAdaptation.ReduceToObserved(profile, 126_000);

        Assert.NotNull(adapted);
        Assert.Equal(128_048, adapted!.ContextWindowTokens);
        Assert.Equal(16_384, adapted.MaxOutputTokens);
        Assert.Equal(16_384, adapted.OutputTokenBudget);
    }

    [Fact]
    public void ReduceToObserved_StepsDownWhenOverflowRepeatsAtTheSameCeiling()
    {
        var profile = new ModelContextProfile { ContextWindowTokens = 128_048 };

        var adapted = ContextWindowAdaptation.ReduceToObserved(profile, 126_000);

        Assert.NotNull(adapted);
        Assert.Equal(94_500, adapted!.ContextWindowTokens);
        Assert.Equal(16_384, adapted.OutputTokenBudget);
    }

    [Fact]
    public void ReduceToObserved_IgnoresTinyObservedValues()
    {
        var profile = new ModelContextProfile { ContextWindowTokens = 128_000 };

        Assert.Null(ContextWindowAdaptation.ReduceToObserved(profile, 1_000));
    }

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
            64_000
        );

        Assert.Equal(64_000, resolved);
    }
}
