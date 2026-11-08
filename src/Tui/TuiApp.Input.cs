using System.Text.RegularExpressions;
using Core.Data;
using Core.Shared;
using LlmTornado.Code;
using Tui.Controls;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui;

public sealed partial class TuiApp
{
    private volatile bool _isPromptPending; // 粘贴防重：RunPromptAsync 尚未设 IsRunning 期间阻止重复提交

    // ── 输入处理 ──

    /// <summary>
    /// 提交用户输入：处理粘贴防重、Refinement 流程、斜杠命令、内联引用替换，
    /// 并根据 Agent 运行状态决定走 Steering（插队）还是新 Prompt。
    /// </summary>
    private void SubmitInput()
    {
        var text = _inputText.Value?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        _inputHistory.Add(text);
        _promptEditor.SetHistory(_inputHistory);
        _ = SaveInputHistoryAsync(text);

        // 斜杠命令优先于 pending refinement：/new、/clear 等不能被当成修改要求发出去。
        var isColonCloud =
            text.StartsWith(":cloud")
            && (text.Length == ":cloud".Length || text[":cloud".Length] == ' ');
        if (text.StartsWith('/') || isColonCloud)
        {
            _inputText.Value = "";
            HandleSlashCommand(text);
            return;
        }

        // Refinement 流程：用户正在响应"继续修改"的变体
        if (_pendingRefinement is not null)
        {
            var (decision, explorePrompt, exploreType, applyTarget) = _pendingRefinement;
            _pendingRefinement = null;
            _inputText.Value = "";

            var variantContent = decision.VariantContent ?? "";
            var variantLabel = decision.VariantLabel ?? "";
            var targetText =
                applyTarget is not null && !string.IsNullOrEmpty(applyTarget.Title)
                    ? $"第{applyTarget.Order}章「{applyTarget.Title}」"
                    : null;
            var modifiedText = text.StartsWith($"修改「{variantLabel}」版本：")
                ? text[$"修改「{variantLabel}」版本：".Length..].Trim()
                : text;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(
                $"用户从 explore 工具的结果中选择了变体「{variantLabel}」（ID: {decision.VariantId}），并要求基于此版本继续修改。"
            );
            sb.AppendLine();

            if (!string.IsNullOrEmpty(explorePrompt))
            {
                sb.AppendLine("===== 原始探索指令 =====\n");
                sb.AppendLine(explorePrompt);
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(exploreType))
            {
                sb.AppendLine("===== 探索类型 =====\n");
                sb.AppendLine(exploreType);
                sb.AppendLine();
            }

            if (applyTarget is not null && !string.IsNullOrEmpty(applyTarget.Title))
            {
                sb.AppendLine("===== 应用目标 =====\n");
                sb.AppendLine($"目标章节: {applyTarget.Title} (序号 {applyTarget.Order})");
                sb.AppendLine();
            }

            sb.AppendLine("===== 选中的变体内容 =====\n");
            sb.AppendLine(
                VariantXml.Wrap(
                    variantContent,
                    decision.VariantId,
                    variantLabel,
                    exploreType,
                    targetText
                )
            );
            sb.AppendLine();
            sb.AppendLine("===== 用户修改要求 =====\n");
            sb.AppendLine(modifiedText);

            var ct = _appCts?.Token ?? CancellationToken.None;
            _ = RunPromptAsync(sb.ToString(), ct);
            return;
        }

        _inputText.Value = "";

        // 预处理 @ 文件引用：转换为裸路径标记
        var processedText = InlineFileReferences(text);
        processedText = InlineEntityReferences(processedText);
        var ct2 = _appCts?.Token ?? CancellationToken.None;

        if (_agent.IsRunning || _isPromptPending)
        {
            // ★ Loop 运行中（或即将运行 — 粘贴防重）：消息入 SteeringQueue
            var steerId = Guid.NewGuid().ToString("N")[..12];
            _agent.Steer(new LlmChatMessage(ChatMessageRoles.User, processedText), steerId);
            _conversationPanel.AddWaitingUserMessage(processedText, steerId);
        }
        else
        {
            // ★ Loop 空闲：正常触发新对话
            _isPromptPending = true;
            _ = RunPromptAsync(processedText, ct2);
        }
    }

    /// <summary>将输入文本追加到会话输入历史并持久化。</summary>
    private async Task SaveInputHistoryAsync(string text)
    {
        await _sessionStore.SaveInputHistory(_sessionId, text);
    }

    /// <summary>
    /// 将文件引用内联到文本中。
    /// 将 @path 格式转换为纯路径（去掉 @ 前缀）。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>处理后的文本。</returns>
    private static string InlineFileReferences(string text)
    {
        // 去掉 @ 前缀，LLM 看到裸路径后可调用 read 工具获取内容
        return PathRegex()
            .Replace(
                text,
                m =>
                {
                    var pathPart = m.Groups[1].Value;
                    var resolved = PathHelper.ResolvePath(pathPart);
                    if (!File.Exists(resolved) && !Directory.Exists(resolved))
                        // 路径不存在时返回提示，避免 LLM 调用不存在的文件
                        return $"[文件不存在: {pathPart}]";
                    return pathPart;
                }
            );
    }

    /// <summary>引用起始（`#类型:`）；名称由 <see cref="EntityReferenceMatcher"/> 匹配（可含空格）。</summary>
    [GeneratedRegex(@"#([\p{L}]+):")]
    private static partial Regex EntityRefStartRegex();

    /// <summary>
    /// 将实体引用内联到文本中：`#类型:名称` → `类型[ID:xxx]:名称`。
    /// 名称按「最长且词边界结束」匹配，章节标题 / 卷名里的空格不会截断。
    /// 未知类型 / 名称查不到时保留原文。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>处理后的文本。</returns>
    private string InlineEntityReferences(string text)
    {
        var matches = EntityRefStartRegex().Matches(text);
        if (matches.Count == 0)
            return text;

        var refsByType = new Dictionary<EntityType, IReadOnlyList<(string Name, string Id)>>();
        var sb = new System.Text.StringBuilder(text.Length + 32);
        var last = 0;
        foreach (Match match in matches)
        {
            if (match.Index < last)
                continue; // 已落在上一个引用名称内部
            var typeDisplay = match.Groups[1].Value;
            var typeKey = EntityDisplayHelper.ToEntityType(typeDisplay);
            if (typeKey is null)
                continue;

            var nameStart = match.Index + match.Length;
            if (nameStart >= text.Length)
                continue;

            if (!refsByType.TryGetValue(typeKey.Value, out var refs))
            {
                refs = _setUpPanel.GetEntityRefsByType(typeKey.Value);
                refsByType[typeKey.Value] = refs;
            }

            var hit = EntityReferenceMatcher.MatchLongest(text, nameStart, refs);
            if (hit is null)
                continue;

            sb.Append(text, last, match.Index - last)
                .Append(typeDisplay)
                .Append("[ID:")
                .Append(hit.Value.Id)
                .Append("]:")
                .Append(hit.Value.Name);
            last = nameStart + hit.Value.Name.Length;
        }

        if (last == 0)
            return text;
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    /// <summary>路径正则表达式。</summary>
    [GeneratedRegex(@"@([\./~]?[\w./\-]+)")]
    private static partial Regex PathRegex();
}
