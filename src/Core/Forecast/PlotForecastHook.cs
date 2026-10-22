using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Core.Data;
using Core.Tools;
using Core.Vector;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Forecast;

/// <summary>
/// 远期推演钩子：在写作/规划类工具成功后，把「下一章 + 未来 N 章细纲 + 计划转折 + 到期义务 +
/// 播种缺口 + 风险 + 笔记线索」以 <see cref="PlotForecastFormatter.Prefix"/> 前缀静默注入
/// <see cref="Core.Agent.Agent.InjectQueue"/>，让模型在下一轮据此对齐全书走向。
///
/// 去重按小说记指纹：同一本书推演结果没变就不再注入（每章最多一次）。
/// 任何失败都只记日志，绝不阻塞工具调用。
/// </summary>
public sealed class PlotForecastHook(
    Core.Agent.Agent agent,
    INovelStore store,
    RetrievalKernel? kernel = null,
    ILogger<PlotForecastHook>? logger = null
) : IToolHook
{
    /// <summary>触发工具：写作 / 细纲 / 情节线 / 笔记写操作。</summary>
    private static readonly HashSet<string> TriggerTools = new(StringComparer.Ordinal)
    {
        "write_chapter",
        "rewrite_selection",
        "build_scene",
        "manage_scene",
        "batch_create_scenes",
        "get_outline",
        "edit_entities",
        "manage_plotline",
        "advance_plotline",
        "suggest_continuation",
        "manage_note",
        "capture_note",
    };

    private readonly PlotForecastService _service = new(store, kernel);
    private readonly ConcurrentDictionary<string, string> _lastSentByNovel = new(
        StringComparer.Ordinal
    );

    public Task<ToolCallBeforeResult> BeforeToolCallAsync(
        ToolHookContext context,
        CancellationToken ct
    ) => Task.FromResult(ToolCallBeforeResult.Allow);

    public async Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct)
    {
        try
        {
            await RunAsync(context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 用户取消：上抛
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "远期推演钩子失败（已忽略）: {Tool}", context.ToolName);
        }
    }

    private async Task RunAsync(ToolHookContext context, CancellationToken ct)
    {
        if (context.Result is null or { IsError: true })
            return;
        if (!TriggerTools.Contains(context.ToolName))
            return;

        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
            return;

        var forecast = await _service.BuildAsync(novelId, PlotForecastBuilder.DefaultWindow, ct);
        if (forecast is null || !forecast.HasPlanData)
            return;

        var notes = await _service.SelectNotesAsync(novelId, forecast, 4, ct);
        var text = PlotForecastFormatter.Format(forecast, [], notes);

        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))[..8])
            .ToLowerInvariant();
        if (
            _lastSentByNovel.TryGetValue(novelId, out var last)
            && string.Equals(last, hash, StringComparison.Ordinal)
        )
        {
            return;
        }
        _lastSentByNovel[novelId] = hash;

        agent.InjectQueue.Enqueue(new LlmChatMessage(ChatMessageRoles.User, text));
    }
}
