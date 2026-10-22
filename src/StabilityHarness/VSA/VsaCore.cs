using System.Runtime.CompilerServices;

namespace StabilityHarness.VSA;

public static class VsaCore
{
    private static readonly Random _rng = new();
    private const ulong FnvOffset = 0xcbf29ce484222325;
    private const ulong FnvPrime = 0x100000001b3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bind(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b, Span<sbyte> result)
    {
        for (var i = 0; i < a.Length; i++)
            result[i] = (sbyte)(a[i] * b[i]);
    }

    public static sbyte[] Bind(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        var result = new sbyte[a.Length];
        Bind(a, b, result);
        return result;
    }

    public static sbyte[] Bundle(IReadOnlyList<sbyte[]> vectors, int dimension)
    {
        var accum = new int[dimension];
        foreach (var vec in vectors)
            for (var i = 0; i < dimension; i++)
                accum[i] += vec[i];

        var result = new sbyte[dimension];
        for (var i = 0; i < dimension; i++)
            result[i] = accum[i] >= 0 ? (sbyte)1 : (sbyte)-1;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double CosineSimilarity(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        long dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += (long)a[i] * b[i];
        return (double)dot / a.Length;
    }

    public static sbyte[] GenerateRandomVector(int dimension)
    {
        var result = new sbyte[dimension];
        for (var i = 0; i < dimension; i++)
            result[i] = _rng.Next(2) == 0 ? (sbyte)1 : (sbyte)-1;
        return result;
    }

    public static sbyte[] GenerateRandomVector(int dimension, Random rng)
    {
        var result = new sbyte[dimension];
        for (var i = 0; i < dimension; i++)
            result[i] = rng.Next(2) == 0 ? (sbyte)1 : (sbyte)-1;
        return result;
    }

    public static sbyte[] EncodeText(string text, int dimension)
    {
        if (string.IsNullOrEmpty(text))
            return new sbyte[dimension];

        var lower = text.ToLowerInvariant();

        if (lower.Length < 3)
        {
            var seed = Fnv1a64(lower);
            return GenerateRandomVector(dimension, new Random((int)(seed & 0x7FFFFFFF)));
        }

        var accum = new int[dimension];
        for (var i = 0; i <= lower.Length - 3; i++)
        {
            var trigram = lower.AsSpan(i, 3);
            var seed = Fnv1a64(trigram);
            var rng = new Random((int)(seed & 0x7FFFFFFF));
            for (var j = 0; j < dimension; j++)
                accum[j] += rng.Next(2) == 0 ? 1 : -1;
        }

        var result = new sbyte[dimension];
        for (var i = 0; i < dimension; i++)
            result[i] = accum[i] >= 0 ? (sbyte)1 : (sbyte)-1;
        return result;
    }

    private static ulong Fnv1a64(ReadOnlySpan<char> s)
    {
        var hash = FnvOffset;
        foreach (var t in s)
        {
            hash ^= (byte)t;
            hash *= FnvPrime;
        }
        return hash;
    }

    private static ulong Fnv1a64(string s)
    {
        var hash = FnvOffset;
        foreach (var c in s)
        {
            hash ^= (byte)c;
            hash *= FnvPrime;
        }
        return hash;
    }
}
