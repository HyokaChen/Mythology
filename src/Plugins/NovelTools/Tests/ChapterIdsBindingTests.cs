using System.Text.Json;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 云端分析工具 chapter_ids 参数绑定测试：
/// 使用 AgentTool 实际绑定的 JsonSchemaHelper.JsonOptions 验证
/// LLM 双重 JSON 序列化的字符串化数组能被所有带 chapter_ids 的云端工具参数正确解析。
/// </summary>
public class ChapterIdsBindingTests
{
    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonSchemaHelper.JsonOptions)!;

    [Fact]
    public void DetectDescriptionQuality_StringifiedArray_Binds()
    {
        var args = Deserialize<DetectDescriptionQualityParams>(
            """{"novel-id":"n1","chapter-ids":"[\"80c45d176c1c\",\"13a959cf40f8\"]"}"""
        );
        Assert.Equal(["80c45d176c1c", "13a959cf40f8"], args.ChapterIds);
    }

    [Fact]
    public void DetectDescriptionQuality_NativeArray_Binds()
    {
        var args = Deserialize<DetectDescriptionQualityParams>(
            """{"novel-id":"n1","chapter-ids":["80c45d176c1c"]}"""
        );
        Assert.Equal(["80c45d176c1c"], args.ChapterIds);
    }

    [Fact]
    public void EvaluateImmersion_StringifiedArray_Binds()
    {
        var args = Deserialize<EvaluateImmersionParams>("""{"chapter-ids":"[\"a\"]"}""");
        Assert.Equal(["a"], args.ChapterIds);
    }

    [Fact]
    public void AnalyzePacing_StringifiedArray_Binds()
    {
        var args = Deserialize<AnalyzePacingParams>("""{"chapter-ids":"[\"a\",\"b\",\"c\"]"}""");
        Assert.Equal(["a", "b", "c"], args.ChapterIds);
    }

    [Fact]
    public void EvaluateConflict_StringifiedArray_Binds()
    {
        var args = Deserialize<EvaluateConflictParams>("""{"chapter-ids":"[\"x\"]"}""");
        Assert.Equal(["x"], args.ChapterIds);
    }
}
