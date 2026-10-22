namespace StabilityHarness.Diagnostics;

public static class PatternClassifier
{
    public static (FailurePattern Pattern, double Confidence) Classify(
        ReadOnlySpan<double> energy,
        ReadOnlySpan<double> drift,
        bool isTripped,
        bool isFrozen
    )
    {
        if (!isTripped && !isFrozen)
            return (FailurePattern.HealthyCompletion, 0.95);

        if (isFrozen && !isTripped)
            return (FailurePattern.BudgetExhaustion, 0.90);

        if (energy.Length >= 3 && energy[..3].ToArray().Max() > 3.0)
            return (FailurePattern.EarlyExplosion, 0.85);

        if (energy.Length >= 5)
        {
            var tail = energy[^5..].ToArray();
            if (tail.All(v => v > 1.5))
            {
                var increasing = 0;
                for (var i = 1; i < tail.Length; i++)
                    if (tail[i] > tail[i - 1])
                        increasing++;

                if (increasing >= 3)
                    return (
                        FailurePattern.ContextAccumulationSpiral,
                        Math.Min(0.6 + increasing * 0.08, 0.95)
                    );
            }
        }

        if (energy.Length >= 5)
        {
            var cv = CoefficientOfVariation(energy);
            if (cv < 0.3)
                return (FailurePattern.RetryStorm, 0.70);
        }

        if (drift.Length >= 3)
        {
            var recent = drift[^3..].ToArray().Average();
            var early = drift.Length >= 6 ? drift[..3].ToArray().Average() : recent;

            if (recent > 0.7 && recent - early > 0.15)
                return (FailurePattern.PolicyDrift, Math.Min(0.5 + (recent - early), 0.9));
        }

        return (FailurePattern.Unknown, 0.3);
    }

    private static double CoefficientOfVariation(ReadOnlySpan<double> values)
    {
        if (values.Length < 2)
            return 0;
        var arr = values.ToArray();
        var mean = arr.Average();
        if (mean == 0)
            return 0;
        var variance = arr.Average(v => (v - mean) * (v - mean));
        return Math.Sqrt(variance) / mean;
    }
}
