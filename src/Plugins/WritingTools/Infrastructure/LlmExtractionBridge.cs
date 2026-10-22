using System.Text;
using System.Text.Json;
using ClientPlatform;
using Core.Agent;
using Core.Data;
using LlmTornado.Chat;

namespace Plugins.WritingTools.Infrastructure;

/// <summary>
/// LLM 语义提取桥接 — 一次 API 调用提取全部语义级信息。
/// 替代 jieba 分词 + Contains 匹配的角色/交互/行为识别。
/// Level 2 周期性调用（~1-3s/章），Level 1 用缓存兜底。
/// </summary>
/// <remarks>
/// 设计意图：
/// - 一次调用提取：角色出场、角色交互、行为语义、关键词修正
/// - 结果缓存到 ChapterFeatures，Level 1 优先使用缓存
/// - API 不可用时静默降级到 jieba + 否定词检测
/// - 远端模型选择：DeepSeek V4 Flash（快/便宜）或 DeepSeek V4（深度推理）
/// </remarks>
public static class LlmExtractionBridge
{
    // ═══════ 提取结果 ═══════

    /// <summary>
    /// 角色出场信息 — LLM 识别的角色在段落中的精确出现。
    /// </summary>
    public sealed record CharacterPresence(string Name, int[] ParagraphIndices, string[] Aliases);

    /// <summary>
    /// 角色交互 — LLM 语义分类的 A→B 交互事件。
    /// </summary>
    public sealed record CharacterInteraction(
        string From,
        string To,
        string Type,
        int ParagraphIndex
    );

    /// <summary>
    /// 主角行为语义 — LLM 判断的行为分类计数。
    /// </summary>
    public sealed record ProtagonistBehaviors(
        int RationalActions,
        int IrrationalActions,
        int CostPaid,
        int BenefitsWithoutCost,
        int Challenged,
        int GrowthMoments
    );

    /// <summary>
    /// 关键词修正 — LLM 对关键词命中数的语义级修正。
    /// </summary>
    public sealed record KeywordCorrection(
        string OriginalKeyword,
        int RawCount,
        int CorrectedCount,
        string Reason
    );

    /// <summary>
    /// LLM 语义提取完整结果。
    /// </summary>
    public sealed record LlmExtractionResult
    {
        public List<CharacterPresence> CharacterPresences { get; init; } = [];
        public List<CharacterInteraction> Interactions { get; init; } = [];
        public ProtagonistBehaviors? Behaviors { get; init; }
        public List<KeywordCorrection> KeywordCorrections { get; init; } = [];
        public string ModelUsed { get; init; } = "";
        public DateTime ExtractedAt { get; init; } = DateTime.UtcNow;
    }

    // ═══════ 提取方法 ═══════

    /// <summary>
    /// 从章节文本中提取全部语义级信息。
    /// 一次 API 调用，返回角色出场、交互、行为语义和关键词修正。
    /// </summary>
    /// <param name="agent">Agent 实例（提供 TornadoApi 和模型配置）</param>
    /// <param name="chapterText">章节文本</param>
    /// <param name="characters">角色列表（提供已知角色名和角色）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>提取结果；API 不可用时返回空结果</returns>
    public static async Task<LlmExtractionResult> ExtractAsync(
        Agent agent,
        string chapterText,
        List<Character> characters,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(chapterText))
            return new LlmExtractionResult();

        var api = agent.TornadoApi;
        if (api is null)
            return new LlmExtractionResult();

        var modelName = agent.State.ModelName;
        if (string.IsNullOrEmpty(modelName))
            return new LlmExtractionResult();

        var characterList = string.Join("、", characters.Select(c => $"{c.Name}({c.Role})"));

        var prompt = BuildExtractionPrompt(chapterText, characterList);

        var request = new ChatRequest
        {
            Model = modelName,
            Temperature = 0.1, // 低温度：提取任务需要确定性输出
        };

        var conversation = api.Chat.CreateConversation(request);
        conversation.AddSystemMessage(ExtractionSystemPrompt);
        conversation.AddUserMessage(prompt);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CloudTimings.AuxiliaryLlmTimeout);
            var sb = new StringBuilder();
            await foreach (
                var chunk in conversation.StreamResponseEnumerable(token: timeoutCts.Token)
            )
                sb.Append(chunk);
            var response = sb.ToString();
            if (string.IsNullOrWhiteSpace(response))
                return new LlmExtractionResult();

            return ParseExtractionResponse(response, modelName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 用户取消：重抛
        }
        catch (OperationCanceledException)
        {
            return new LlmExtractionResult(); // 超时：降级到 jieba 路径
        }
        catch
        {
            // API 不可用 → 静默降级到 jieba
            return new LlmExtractionResult();
        }
    }

    // ═══════ Prompt 设计 ═══════

    private const string ExtractionSystemPrompt = """
        你是一个中文小说文本分析专家。你的任务是从章节文本中精确提取语义级信息。
        你必须严格按 JSON 格式输出，不要输出任何其他内容。
        不要解释，不要评论，只输出 JSON。
        """;

    private static string BuildExtractionPrompt(string text, string characterList)
    {
        // 截断过长文本（避免 token 超限）
        var truncatedText = text.Length > 8000 ? text[..8000] : text;

        var jsonTemplate = """
            {
              "character-presence": [
                {"name": "角色名", "paragraphs": [0, 2, 5], "aliases": ["别名1"]}
              ],
              "interactions": [
                {"from": "角色A", "to": "角色B", "type": "冲突|合作|情感交换|对话|命令|密谋", "paragraph": 0}
              ],
              "protagonist-behaviors": {
                "rational-actions": 3,
                "irrational-actions": 1,
                "cost-paid": 0,
                "benefits-without-cost": 2,
                "challenged": 0,
                "growth-moments": 1
              },
              "keyword-corrections": [
                {"original": "关键词", "raw-count": 2, "corrected-count": 1, "reason": "攻击性不算攻击"}
              ]
            }
            """;

        return $"""
            从以下章节文本中提取语义信息。

            已知角色：{characterList}

            章节文本：
            {truncatedText}

            请输出以下 JSON（严格格式，不要输出其他内容）：

            {jsonTemplate}

            规则：
            1. character-presence：只列出实际出场的角色，paragraphs 是 0-indexed 段落索引
            2. interactions：只记录有真实交互的事件（同场但无互动不算），type 必须是六种之一
            3. protagonist-behaviors：只统计主角的行为，rational=理性决策，irrational=冲动/情绪化
            4. keyword-corrections：只修正确实存在误匹配的关键词，没有误匹配则输出空数组
            5. 如果某项没有数据，输出空数组或 null
            """;
    }

    // ═══════ 响应解析 ═══════

    private static LlmExtractionResult ParseExtractionResponse(string response, string modelUsed)
    {
        // 提取 JSON 块（可能被 markdown 代码块包裹）
        var json = ExtractJson(response);
        if (json is null)
            return new LlmExtractionResult();

        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var presences = new List<CharacterPresence>();
            if (root.TryGetProperty("character-presence", out var cpArr))
            {
                foreach (var item in cpArr.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var paragraphs = item.TryGetProperty("paragraphs", out var pArr)
                        ? pArr.EnumerateArray().Select(e => e.GetInt32()).ToArray()
                        : [];
                    var aliases = item.TryGetProperty("aliases", out var aArr)
                        ? aArr.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                        : [];

                    if (!string.IsNullOrEmpty(name))
                        presences.Add(new CharacterPresence(name, paragraphs, aliases));
                }
            }

            var interactions = new List<CharacterInteraction>();
            if (root.TryGetProperty("interactions", out var iArr))
            {
                foreach (var item in iArr.EnumerateArray())
                {
                    var from = item.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
                    var to = item.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
                    var type = item.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                    var para = item.TryGetProperty("paragraph", out var pi) ? pi.GetInt32() : -1;

                    if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                        interactions.Add(new CharacterInteraction(from, to, type, para));
                }
            }

            ProtagonistBehaviors? behaviors = null;
            if (
                root.TryGetProperty("protagonist-behaviors", out var bObj)
                && bObj.ValueKind == JsonValueKind.Object
            )
            {
                behaviors = new ProtagonistBehaviors(
                    RationalActions: bObj.TryGetProperty("rational-actions", out var ra)
                        ? ra.GetInt32()
                        : 0,
                    IrrationalActions: bObj.TryGetProperty("irrational-actions", out var ia)
                        ? ia.GetInt32()
                        : 0,
                    CostPaid: bObj.TryGetProperty("cost-paid", out var cp) ? cp.GetInt32() : 0,
                    BenefitsWithoutCost: bObj.TryGetProperty("benefits-without-cost", out var bw)
                        ? bw.GetInt32()
                        : 0,
                    Challenged: bObj.TryGetProperty("challenged", out var ch) ? ch.GetInt32() : 0,
                    GrowthMoments: bObj.TryGetProperty("growth-moments", out var gm)
                        ? gm.GetInt32()
                        : 0
                );
            }

            var corrections = new List<KeywordCorrection>();
            if (root.TryGetProperty("keyword-corrections", out var kcArr))
            {
                foreach (var item in kcArr.EnumerateArray())
                {
                    var original = item.TryGetProperty("original", out var o)
                        ? o.GetString() ?? ""
                        : "";
                    var rawCount = item.TryGetProperty("raw-count", out var rc) ? rc.GetInt32() : 0;
                    var correctedCount = item.TryGetProperty("corrected-count", out var cc)
                        ? cc.GetInt32()
                        : 0;
                    var reason = item.TryGetProperty("reason", out var r)
                        ? r.GetString() ?? ""
                        : "";

                    if (!string.IsNullOrEmpty(original))
                        corrections.Add(
                            new KeywordCorrection(original, rawCount, correctedCount, reason)
                        );
                }
            }

            return new LlmExtractionResult
            {
                CharacterPresences = presences,
                Interactions = interactions,
                Behaviors = behaviors,
                KeywordCorrections = corrections,
                ModelUsed = modelUsed,
            };
        }
        catch (JsonException)
        {
            return new LlmExtractionResult();
        }
    }

    /// <summary>
    /// 从 LLM 响应中提取 JSON 块。
    /// 支持：纯 JSON、markdown 代码块包裹、混合文本中的 JSON。
    /// </summary>
    internal static string? ExtractJson(string response)
    {
        // 1. 尝试 markdown 代码块
        var codeBlockStart = response.IndexOf("```json", StringComparison.Ordinal);
        if (codeBlockStart >= 0)
        {
            var jsonStart = codeBlockStart + 7; // "```json" length
            var jsonEnd = response.IndexOf("```", jsonStart, StringComparison.Ordinal);
            if (jsonEnd >= 0)
                return response[jsonStart..jsonEnd].Trim();
        }

        // 2. 尝试裸代码块
        codeBlockStart = response.IndexOf("```", StringComparison.Ordinal);
        if (codeBlockStart >= 0)
        {
            var jsonStart = codeBlockStart + 3;
            var jsonEnd = response.IndexOf("```", jsonStart, StringComparison.Ordinal);
            if (jsonEnd >= 0)
                return response[jsonStart..jsonEnd].Trim();
        }

        // 3. 尝试找 { ... } 块
        var braceStart = response.IndexOf('{');
        if (braceStart >= 0)
        {
            var depth = 0;
            for (var i = braceStart; i < response.Length; i++)
            {
                if (response[i] == '{')
                    depth++;
                else if (response[i] == '}')
                    depth--;
                if (depth == 0)
                    return response[braceStart..(i + 1)];
            }
        }

        return null;
    }
}
