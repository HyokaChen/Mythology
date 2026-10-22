using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// CHANGE_07: 云端分析工具基类 — 22 个 NovelTools 分析工具的统一云端适配。
/// 本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// 云端不可用时返回可读错误，不伪造分数，不阻塞本地创作。
/// </summary>
/// <typeparam name="TParams">工具参数类型</typeparam>
/// <typeparam name="TDetails">工具详情类型</typeparam>
public abstract class CloudAnalysisTool<TParams, TDetails>(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : AgentTool<TParams, TDetails>
    where TParams : class, new()
    where TDetails : class, new()
{
    /// <summary>云端 capability 标识（子类设置）</summary>
    protected abstract string Capability { get; }

    /// <summary>维度键（子类设置，用于单维度工具）</summary>
    protected abstract string Dimension { get; }

    /// <summary>CHANGE_09 后云端收敛为单一 opaque 能力；NovelTools 全部走它。</summary>
    protected const string StoryCheckCapability = "fiction.story.check";

    /// <summary>云端 Harness 客户端</summary>
    protected IFictionHarnessClient? CloudHarness { get; } = cloudHarness;

    private string? _cloudErrorCode;
    private string? _cloudErrorMessage;

    /// <summary>
    /// 加载目标章节文本。
    /// </summary>
    protected async Task<(List<Chapter> chapters, Novel? novel)> LoadChaptersAsync(
        string novelId,
        List<string>? chapterIds,
        CancellationToken ct
    )
    {
        var novel = await store.GetNovel(novelId, ct);
        var allChapters = await store.GetChapters(novelId, ct);
        ThrowIfCancelled(ct);

        var chapters = chapterIds is { Count: > 0 }
            ? allChapters
                .Where(c => chapterIds.Contains(c.Id))
                .OrderBy(c => c.Order)
                .Where(c => !string.IsNullOrWhiteSpace(c.Content))
                .ToList()
            : allChapters
                .OrderBy(c => c.Order)
                .Where(c => !string.IsNullOrWhiteSpace(c.Content))
                .ToList();

        return (chapters, novel);
    }

    /// <summary>
    /// 执行云端分析 — 通过 IFictionHarnessClient 提交章节，返回原始 JsonElement 结果。
    /// 云端不可用时返回 null。
    /// </summary>
    protected async Task<JsonElement?> ExecuteCloudAnalysisAsync(
        IReadOnlyList<Chapter> chapters,
        Novel novel,
        CancellationToken ct
    )
    {
        _cloudErrorCode = null;
        _cloudErrorMessage = null;
        if (CloudHarness is null || chapters.Count == 0)
            return null;

        try
        {
            var profile = WritingProfile.FromNovel(novel);
            var request = new HarnessRequest
            {
                Capability = Capability,
                Depth = "standard",
                Locale = "zh-CN",
                Chapters = chapters
                    .Select(
                        (c, index) =>
                            new ChapterInput(
                                c.Id,
                                c.Order,
                                c.Title,
                                index == 0 ? "target" : "context",
                                c.Content
                            )
                    )
                    .ToList(),
                Context = HarnessContextBuilder.Build(novel, profile.Genre),
                Profile = WritingProfileInput.FromProfile(profile),
            };

            var result = await CloudHarness.ExecuteAsync(
                request,
                $"{Capability}-{chapters[0].Id}",
                ct
            );

            return result.Result as JsonElement?;
        }
        catch (CloudApiException ex)
        {
            _cloudErrorCode = ex.Code;
            _cloudErrorMessage = ex.Message;
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 构建云端不可用时的错误结果。
    /// 凭据类错误提示重新登录；连接类错误只给简洁文案（原始状态/详情已由云端客户端写入日志），
    /// 不再把 401 等原始技术细节画进对话流；业务拒绝（套餐/契约/参数）保留服务端可执行 detail。
    /// </summary>
    protected AgentToolResult<TDetails> CloudUnavailableResult()
    {
        var isAuthError = _cloudErrorCode is "invalid_api_key" or "unauthorized" or "forbidden";
        var isBusiness = CloudErrorMapper.IsBusinessRejection(_cloudErrorCode ?? "");
        var detail = !string.IsNullOrWhiteSpace(_cloudErrorMessage)
            ? $"：{_cloudErrorMessage}"
            : "";
        var message =
            isAuthError
                ? $"⚠️ 云端分析不可用（{Dimension}），未执行分析。请使用 :cloud login 登录后重试。本地创作功能不受影响。"
            : isBusiness
                ? $"⚠️ 云端分析不可用（{Dimension}），未执行分析（{_cloudErrorCode}{detail}）。本地创作功能不受影响。"
            : $"⚠️ 云端分析不可用（{Dimension}），未执行分析。详情已写入日志。本地创作功能不受影响。";
        return new AgentToolResult<TDetails>
        {
            Content = [new TextContent(message)],
            IsError = true,
        };
    }

    /// <summary>
    /// 从 fiction.story.check 结果中提取指定业务维度分组。
    /// </summary>
    protected static JsonElement GetDimension(JsonElement element, string key)
    {
        if (
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("dimensions", out var dimensions)
            && dimensions.ValueKind == JsonValueKind.Object
            && dimensions.TryGetProperty(key, out var dimension)
        )
            return dimension;
        return default;
    }

    /// <summary>
    /// 从维度结果对象中读取数值分数（默认 overallScore）。
    /// </summary>
    protected static double GetDimensionScore(
        JsonElement dimension,
        string property = "overall-score"
    )
    {
        if (
            dimension.ValueKind == JsonValueKind.Object
            && dimension.TryGetProperty(property, out var score)
            && score.ValueKind == JsonValueKind.Number
        )
            return score.GetDouble();
        return 0;
    }

    /// <summary>读取维度结果中的字符串字段（缺省空串）。</summary>
    protected static string GetDimensionString(JsonElement dimension, string property)
    {
        if (
            dimension.ValueKind == JsonValueKind.Object
            && dimension.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
        )
            return value.GetString() ?? "";
        return "";
    }

    /// <summary>读取维度结果中的整数字段（缺省 0）。</summary>
    protected static int GetDimensionInt(JsonElement dimension, string property)
    {
        if (
            dimension.ValueKind == JsonValueKind.Object
            && dimension.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
        )
            return value.GetInt32();
        return 0;
    }

    /// <summary>读取维度结果中的布尔字段（缺省 false）。</summary>
    protected static bool GetDimensionBool(JsonElement dimension, string property)
    {
        return dimension.ValueKind == JsonValueKind.Object
            && dimension.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// 从 characters 分组中按本地角色 ID（回退角色名）选择目标角色维度。
    /// 未指定 ID 或找不到时取第一个角色。
    /// </summary>
    protected static JsonElement SelectCharacterDimension(
        JsonElement charactersGroup,
        IEnumerable<Character>? novelCharacters,
        string? characterId
    )
    {
        string? targetName = null;
        if (!string.IsNullOrWhiteSpace(characterId) && novelCharacters is not null)
        {
            targetName = novelCharacters.FirstOrDefault(c => c.Id == characterId)?.Name;
            if (string.IsNullOrWhiteSpace(targetName))
                targetName = characterId; // 容错：允许直接传角色名
        }

        if (charactersGroup.ValueKind != JsonValueKind.Array)
            return default;

        var items = charactersGroup.EnumerateArray().ToList();
        if (items.Count == 0)
            return default;

        if (!string.IsNullOrWhiteSpace(targetName))
        {
            var match = items.FirstOrDefault(item =>
                item.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String
                && n.GetString() == targetName
            );
            if (match.ValueKind == JsonValueKind.Object)
                return match;
        }

        return items[0];
    }

    /// <summary>读取维度结果中的字符串数组字段（缺省空列表）。</summary>
    protected static List<string> ParseStringList(JsonElement dimension, string property)
    {
        var result = new List<string>();
        if (
            dimension.ValueKind == JsonValueKind.Object
            && dimension.TryGetProperty(property, out var array)
            && array.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    result.Add(item.GetString() ?? "");
            }
        }
        return result;
    }

    /// <summary>
    /// 从维度结果对象/数组解析云端问题列表。
    /// </summary>
    protected static List<CloudIssue> ParseDimensionIssues(JsonElement dimensionOrArray)
    {
        var array =
            dimensionOrArray.ValueKind == JsonValueKind.Array ? dimensionOrArray
            : dimensionOrArray.ValueKind == JsonValueKind.Object
            && dimensionOrArray.TryGetProperty("issues", out var issues)
                ? issues
            : default;
        return ParseIssueArray(array);
    }

    /// <summary>
    /// 从云端结果解析维度分数。
    /// </summary>
    protected static Dictionary<string, double> ParseDimensionScores(JsonElement element)
    {
        var scores = new Dictionary<string, double>();
        if (
            element.TryGetProperty("dimension-scores", out var dims)
            && dims.ValueKind == JsonValueKind.Object
        )
        {
            foreach (
                var prop in dims.EnumerateObject()
                    .Where(prop => prop.Value.ValueKind == JsonValueKind.Number)
            )
            {
                scores[prop.Name] = prop.Value.GetDouble();
            }
        }
        return scores;
    }

    /// <summary>
    /// 从云端结果解析问题列表。
    /// </summary>
    protected static List<CloudIssue> ParseCloudIssues(JsonElement element)
    {
        if (
            element.TryGetProperty("signals", out var signals)
            && signals.ValueKind == JsonValueKind.Array
        )
            return ParseIssueArray(signals);
        return [];
    }

    private static List<CloudIssue> ParseIssueArray(JsonElement array)
    {
        var issues = new List<CloudIssue>();
        if (array.ValueKind != JsonValueKind.Array)
            return issues;
        foreach (var signal in array.EnumerateArray())
        {
            if (signal.ValueKind != JsonValueKind.Object)
                continue;
            issues.Add(
                new CloudIssue
                {
                    Dimension = signal.TryGetProperty("dimension", out var dim)
                        ? dim.GetString() ?? ""
                        : "",
                    Severity = signal.TryGetProperty("severity", out var sev)
                        ? sev.GetString() ?? ""
                        : "",
                    Description = signal.TryGetProperty("description", out var desc)
                        ? desc.GetString() ?? ""
                        : "",
                    Evidence = signal.TryGetProperty("evidence", out var ev)
                        ? ev.GetString() ?? ""
                        : "",
                    Suggestion = signal.TryGetProperty("suggestion", out var sug)
                        ? sug.GetString() ?? ""
                        : "",
                    ParagraphIndex = signal.TryGetProperty("paragraph-index", out var pi)
                        ? pi.GetInt32()
                        : -1,
                    ImpactScore = signal.TryGetProperty("impact-score", out var imp)
                        ? imp.GetDouble()
                        : 0,
                }
            );
        }
        return issues;
    }

    /// <summary>
    /// 分数→emoji（维度分，0-100 百分制；各工具壳的维度表共用）。
    /// </summary>
    protected static string ScoreEmoji(double score) =>
        score switch
        {
            >= 80 => "🟢 优秀",
            >= 60 => "🟡 良好",
            >= 40 => "🟠 一般",
            _ => "🔴 较差",
        };

    protected static string IssueEmoji(string severity) =>
        severity switch
        {
            Severity.High => "🔴",
            Severity.Medium => "🟡",
            _ => "🟢",
        };
}

/// <summary>
/// 云端分析问题条目 — 通用 DTO，各工具壳映射为自己的详情类型。
/// </summary>
public sealed record CloudIssue
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Suggestion { get; init; } = "";
    public int ParagraphIndex { get; init; } = -1;
    public double ImpactScore { get; init; }
}
