using Core.Stability;

namespace StabilityHarness.Diagnostics;

public sealed record Suggestion
{
    public int Priority { get; init; }
    public string Action { get; init; } = "";
    public string Rationale { get; init; } = "";
}

public static class ModelCosts
{
    public static readonly Dictionary<string, double> CostPerToken = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // Gemini
        ["gemini-2.5-flash"] = 0.15e-6,
        ["gemini-2.5-pro"] = 1.25e-6,
        ["gemini-2.0-flash"] = 0.10e-6,
        // GPT
        ["gpt-4o"] = 2.5e-6,
        ["gpt-4o-mini"] = 0.15e-6,
        ["gpt-4.1"] = 2.0e-6,
        ["gpt-4.1-mini"] = 0.40e-6,
        ["gpt-4.1-nano"] = 0.10e-6,
        // Claude
        ["claude-sonnet-4"] = 3.0e-6,
        ["claude-3.5-sonnet"] = 3.0e-6,
        ["claude-opus-4"] = 15.0e-6,
        ["claude-haiku-3.5"] = 0.80e-6,
    };
}

public sealed class FailureReport
{
    public FailurePattern Pattern { get; set; }
    public double Confidence { get; set; }
    public long TotalTokens { get; init; }
    public int TotalSteps { get; init; }
    public bool IsTripped { get; init; }
    public bool IsFrozen { get; init; }
    public double? BaselineTokens { get; init; }
    public double? PeakRatio { get; init; }
    public double[] EnergyTrajectory { get; init; } = [];
    public double[] DriftTrajectory { get; init; } = [];
    public double? CostEstimateUsd { get; init; }
    public double? ProjectedCostUsd { get; init; }
    public Suggestion[] Suggestions { get; set; } = [];
    public string[] Evidence { get; set; } = [];

    public static FailureReport FromGuard(
        IGrowthRatioGuard guard,
        double[]? driftHistory = null,
        string? model = null
    ) => FromGrowthRatioGuard(guard, driftHistory, model);

    public static FailureReport FromLyapunovMonitor(IStabilityMonitor monitor, string? model = null)
    {
        var totalTokens = monitor.TotalTokens;
        var totalSteps = monitor.TotalSteps;
        var isTripped = monitor.IsTripped;
        var isFrozen = monitor.IsFrozen;
        var energy = monitor.EnergyHistory.ToArray();
        var costUsd = model != null ? CostEstimator.Estimate(totalTokens, model) : null;
        var projectedUsd =
            model != null ? CostEstimator.Project(energy, totalTokens, totalSteps, model) : null;

        var report = new FailureReport
        {
            Pattern = FailurePattern.Unknown,
            Confidence = 0.0,
            TotalTokens = totalTokens,
            TotalSteps = totalSteps,
            IsTripped = isTripped,
            IsFrozen = isFrozen,
            BaselineTokens = null,
            PeakRatio = null,
            EnergyTrajectory = energy,
            DriftTrajectory = [],
            CostEstimateUsd = costUsd,
            ProjectedCostUsd = projectedUsd,
        };

        Classify(report);
        Suggest(report);
        return report;
    }

    public static FailureReport FromGrowthRatioGuard(
        IGrowthRatioGuard guard,
        double[]? driftHistory = null,
        string? modelName = null
    )
    {
        var totalTokens = guard.TotalTokens;
        var totalSteps = guard.TotalSteps;
        var isTripped = guard.IsTripped;
        var isFrozen = guard.IsFrozen;
        var energy = guard.EnergyHistory.ToArray();
        var baseline = guard.Baseline;
        var peakRatio = baseline is > 0 && energy.Length > 0 ? (double?)energy.Max() : null;
        var drift = driftHistory ?? [];
        var costUsd = modelName != null ? CostEstimator.Estimate(totalTokens, modelName) : null;
        var projectedUsd =
            modelName != null
                ? CostEstimator.Project(energy, totalTokens, totalSteps, modelName)
                : null;

        var report = new FailureReport
        {
            Pattern = FailurePattern.Unknown,
            Confidence = 0.0,
            TotalTokens = totalTokens,
            TotalSteps = totalSteps,
            IsTripped = isTripped,
            IsFrozen = isFrozen,
            BaselineTokens = baseline,
            PeakRatio = peakRatio,
            EnergyTrajectory = energy,
            DriftTrajectory = drift,
            CostEstimateUsd = costUsd,
            ProjectedCostUsd = projectedUsd,
        };

        Classify(report);
        Suggest(report);
        return report;
    }

    private static string PatternToSnake(FailurePattern p)
    {
        var name = p.ToString();
        return string.Concat(
            name.Select(
                (c, i) =>
                    i > 0 && char.IsUpper(c)
                        ? "_" + char.ToLowerInvariant(c)
                        : char.ToLowerInvariant(c).ToString()
            )
        );
    }

    private static void Classify(FailureReport report)
    {
        var (pattern, confidence) = PatternClassifier.Classify(
            report.EnergyTrajectory,
            report.DriftTrajectory,
            report.IsTripped,
            report.IsFrozen
        );
        report.Pattern = pattern;
        report.Confidence = confidence;
        report.Evidence = pattern switch
        {
            FailurePattern.HealthyCompletion => ["Task completed within budget without tripping."],
            FailurePattern.BudgetExhaustion =>
            [
                $"Budget ceiling reached after {report.TotalTokens:N0} tokens.",
            ],
            FailurePattern.EarlyExplosion => ["Token usage spiked early in the conversation."],
            FailurePattern.ContextAccumulationSpiral =>
            [
                "Context growing with accelerating energy.",
            ],
            FailurePattern.RetryStorm => ["Low token variance suggests repeated identical calls."],
            FailurePattern.PolicyDrift => ["Agent responses diverging from domain policy."],
            FailurePattern.GradualDegradation =>
            [
                "Moderate token growth without clear exponential pattern.",
            ],
            _ => ["No clear failure pattern detected from available signals."],
        };
    }

    private static void Suggest(FailureReport report)
    {
        report.Suggestions = report.Pattern switch
        {
            FailurePattern.ContextAccumulationSpiral =>
            [
                new Suggestion
                {
                    Priority = 1,
                    Action = "Enable RG history compression in your agent loop.",
                    Rationale =
                        "The context window is growing unchecked. Compressing older messages reduces prompt tokens by 40-60%.",
                },
                new Suggestion
                {
                    Priority = 2,
                    Action = report.PeakRatio.HasValue
                        ? $"Lower the growth ratio threshold to {Math.Max(1.8, report.PeakRatio.Value - 0.2):F1}x."
                        : "Lower the growth ratio threshold.",
                    Rationale = "Catching spirals earlier reduces wasted tokens.",
                },
                new Suggestion
                {
                    Priority = 3,
                    Action = "Add a sliding-window context strategy.",
                    Rationale =
                        "Instead of sending the full conversation each turn, send only the last N messages plus a summary of earlier ones.",
                },
            ],
            FailurePattern.RetryStorm =>
            [
                new Suggestion
                {
                    Priority = 1,
                    Action = "Add exponential backoff with jitter to tool calls.",
                    Rationale =
                        "The agent is burning tokens on identical retries. Backoff prevents tight retry loops.",
                },
                new Suggestion
                {
                    Priority = 2,
                    Action = "Set a max-retries limit per tool call.",
                    Rationale =
                        $"The agent made {report.TotalSteps} attempts. Cap retries at 3 to fail fast.",
                },
                new Suggestion
                {
                    Priority = 3,
                    Action = "Classify errors as transient vs. permanent.",
                    Rationale =
                        "Permanent errors (auth failures, invalid schemas) should not be retried at all.",
                },
            ],
            FailurePattern.PolicyDrift =>
            [
                new Suggestion
                {
                    Priority = 1,
                    Action = "Re-inject the domain policy in the system prompt every N turns.",
                    Rationale =
                        "Long conversations cause the model to 'forget' the original policy. Periodic re-injection keeps it anchored.",
                },
                new Suggestion
                {
                    Priority = 2,
                    Action = "Enable dual-confirmation gating in the harness.",
                    Rationale =
                        "Trip the circuit breaker only when BOTH growth ratio AND drift score confirm failure, reducing false positives.",
                },
            ],
            FailurePattern.EarlyExplosion =>
            [
                new Suggestion
                {
                    Priority = 1,
                    Action = "Audit your system prompt and first tool response sizes.",
                    Rationale =
                        "A token spike in the first 3 turns usually means the system prompt or an initial tool response is too large.",
                },
                new Suggestion
                {
                    Priority = 2,
                    Action =
                        "Truncate or summarize large tool responses before injecting them into context.",
                    Rationale =
                        "Raw database dumps or API responses can be thousands of tokens. Summarize before adding to context.",
                },
            ],
            FailurePattern.BudgetExhaustion =>
            [
                new Suggestion
                {
                    Priority = 1,
                    Action = "Increase the token budget for this task type.",
                    Rationale =
                        $"The task consumed {report.TotalTokens:N0} tokens without spiraling. It may be genuinely complex.",
                },
                new Suggestion
                {
                    Priority = 2,
                    Action = "Enable history compression to reduce per-turn cost.",
                    Rationale =
                        "Even non-spiraling tasks benefit from pruning low-relevance messages in long conversations.",
                },
            ],
            FailurePattern.GradualDegradation =>
            [
                new Suggestion
                {
                    Priority = 2,
                    Action = "Consider increasing the ratio threshold to 2.5x.",
                    Rationale =
                        "The growth was moderate, not exponential. A higher threshold would let this task complete.",
                },
                new Suggestion
                {
                    Priority = 3,
                    Action = "Review whether this task type typically needs more turns.",
                    Rationale =
                        "Some tasks are genuinely multi-step. Profile typical turn counts and set budget accordingly.",
                },
            ],
            FailurePattern.HealthyCompletion => [],
            _ =>
            [
                new Suggestion
                {
                    Priority = 3,
                    Action = "Enable DEBUG logging to capture full execution trace.",
                    Rationale =
                        "The failure pattern is unclear from energy/drift signals alone. Full traces will reveal the root cause.",
                },
            ],
        };
    }
}
