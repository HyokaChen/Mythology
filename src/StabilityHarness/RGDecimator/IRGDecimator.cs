namespace StabilityHarness.RGDecimator;

public interface IRGDecimator
{
    ScoredMessage[] Decimate(ReadOnlySpan<string> messages);
    string[] Compress(ReadOnlySpan<string> messages);
    float ScoreMessage(ReadOnlySpan<char> message, ReadOnlySpan<char> context);
}
