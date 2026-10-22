namespace StabilityHarness.VSA;

/// <summary>
/// VSA 全息引擎实现。实现 StabilityHarness.VSA.IHolographicEngine（继承自 Core.Stability.IHolographicEngine）。
/// </summary>
public sealed class HolographicEngine : IHolographicEngine
{
    private readonly VSAConfig _config;
    private readonly Dictionary<string, sbyte[]> _invariants = [];
    private readonly Dictionary<string, sbyte[]> _keys = [];

    public int Dimensionality => _config.Dimensionality;
    public int InvariantCount => _invariants.Count;

    // ── IHolographicEngine 漂移配置 ──
    public double EffectiveDriftThreshold => _config.EffectiveDriftThreshold;
    public int EffectiveDriftCheckInterval => _config.EffectiveDriftCheckInterval;
    public int EffectiveDriftContextWindow => _config.EffectiveDriftContextWindow;

    public HolographicEngine(VSAConfig? config = null)
    {
        _config = config ?? new VSAConfig();
        if (_config.Dimensionality <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "Dimensionality must be > 0");
    }

    public sbyte[] Bind(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        ValidateBipolar(a, "a");
        ValidateBipolar(b, "b");
        return VsaCore.Bind(a, b);
    }

    public sbyte[] Bundle(IReadOnlyList<sbyte[]> vectors)
    {
        foreach (var v in vectors)
            ValidateBipolar(v, nameof(vectors));
        return VsaCore.Bundle(vectors, _config.Dimensionality);
    }

    public sbyte[] GenerateRandomVector() => VsaCore.GenerateRandomVector(_config.Dimensionality);

    public sbyte[] EncodeText(string text) => VsaCore.EncodeText(text, _config.Dimensionality);

    public sbyte[][] BatchEncodeTexts(string[] texts) =>
        texts.Select(t => VsaCore.EncodeText(t, _config.Dimensionality)).ToArray();

    public double CosineSimilarity(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        if (a.Length != _config.Dimensionality || b.Length != _config.Dimensionality)
            throw new ArgumentException($"Vectors must have dimension {_config.Dimensionality}");
        ValidateBipolar(a, "a");
        ValidateBipolar(b, "b");
        return VsaCore.CosineSimilarity(a, b);
    }

    public double[] BatchCosineSimilarity(
        ReadOnlySpan<sbyte> target,
        IReadOnlyList<sbyte[]> candidates
    )
    {
        var results = new double[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            results[i] = VsaCore.CosineSimilarity(target, candidates[i]);
        return results;
    }

    public void RegisterInvariant(string name, ReadOnlySpan<sbyte> key, ReadOnlySpan<sbyte> value)
    {
        if (!_invariants.ContainsKey(name) && _invariants.Count >= _config.MaxInvariants)
            throw new InvalidOperationException(
                $"Cannot register more than {_config.MaxInvariants} invariants. Algebraic recovery degrades as ≈1/(K+1) beyond this limit."
            );

        var bound = VsaCore.Bind(key, value);
        _invariants[name] = bound;
        _keys[name] = key.ToArray();
    }

    public bool RemoveInvariant(string name) => _invariants.Remove(name) && _keys.Remove(name);

    public string[] ListInvariants() => _invariants.Keys.OrderBy(k => k).ToArray();

    public bool TryRecover(string name, Span<sbyte> output)
    {
        if (!_invariants.TryGetValue(name, out var h) || !_keys.TryGetValue(name, out var k))
            return false;
        VsaCore.Bind(k, h, output);
        return true;
    }

    public sbyte[] Recover(string name)
    {
        if (!_invariants.TryGetValue(name, out var h) || !_keys.TryGetValue(name, out var k))
            throw new KeyNotFoundException($"No invariant registered as '{name}'");
        return VsaCore.Bind(k, h);
    }

    public double CheckDrift(string name, ReadOnlySpan<sbyte> context)
    {
        if (!_invariants.TryGetValue(name, out var h))
            return -1.0;
        return VsaCore.CosineSimilarity(h, context);
    }

    public IReadOnlyDictionary<string, double> CheckAllDrift(ReadOnlySpan<sbyte> context)
    {
        var results = new Dictionary<string, double>();
        foreach (var (name, h) in _invariants)
            results[name] = VsaCore.CosineSimilarity(h, context);
        return results;
    }

    public void ApplyProfile(Core.Stability.ModelContextProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // 同步 VsaDimensionality，确保漂移阈值计算基于实际维度
        profile = profile with
        {
            VsaDimensionality = _config.Dimensionality,
        };
        _config.ApplyProfile(profile);
    }

    public void Dispose()
    {
        _invariants.Clear();
        _keys.Clear();
    }

    private static void ValidateBipolar(ReadOnlySpan<sbyte> v, string name)
    {
        for (var i = 0; i < v.Length; i++)
        {
            if (v[i] != -1 && v[i] != 1)
                throw new ArgumentException(
                    $"Vector '{name}' element [{i}] = {v[i]} is not bipolar (must be -1 or 1)"
                );
        }
    }
}
