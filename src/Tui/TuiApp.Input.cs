using System.Text.RegularExpressions;
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

        var isColonCloud =
            text.StartsWith(":cloud")
            && (text.Length == ":cloud".Length || text[":cloud".Length] == ' ');
        if (text.StartsWith('/') || isColonCloud)
        {
            // 斜杠命令 + :cloud 冒号形式（login/status/logout）统一走命令路由；
            // 修复前仅 '/' 前缀进 HandleSlashCommand，:cloud xxx 实际被当作普通
            // 用户消息发给 LLM——启动引导 Toast 引导的 ":cloud login" 一直是死路径。
            _inputText.Value = "";
            HandleSlashCommand(text);
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

    /// <summary>实体引用正则表达式。</summary>
    [GeneratedRegex(@"#([\p{L}]+):([^\s\]\)]+)")]
    private static partial Regex EntityRefRegex();

    /// <summary>
    /// 将实体引用内联到文本中。
    /// 将 #类型:名称 格式转换为包含实体 ID 的格式。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>处理后的文本。</returns>
    private string InlineEntityReferences(string text)
    {
        return EntityRefRegex()
            .Replace(
                text,
                m =>
                {
                    var typeDisplay = m.Groups[1].Value;
                    // 将显示名（如"角色"）映射为内部实体类型键
                    var entityName = m.Groups[2].Value;
                    var typeKey = EntityDisplayHelper.ToEntityType(typeDisplay);
                    if (typeKey is null)
                        return m.Value;
                    // 通过 SetUpPanel 解析实体名称为 ID
                    var entityId = _setUpPanel.TryResolveEntityId(typeKey.Value, entityName);
                    // 无法解析时保留原始引用文本；解析成功则内联 ID
                    return entityId is null
                        ? m.Value
                        : $"{typeDisplay}[ID:{entityId}]:{entityName}";
                }
            );
    }

    /// <summary>路径正则表达式。</summary>
    [GeneratedRegex(@"@([\./~]?[\w./\-]+)")]
    private static partial Regex PathRegex();
}
