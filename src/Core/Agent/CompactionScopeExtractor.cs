using System.Text;
using System.Text.Json;
using Core.Novels;
using LlmTornado.Chat;

namespace Core.Agent;

internal sealed record CompactionScopeExtraction(bool Completed, CompactionTaskScope? Scope);

/// <summary>
/// compact 时让 LLM 从“最近作者消息 + 上一份摘要”提取任务边界。
/// 不做本地持久化：Scope 只是一次压缩的推导结果，下一次压缩重新判断。
/// </summary>
internal static class CompactionScopeExtractor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task<CompactionScopeExtraction> TryExtractAsync(
        Agent agent,
        NovelStructure structure,
        string? latestAuthorMessage,
        string? previousSummary,
        CancellationToken ct
    )
    {
        var api = agent.TornadoApi;
        var modelName = agent.State.ModelName;
        if (api is null || string.IsNullOrEmpty(modelName))
            return new CompactionScopeExtraction(false, null);

        var request = new ChatRequest
        {
            Model = modelName,
            Temperature = 0.1,
            MaxTokens = 800,
        };
        var conversation = api.Chat.CreateConversation(request);
        conversation.AddSystemMessage(
            "你是小说写作任务边界提取器。只输出 JSON，不要 Markdown，不要解释。"
                + "你必须从最近作者消息判断任务范围；上一份摘要只用于继承旧任务。"
                + "全局写作前沿不是任务范围。"
        );
        conversation.AddUserMessage(BuildPrompt(latestAuthorMessage, previousSummary, structure));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var builder = new StringBuilder();
        try
        {
            await foreach (var chunk in conversation.StreamResponseEnumerable(token: timeout.Token))
                builder.Append(chunk);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new CompactionScopeExtraction(false, null);
        }

        return ParseSelection(builder.ToString(), structure, latestAuthorMessage);
    }

    internal static CompactionScopeExtraction ParseSelection(
        string raw,
        NovelStructure structure,
        string? fallbackInstruction
    )
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
            return new CompactionScopeExtraction(false, null);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var mode = GetString(root, "mode");
            if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
                return new CompactionScopeExtraction(true, null);

            var instruction = GetString(root, "instruction");
            if (string.IsNullOrWhiteSpace(instruction))
                instruction = fallbackInstruction;

            var volumeIds = GetStringArray(root, "volume_ids", "volumeIds");
            var chapterOrders = GetIntArray(root, "chapter_orders", "chapterOrders");
            var scope = CompactionTaskScope.FromLlmSelection(
                instruction,
                mode,
                volumeIds,
                chapterOrders,
                structure
            );
            // write/revise 却选不出任何卷/章 = 半成品回答，与解析失败同构：
            // 返回未完成，让上游走 TryCreate 兜底。不能把“有任务却无范围”当成
            // “无任务”——那会关闭 EnforceTaskScope 并丢掉 Progress，
            // 让全局写作前沿再次渗进 Next。mode=none 仍表示作者真的取消了任务。
            return new CompactionScopeExtraction(scope is not null, scope);
        }
        catch (JsonException)
        {
            return new CompactionScopeExtraction(false, null);
        }
    }

    private static string BuildPrompt(
        string? latestAuthorMessage,
        string? previousSummary,
        NovelStructure structure
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 最近作者消息（Scope 的唯一新来源）");
        sb.AppendLine(
            string.IsNullOrWhiteSpace(latestAuthorMessage)
                ? "- （本轮没有新的作者消息；只能继承上一份摘要里的作者指令/Scope）"
                : $"- {Truncate(latestAuthorMessage.ReplaceLineEndings(" "), 2_000)}"
        );
        sb.AppendLine();
        sb.AppendLine("## 上一份摘要（只用于继承旧任务，不得从全局前沿推导）");
        sb.AppendLine(
            string.IsNullOrWhiteSpace(previousSummary)
                ? "- （无）"
                : Truncate(previousSummary, 4_000)
        );
        sb.AppendLine();
        sb.AppendLine("## 卷章目录（volume_ids/chapter_orders 必须从这里选）");
        AppendCatalog(sb, structure);
        sb.AppendLine();
        sb.AppendLine("## 判断规则");
        sb.AppendLine(
            "1. 最近作者消息明确说“整个/全部/这一卷/第X-Y章/接下来只写…”才建立新 Scope。"
        );
        sb.AppendLine(
            "2. 最近作者消息只是“继续/好/可以/按这个来”，继承上一份摘要的作者指令/Scope。"
        );
        sb.AppendLine(
            "3. 最近作者消息只是单章/单段修改（如“把第54章第三段改一下”），且上一份摘要已有长期 Scope：保持原 Scope，不要扩大到第54章。"
        );
        sb.AppendLine(
            "4. 当前章/下一章/全局写作前沿只是全书进度，不是任务范围；绝不能用它扩大 Scope。"
        );
        sb.AppendLine(
            "5. 没有新的作者消息时，上一份摘要里的 Scope 原样继承；不得把“没有新指令”解释为任务结束或切换。"
        );
        sb.AppendLine(
            "6. 上一份摘要里的 Progress/工具活动/章节编辑只是任务进度，不是新的任务范围。"
        );
        sb.AppendLine("7. 无法确定时输出 mode=none。");
        sb.AppendLine();
        sb.AppendLine("## 输出 JSON（只输出这个对象）");
        sb.AppendLine(
            "instruction 必须逐字复制作者任务原话；若继承上一份 Scope，复制上一份 Scope 的作者指令，不要概括。"
        );
        sb.AppendLine(
            """{"mode":"write|revise|none","instruction":"作者任务原话","volume_ids":["卷ID"],"chapter_orders":[21,22]}"""
        );
        return sb.ToString();
    }

    private static void AppendCatalog(StringBuilder sb, NovelStructure structure)
    {
        foreach (var volume in structure.Volumes.OrderBy(v => v.Order))
        {
            var chapters = structure
                .Chapters.Where(c => string.Equals(c.VolumeId, volume.Id, StringComparison.Ordinal))
                .OrderBy(c => c.Order)
                .ToList();
            if (chapters.Count == 0)
                continue;
            var orders = string.Join(
                "、",
                chapters.Select(c => c.CharCount > 0 ? $"{c.Order}*" : c.Order.ToString())
            );
            sb.AppendLine(
                $"第{volume.Order}卷《{volume.Name}》 ID:{volume.Id}：第{chapters[0].Order}-{chapters[^1].Order}章（* = 已有正文）"
            );
            sb.AppendLine($"  章节序号：{orders}");
        }
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static List<string> GetStringArray(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                continue;
            return value
                .EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
        }
        return [];
    }

    private static List<int> GetIntArray(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                continue;
            var result = new List<int>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number))
                    result.Add(number);
                else if (
                    item.ValueKind == JsonValueKind.String
                    && int.TryParse(item.GetString(), out number)
                )
                    result.Add(number);
            }
            return result;
        }
        return [];
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
