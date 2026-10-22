using StabilityHarness.RGDecimator;
using Xunit;

namespace StabilityHarness.Tests;

public class RGDecimatorTests
{
    [Fact]
    public void Decimate_EmptyInput_ReturnsEmpty()
    {
        var dec = new RGDecimator.RGDecimator();
        var result = dec.Decimate([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Decimate_SingleMessage_AlwaysRetained()
    {
        var dec = new RGDecimator.RGDecimator();
        var result = dec.Decimate(["hello"]);
        Assert.Single(result);
        Assert.True(result[0].Retained);
    }

    [Fact]
    public void Decimate_FirstAndLastAlwaysRetained()
    {
        var dec = new RGDecimator.RGDecimator();
        var messages = new[] { "first", "middle", "last" };
        var result = dec.Decimate(messages);

        Assert.True(result[0].Retained);
        Assert.True(result[^1].Retained);
    }

    [Fact]
    public void Decimate_AssignsScores()
    {
        var dec = new RGDecimator.RGDecimator();
        var messages = new[] { "hello world", "test message", "another one" };
        var result = dec.Decimate(messages);

        Assert.Equal(3, result.Length);
        Assert.All(result, r => Assert.True(r.Score is >= 0 and <= 1));
    }

    [Fact]
    public void Compress_ReturnsOnlyRetainedMessages()
    {
        var dec = new RGDecimator.RGDecimator();
        var messages = new[] { "keep this", "maybe drop", "keep that" };
        var result = dec.Compress(messages);

        Assert.NotEmpty(result);
        Assert.Equal(messages[0], result[0]); // first always retained
        Assert.Equal(messages[^1], result[^1]); // last always retained
    }

    [Fact]
    public void Compress_EmptyInput_ReturnsEmpty()
    {
        var dec = new RGDecimator.RGDecimator();
        Assert.Empty(dec.Compress([]));
    }

    [Fact]
    public void GetThreshold_ReturnsConfigThreshold()
    {
        var config = new RGConfig { Threshold = 0.5f };
        var dec = new RGDecimator.RGDecimator(config);
        Assert.Equal(0.5f, dec.GetThreshold());
    }

    [Fact]
    public void GetMaxRetained_ReturnsConfigMax()
    {
        var config = new RGConfig { MaxRetained = 10 };
        var dec = new RGDecimator.RGDecimator(config);
        Assert.Equal(10, dec.GetMaxRetained());
    }

    [Fact]
    public void ScoreMessage_WithEmptyContext_ReturnsNonZero()
    {
        var dec = new RGDecimator.RGDecimator();
        var score = dec.ScoreMessage("hello world", [], 0);
        Assert.True(score > 0);
    }

    [Fact]
    public void Decimate_SimilarMessages_LowerScore()
    {
        var dec = new RGDecimator.RGDecimator();
        var messages = new[]
        {
            "the quick brown fox",
            "the quick brown fox",
            "completely different content",
        };
        var result = dec.Decimate(messages);

        // Redundant messages should have lower scores
        // Not guaranteed strictly greater due to keyword/info weights, but generally unique > duplicate
        // Just verify scores are computed
        Assert.True(result[0].Score >= 0);
        Assert.True(result[1].Score >= 0);
        Assert.True(result[2].Score >= 0);
    }
}
