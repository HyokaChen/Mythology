using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// SystemOneConfig 配置往返契约：ToDict → FromDict 全字段保值；
/// 缺节 = 默认值（整块可选，旧 config.yaml 无 system-one 节必须零影响）。
/// </summary>
public sealed class SystemOneConfigTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var config = new SystemOneConfig
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:9999",
            Path = "/decisions",
            ApiKey = "sk-test",
            Model = "d1-omni-test",
            TimeoutMs = 12_345,
            MaxStateChars = 2345,
            Rerank = new SystemOneRerankConfig
            {
                Enabled = false,
                Overfetch = 3,
                Weight = 0.8,
                PassageChars = 400,
                MaxCandidates = 12,
            },
            Conflicts = new SystemOneConflictConfig
            {
                Enabled = false,
                MaxPairs = 5,
                DropUnrelatedProbability = 0.55,
            },
            Relations = new SystemOneRelationConfig
            {
                Enabled = false,
                MinProbability = 0.85,
                MaxCandidates = 30,
                MaxNeighbors = 4,
            },
            Ontology = new SystemOneOntologyConfig
            {
                Enabled = false,
                MinProbability = 0.75,
                MinConfidence = 0.9,
                MaxCandidates = 10,
                MaxEvidenceChars = 200,
            },
            Loop = new SystemOneLoopConfig
            {
                Enabled = true,
                TextChars = 900,
                StopCheck = new SystemOneStopCheckConfig
                {
                    Enabled = false,
                    MinProbability = 0.9,
                    MaxPerRun = 2,
                },
                DriftCheck = new SystemOneDriftCheckConfig
                {
                    Enabled = false,
                    MinProbability = 0.55,
                },
                StabilityCheck = new SystemOneStabilityCheckConfig
                {
                    Enabled = false,
                    MinProbability = 0.65,
                    MaxCalls = 3,
                },
            },
        };

        var roundTripped = SystemOneConfig.FromDict(config.ToDict());

        Assert.Equal(config, roundTripped);
    }

    [Fact]
    public void FromDict_MissingSection_ReturnsDefaults()
    {
        var config = SystemOneConfig.FromDict([]);

        Assert.False(config.Enabled); // 可选区块：默认关闭
        Assert.Equal("http://127.0.0.1:8080", config.BaseUrl);
        Assert.Equal("d1-omni", config.Model);
        Assert.Equal(20_000, config.TimeoutMs);
        Assert.Equal(3000, config.MaxStateChars);
        Assert.True(config.Rerank.Enabled);
        Assert.Equal(2, config.Rerank.Overfetch);
        Assert.Equal(0.5, config.Rerank.Weight, 3);
        Assert.True(config.Conflicts.Enabled);
        Assert.Equal(12, config.Conflicts.MaxPairs);
        Assert.True(config.Relations.Enabled);
        Assert.Equal(0.7, config.Relations.MinProbability, 3);
        Assert.Equal(24, config.Relations.MaxCandidates);
        Assert.Equal(8, config.Relations.MaxNeighbors);
        Assert.True(config.Ontology.Enabled);
        Assert.Equal(0.7, config.Ontology.MinProbability, 3);
        Assert.Equal(0.8, config.Ontology.MinConfidence, 3);
        Assert.Equal(24, config.Ontology.MaxCandidates);
        Assert.Equal(300, config.Ontology.MaxEvidenceChars);
        Assert.False(config.Loop.Enabled); // 循环判定默认关闭：需显式开启
        Assert.Equal(1200, config.Loop.TextChars);
        Assert.True(config.Loop.StopCheck.Enabled);
        Assert.Equal(0.8, config.Loop.StopCheck.MinProbability, 3);
        Assert.Equal(1, config.Loop.StopCheck.MaxPerRun);
        Assert.True(config.Loop.DriftCheck.Enabled);
        Assert.Equal(0.6, config.Loop.DriftCheck.MinProbability, 3);
        Assert.True(config.Loop.StabilityCheck.Enabled);
        Assert.Equal(0.75, config.Loop.StabilityCheck.MinProbability, 3);
        Assert.Equal(6, config.Loop.StabilityCheck.MaxCalls);
    }

    [Fact]
    public void FromDict_PartialSection_KeepsDefaultsForRest()
    {
        var dict = new Dictionary<string, object?>
        {
            ["enabled"] = "true", // YAML 字符串形态
            ["timeout-ms"] = "8000",
            ["rerank"] = new Dictionary<string, object?> { ["overfetch"] = "4" },
            ["loop"] = new Dictionary<string, object?>
            {
                ["text-chars"] = "900",
                ["stop-check"] = new Dictionary<string, object?> { ["max-per-run"] = "2" },
            },
        };

        var config = SystemOneConfig.FromDict(dict);

        Assert.True(config.Enabled);
        Assert.Equal(8000, config.TimeoutMs);
        Assert.Equal(4, config.Rerank.Overfetch);
        Assert.Equal(0.5, config.Rerank.Weight, 3); // 未提供 → 默认
        Assert.Equal(12, config.Conflicts.MaxPairs);
        Assert.Equal(900, config.Loop.TextChars);
        Assert.False(config.Loop.Enabled); // 未提供 → 默认关闭
        Assert.Equal(2, config.Loop.StopCheck.MaxPerRun);
        Assert.Equal(0.8, config.Loop.StopCheck.MinProbability, 3); // 未提供 → 默认
        Assert.True(config.Loop.DriftCheck.Enabled);
    }

    [Fact]
    public void ConfigData_RoundTrip_KeepsSystemOneSection()
    {
        var data = new ConfigData
        {
            SystemOne = new SystemOneConfig
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:7777",
                Relations = new SystemOneRelationConfig { MinProbability = 0.8 },
                Loop = new SystemOneLoopConfig
                {
                    Enabled = true,
                    StopCheck = new SystemOneStopCheckConfig { MinProbability = 0.9 },
                },
            },
        };

        var roundTripped = ConfigData.FromDict(data.ToDict());

        Assert.True(roundTripped.SystemOne.Enabled);
        Assert.Equal("http://127.0.0.1:7777", roundTripped.SystemOne.BaseUrl);
        Assert.Equal(0.8, roundTripped.SystemOne.Relations.MinProbability, 3);
        Assert.True(roundTripped.SystemOne.Loop.Enabled);
        Assert.Equal(0.9, roundTripped.SystemOne.Loop.StopCheck.MinProbability, 3);
        Assert.Equal(1200, roundTripped.SystemOne.Loop.TextChars); // 未提供 → 默认
    }
}
