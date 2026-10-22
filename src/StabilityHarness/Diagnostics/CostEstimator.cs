namespace StabilityHarness.Diagnostics;

public static class CostEstimator
{
    public static double? Estimate(long totalTokens, string model)
    {
        var costPerToken = ModelCosts.CostPerToken.GetValueOrDefault(model, 0);
        if (costPerToken == 0)
        {
            foreach (var (key, val) in ModelCosts.CostPerToken)
            {
                if (model.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    costPerToken = val;
                    break;
                }
            }
        }
        if (costPerToken == 0)
            costPerToken = 0.5e-6;
        return totalTokens * costPerToken;
    }

    public static double? Project(
        double[] energy,
        long totalTokens,
        int totalSteps,
        string model,
        int maxProjectionSteps = 20
    )
    {
        if (energy.Length < 3 || totalSteps == 0)
            return null;

        var avgTokensPerStep = (double)totalTokens / totalSteps;
        var tail = energy[^3..];
        if (tail[0] <= 0)
            return null;

        var growthRate = Math.Pow(tail[^1] / tail[0], 1.0 / (tail.Length - 1));
        if (growthRate <= 1.0)
            return Estimate(totalTokens, model);

        var projectedTokens = (double)totalTokens;
        var currentRate = avgTokensPerStep * energy[^1];
        for (var i = 0; i < maxProjectionSteps; i++)
        {
            currentRate *= growthRate;
            projectedTokens += currentRate;
            if (projectedTokens > totalTokens * 10)
                break;
        }

        return Estimate((long)projectedTokens, model);
    }
}
