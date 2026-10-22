using Core.Stability;

namespace StabilityHarness;

public sealed class TelemetrySnapshot
{
    public int StepCount { get; init; }
    public long CumulativeTokens { get; init; }
    public long CumulativeErrors { get; init; }
    public double[] EnergyTrajectory { get; init; } = [];
    public StabilityStatus FinalStatus { get; init; }
    public int ConsecutiveNonNegative { get; init; }

    public override string ToString() =>
        $"TelemetrySnapshot(steps={StepCount}, tokens={CumulativeTokens}, errors={CumulativeErrors}, status={FinalStatus})";
}
