using Core.Data;
using Core.Shared;

namespace Core.Tools;

/// <summary>decompress 工具参数。</summary>
public sealed class DecompressParams
{
    /// <summary>要恢复的压缩块 ID（如 "b1"），可从摘要消息的 [上下文摘要 b1] 获得。</summary>
    public string? BlockId { get; set; }

    /// <summary>true 时在工具结果中直接返回完整原文；false（默认）写入文件并返回路径+预览。</summary>
    public bool Inline { get; set; }
}

/// <summary>
/// decompress 工具：恢复之前压缩的消息原文（B2）。
///
/// 设计：压缩块保持折叠状态（摘要仍在上下文），decompress 只读不写状态。
/// 默认写文件到 ~/.cache/mythology/decompress/{blockId}-{ts}.txt（避免大内容撑爆上下文），
/// inline:true 才直接返回全文。blockId 从可见摘要消息 [上下文摘要 bN] 获取。
/// </summary>
public sealed class DecompressTool(Core.Agent.Agent agent, FileSessionStore sessionStore)
    : AgentTool<DecompressParams, string>
{
    public override string Name => "decompress";
    public override string Label => "恢复压缩内容";
    public override string Description =>
        "恢复之前被上下文压缩（compact，上下文预算管理）折叠的【对话消息】原文。"
        + "参数 blockId 从上下文中的摘要消息 [上下文摘要 bN] 获取；"
        + "不传 blockId 时列出所有可恢复的压缩块。默认把原文写入文件并返回路径与预览（避免撑爆上下文），"
        + "inline:true 时直接在工具结果中返回完整原文。压缩块保持折叠状态，不改变上下文。"
        + "注意：本工具只恢复被压缩的对话消息，与小说章节/作品文件内容无关——"
        + "章节是磁盘上的 .md 文件，用 read 读取；章节内容变少是文件本身状态，不产生压缩块。";

    public override ToolCategory Category => ToolCategory.Generic;

    public override async Task<AgentToolResult<string>> ExecuteAsync(
        string toolCallId,
        DecompressParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<string>>? onUpdate = null
    )
    {
        var blockId = args.BlockId?.Trim();
        var sessionId = agent.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            return new AgentToolResult<string>
            {
                Content = [new TextContent("当前没有活动会话，无法恢复压缩内容。")],
                IsError = true,
            };
        }

        if (string.IsNullOrWhiteSpace(blockId))
        {
            // 枚举所有压缩块，帮助模型定位
            var blocks = await sessionStore.ListCompactBlocksAsync(sessionId, ct);
            if (blocks.Count == 0)
            {
                return new AgentToolResult<string>
                {
                    Content =
                    [
                        new TextContent(
                            "当前没有可恢复的压缩块。本会话从未发生上下文压缩；"
                                + "压缩块仅保存被折叠的对话消息原文，与章节/文件内容无关。"
                        ),
                    ],
                    IsError = true,
                };
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("可恢复的压缩块：");
            foreach (var (id, summary, count) in blocks)
            {
                var preview = summary.Length > 80 ? summary[..80] + "…" : summary;
                sb.AppendLine($"- {id}（{count} 条消息）：{preview.ReplaceLineEndings(" ")}");
            }
            sb.AppendLine("调用 decompress({ blockId: \"bN\" }) 恢复指定块。");
            return new AgentToolResult<string>
            {
                Content = [new TextContent(sb.ToString())],
                Details = "blocks:" + string.Join(",", blocks.Select(b => b.BlockId)),
            };
        }

        var messages = await sessionStore.ReadCompactedMessagesAsync(sessionId, blockId, ct);
        if (messages is null || messages.Count == 0)
        {
            return new AgentToolResult<string>
            {
                Content =
                [
                    new TextContent(
                        $"压缩块 {blockId} 无法恢复：blockId 不存在，或该块创建于迁移前（无原文映射）。"
                    ),
                ],
                IsError = true,
            };
        }

        // 组装原文文本
        var text = string.Join(
            "\n\n",
            messages.Select(m =>
            {
                var role = m.Role switch
                {
                    MessageRole.User => "用户",
                    MessageRole.Assistant => "助手",
                    MessageRole.Tool => $"工具({m.ToolName ?? "?"})",
                    _ => m.Role.ToString(),
                };
                var content = m.Content;
                if (m.ReasoningContent is not null && !string.IsNullOrEmpty(m.ReasoningContent))
                    content = $"[思考]\n{m.ReasoningContent}\n\n{content}";
                return $"[{role}]\n{content}";
            })
        );

        if (args.Inline)
        {
            return new AgentToolResult<string>
            {
                Content = [new TextContent(text)],
                Details = $"{blockId}:{messages.Count}msgs",
            };
        }

        // 默认写文件 + 预览
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "mythology",
            "decompress"
        );
        Directory.CreateDirectory(cacheDir);
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var filePath = Path.Combine(cacheDir, $"{blockId}-{ts}.txt");
        await File.WriteAllTextAsync(filePath, text, ct);

        var previewText = text.Length > 600 ? text[..600] + "…" : text;
        return new AgentToolResult<string>
        {
            Content =
            [
                new TextContent(
                    $"压缩块 {blockId}（{messages.Count} 条消息，{text.Length:N0} 字符）已写入:\n{filePath}\n\n预览:\n{previewText}"
                ),
            ],
            Details = $"{blockId}:{messages.Count}msgs:{filePath}",
        };
    }
}
