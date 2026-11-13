using System.Text.Json;
using Core.Data;
using Core.Interactions;
using Core.Shared;
using Core.Tools;
using LlmTornado.Code;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui;

public sealed partial class TuiApp
{
    // ── 变体审核 ──
    /// <summary>当前待处理的变体审核上下文（用户选择"继续修改"时暂存）。</summary>
    private PendingRefinement? _pendingRefinement;
    private ExploreDetails? _lastExploreDetails; // 最近的变体组（会话恢复/重新打开用）

    /// 暂存 refine 需要的 explore 上下文，用于 SubmitInput 中构造 Agent prompt。
    private sealed record PendingRefinement(
        VariantReviewDecision Decision,
        string? ExplorePrompt,
        string? ExploreType,
        VariantApplyTarget? ApplyTarget
    );

    /// <summary>
    /// 处理变体审核事件：持久化变体组 → 弹出审核对话框 → 根据用户选择路由到采用/修改/忽略。
    /// </summary>
    /// <param name="details">ExploreDetails 对象（事件参数）。</param>
    private async Task HandleVariantReviewAsync(object details)
    {
        if (details is not ExploreDetails explore)
            return;

        _lastExploreDetails = explore;

        // 持久化变体组，确保会话恢复后可以重新打开
        await PersistVariantSetAsync(explore);

        var decision = await VariantReviewDialog.ShowAsync(explore, _store);

        switch (decision.Action)
        {
            case VariantReviewAction.Apply:
                await HandleVariantApplyAsync(explore, decision);
                break;

            case VariantReviewAction.Refine:
                HandleVariantRefine(explore, decision);
                break;

            case VariantReviewAction.Dismiss:
                // 卡片保留在对话中，无需额外操作
                break;
        }
    }

    /// <summary>
    /// 持久化变体组到 JSONL（SessionEventType.VariantSetCreated），确保会话恢复后可重新打开。
    /// </summary>
    /// <param name="explore">变体组详情。</param>
    private async Task PersistVariantSetAsync(ExploreDetails explore)
    {
        try
        {
            var json = JsonSerializer.Serialize(explore, JsonHelper.JsonOptions);
            await _sessionStore.SaveSessionEvent(
                _sessionId,
                _agent.CurrentNovelId ?? "",
                new SessionEvent
                {
                    EventType = SessionEventType.VariantSetCreated,
                    ParentEntryId = _lastParentEntryId,
                    Order = _nextOrder,
                    Content = json,
                }
            );
            _nextOrder++;
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "persist variant set");
        }
    }

    /// <summary>执行 Core 下发的变体效果 plan；Host 只做副作用。</summary>
    private async Task ExecuteVariantEffectAsync(VariantEffect effect)
    {
        switch (effect.Kind)
        {
            case VariantEffectKind.Dismiss:
                return;
            case VariantEffectKind.PrefillRefinement:
                QueueVariantRefinement(
                    new VariantReviewDecision
                    {
                        Action = VariantReviewAction.Refine,
                        VariantId = effect.VariantId,
                        VariantLabel = effect.VariantLabel,
                        VariantContent = effect.VariantContent,
                    },
                    effect.ExplorePrompt,
                    effect.ExploreType,
                    effect.ApplyTarget
                );
                return;
        }

        if (_sessionPersistenceFaulted)
        {
            ToastService.Error("会话保存故障尚未恢复，已拒绝采用变体以防止历史记录缺失。");
            return;
        }

        switch (effect.Kind)
        {
            case VariantEffectKind.AppendToContext:
                _agent.AppendMessage(
                    new LlmChatMessage(ChatMessageRoles.Assistant, effect.VariantContent)
                );
                // ★ 变体内容写入上下文（无新 API 调用）：重算 token 显示
                InvalidateContextMeasure();
                UpdateTokenDisplay();
                if (!await TryPersistNewMessagesAsync())
                    return;
                _conversationPanel.AddSystemMessage($"✅ 已采用变体「{effect.VariantLabel}」");
                return;
            case VariantEffectKind.SendExtractionPrompt:
                var ct = _appCts?.Token ?? CancellationToken.None;
                _ = RunPromptAsync(effect.ExtractionPrompt, ct);
                _conversationPanel.AddSystemMessage(
                    $"📋 已采用变体「{effect.VariantLabel}」，正在提取结构化数据…"
                );
                return;
            case VariantEffectKind.WriteChapter:
                await WriteVariantChapterAsync(effect);
                return;
            default:
                throw new InvalidOperationException($"未知变体效果: {effect.Kind}");
        }
    }

    /// <summary>执行章节写入效果；确认/冲突校验由效果 plan 的标记驱动。</summary>
    private async Task WriteVariantChapterAsync(VariantEffect effect)
    {
        var target = effect.ApplyTarget;
        if (target is null)
        {
            _conversationPanel.AddSystemMessage("无法应用变体：缺少章节目标");
            return;
        }

        var variantContent = effect.VariantContent;
        var variantLabel = effect.VariantLabel;
        var chapterTitle = target.Title;
        var chapterOrder = target.Order;

        if (
            !string.IsNullOrEmpty(target.ChapterId)
            && (string.IsNullOrWhiteSpace(chapterTitle) || chapterOrder <= 0)
        )
        {
            var lookup = await _store.GetChapter(target.NovelId, target.ChapterId!);
            if (lookup is not null)
            {
                chapterTitle = lookup.Title;
                chapterOrder = lookup.Order;
            }
        }
        if (string.IsNullOrWhiteSpace(chapterTitle) || chapterOrder <= 0)
        {
            _conversationPanel.AddSystemMessage("无法应用变体：缺少章节标题或序号");
            return;
        }

        if (effect.RequiresOverwriteConfirmation)
        {
            // ChapterId 非空由 RequiresOverwriteConfirmation 保证
            var existing = await _store.GetChapter(target.NovelId, target.ChapterId!);
            if (existing == null)
            {
                _conversationPanel.AddSystemMessage("无法覆盖：目标章节不存在");
                return;
            }

            // 版本保护：记录确认前的章节快照，写入前二次校验
            var snapshotContent = existing.Content;
            var snapshotLength = existing.Content.Length;

            var confirmed = await ShowApplyConfirmDialogAsync(
                existing.Title,
                existing.Content.Length,
                variantContent.Length
            );
            if (!confirmed)
                return;

            var recheck = await _store.GetChapter(target.NovelId, target.ChapterId!);
            if (recheck?.Content != snapshotContent)
            {
                var recheckConfirmed = await ShowVersionConflictDialogAsync(
                    existing.Title,
                    snapshotLength,
                    recheck?.Content.Length ?? 0
                );
                if (!recheckConfirmed)
                {
                    _conversationPanel.AddSystemMessage("覆盖已取消：章节在确认后发生了变化");
                    return;
                }
            }
        }

        // 通过 ToolRegistry 获取 write_chapter 工具，走正式执行路径
        var writeTool = _registry.TryGet("write_chapter");
        if (writeTool is null)
        {
            _conversationPanel.AddSystemMessage("错误：未找到写章节工具");
            return;
        }

        var toolParamsJson = JsonSerializer.Serialize(
            new
            {
                novelId = target.NovelId,
                title = chapterTitle,
                order = chapterOrder,
                content = variantContent,
                chapterId = target.ChapterId,
            },
            JsonHelper.JsonOptions
        );
        using var doc = JsonDocument.Parse(toolParamsJson);

        try
        {
            var result = await writeTool.ExecuteAsync(
                Guid.NewGuid().ToString("N")[..12],
                doc.RootElement,
                CancellationToken.None
            );

            var resultText = (result.Content.ToList().FirstOrDefault() as TextContent)?.Text
                ?? "已写入";
            if (!await TryPersistNewMessagesAsync())
                return;
            _conversationPanel.AddSystemMessage($"✅ {resultText}（采用「{variantLabel}」）");
        }
        catch (Exception ex)
        {
            _conversationPanel.AddSystemMessage($"写入失败：{ex.Message}");
        }

        await RefreshOutline();
    }

    /// <summary>
    /// 兼容无 Hub 的旧路径：把 ExploreDetails/Decision 适配成 Core 效果 plan 后执行。
    /// </summary>
    private async Task HandleVariantApplyAsync(
        ExploreDetails explore,
        VariantReviewDecision decision
    )
    {
        var variantContent = decision.VariantContent ?? "";
        var variantLabel = decision.VariantLabel ?? "";
        var variantId = decision.VariantId ?? "";
        var target = explore.ApplyTarget;

        VariantEffect effect;
        if (target is not null)
        {
            effect = new VariantEffect(
                Guid.NewGuid().ToString("N")[..12],
                VariantEffectKind.WriteChapter,
                variantId,
                variantLabel,
                variantContent,
                explore.ExploreType,
                explore.Prompt,
                target,
                "",
                RequiresOverwriteConfirmation: !string.IsNullOrEmpty(target.ChapterId)
            );
        }
        else if (
            explore.ExploreType is "character" or "plot" or "outline" or "style"
            && !string.IsNullOrWhiteSpace(variantContent)
        )
        {
            var typeName = explore.ExploreType switch
            {
                "character" => "角色",
                "plot" => "情节线",
                "outline" => "大纲",
                _ => "创作内容",
            };
            var variantXml = VariantXml.Wrap(
                variantContent,
                variantId,
                variantLabel,
                explore.ExploreType
            );
            var extractPrompt =
                $"用户从探索结果中选择了变体「{variantLabel}」——"
                + $"一个{typeName}方案。\n\n"
                + variantXml
                + "\n\n请根据以上内容，使用相应的工具（如 batch_create_characters）创建结构化实体。"
                + "如果内容为大纲/风格描述，直接将其作为参考存入上下文，不需要创建实体。";
            effect = new VariantEffect(
                Guid.NewGuid().ToString("N")[..12],
                VariantEffectKind.SendExtractionPrompt,
                variantId,
                variantLabel,
                variantContent,
                explore.ExploreType,
                explore.Prompt,
                null,
                extractPrompt,
                false
            );
        }
        else
        {
            effect = new VariantEffect(
                Guid.NewGuid().ToString("N")[..12],
                VariantEffectKind.AppendToContext,
                variantId,
                variantLabel,
                variantContent,
                explore.ExploreType,
                explore.Prompt,
                null,
                "",
                false
            );
        }

        await ExecuteVariantEffectAsync(effect);
    }

    /// <summary>
    /// 版本冲突弹窗 — 确认→写入之间章节被外部修改。
    /// </summary>
    private static Task<bool> ShowVersionConflictDialogAsync(
        string title,
        int expectedLen,
        int actualLen
    )
    {
        var tcs = new TaskCompletionSource<bool>();

        var dialog = new Dialog()
            .Style(PanelThemes.DesertDialog)
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("Version Conflict")
            .Width(52)
            .MinHeight(10)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(
                PanelThemes.CreateDialogContent(
                    new VStack(
                        new Markup($"[bold]「{title}」[/] was modified after confirm."),
                        new Markup(
                            $"[dim]Expected: {expectedLen} chars → Actual: {actualLen} chars[/]"
                        ),
                        new Markup("[dim]Force overwrite will discard external changes.[/]")
                    ).Spacing(1)
                )
            );

        var cancelBtn = new Button(new Markup("[dim]Cancel[/]"))
            .Style(PanelThemes.DesertButton)
            .Click(() =>
            {
                tcs.TrySetResult(false);
                dialog.Close();
            });
        var forceBtn = new Button(new Markup("[bold]Force Overwrite[/]"))
            .Style(PanelThemes.DesertButtonDanger)
            .Click(() =>
            {
                tcs.TrySetResult(true);
                dialog.Close();
            });

        dialog.BottomRightText(new HStack(cancelBtn, forceBtn).Spacing(2));
        dialog.BottomLeftText(new Markup("[dim]Esc to cancel[/]"));

        var closeBtn = PanelThemes.CreateDialogCloseButton(() =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        });
        dialog.TopRightText(closeBtn);

        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    tcs.TrySetResult(false);
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );

        dialog.Show();
        return tcs.Task;
    }

    /// <summary>
    /// 覆盖章节确认弹窗。
    /// </summary>
    private static Task<bool> ShowApplyConfirmDialogAsync(string title, int oldSize, int newSize)
    {
        var tcs = new TaskCompletionSource<bool>();

        var dialog = new Dialog()
            .Style(PanelThemes.DesertDialog)
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("Confirm Overwrite")
            .Width(52)
            .MinHeight(10)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(
                PanelThemes.CreateDialogContent(
                    new VStack(
                        new Markup($"This will overwrite [bold]「{title}」[/]."),
                        new Markup($"[dim]{oldSize} chars → {newSize} chars[/]"),
                        new Markup("[dim]This action cannot be undone.[/]")
                    ).Spacing(1)
                )
            );

        var cancelBtn = new Button(new Markup("[dim]Cancel[/]"))
            .Style(PanelThemes.DesertButton)
            .Click(() =>
            {
                tcs.TrySetResult(false);
                dialog.Close();
            });
        var confirmBtn = new Button(new Markup("[bold]Confirm Overwrite[/]"))
            .Style(PanelThemes.DesertButtonAccent)
            .Click(() =>
            {
                tcs.TrySetResult(true);
                dialog.Close();
            });

        dialog.BottomRightText(new HStack(cancelBtn, confirmBtn).Spacing(2));
        dialog.BottomLeftText(new Markup("[dim]Esc to cancel[/]"));

        var closeBtn = PanelThemes.CreateDialogCloseButton(() =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        });
        dialog.TopRightText(closeBtn);

        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    tcs.TrySetResult(false);
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );

        dialog.Show();
        return tcs.Task;
    }

    /// <summary>
    /// 继续修改：将变体内容暂存，预填主输入框，等待用户输入修改要求。
    /// </summary>
    private void HandleVariantRefine(ExploreDetails explore, VariantReviewDecision decision) =>
        QueueVariantRefinement(decision, explore.Prompt, explore.ExploreType, explore.ApplyTarget);

    private void QueueVariantRefinement(
        VariantReviewDecision decision,
        string? explorePrompt,
        string? exploreType,
        VariantApplyTarget? applyTarget
    )
    {
        _pendingRefinement = new PendingRefinement(
            Decision: decision,
            ExplorePrompt: explorePrompt,
            ExploreType: exploreType,
            ApplyTarget: applyTarget
        );

        var label = decision.VariantLabel ?? "variant";
        _inputText.Value = $"修改「{label}」版本：";
        _promptEditor.CaretIndex = _inputText.Value?.Length ?? 0;

        _conversationPanel.AddSystemMessage($"📝 请输入对「{label}」的修改要求，按 Enter 发送");
    }

    /// <summary>
    /// 重新打开最近的变体组：优先使用内存中的 _lastExploreDetails，
    /// 否则从 JSONL 反序列化，无可用变体时提示用户。
    /// </summary>
    private async Task ReopenLastVariantSetAsync()
    {
        var explore = _lastExploreDetails;
        if (explore is null)
        {
            try
            {
                var json = await _sessionStore.GetLastVariantSetContentAsync(_sessionId);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    explore = JsonSerializer.Deserialize<ExploreDetails>(
                        json,
                        JsonHelper.JsonOptions
                    );
                }
            }
            catch (Exception ex)
            {
                Services.TuiLogger.OperationFailed(_logger, ex, "reopen variant set");
            }
        }

        if (explore?.Variants is not { Count: > 0 })
        {
            ToastService.Info("没有待选择的变体");
            return;
        }
        _lastExploreDetails = explore;

        if (_interactionHub is null)
        {
            // 兼容无 hub 的测试/旧宿主；生产路径必须走 Core 认领与提交。
            await HandleVariantReviewAsync(explore);
            return;
        }
        if (string.IsNullOrEmpty(_sessionId))
        {
            ToastService.Warning("当前会话尚未建立，无法重开变体");
            return;
        }

        var reopened = _interactionHub.ReopenVariantReview(
            new InteractionContext(TuiHostId, _sessionId, _agent.CurrentNovelId),
            explore
        );
        if (reopened is null)
        {
            ToastService.Info("没有待选择的变体");
            return;
        }
        OnInteractionChanged(reopened);
    }
}
