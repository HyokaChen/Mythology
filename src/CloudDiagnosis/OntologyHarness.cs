using Core.Agent;
using Core.Data;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace CloudDiagnosis;

/// <summary>
/// 本体驱动的写作前 harness（BeforeRun）：每章按「章节更新时间 + 本体日志长度」做变更戳，
/// 计算 delta/规则/死亡角色/悬空引用/无计划变化/情节线停转等信号，注入一条
/// 「本体变化 / 风险 / 建议 / 边界」提示。零 LLM、零云端积分。
/// </summary>
public sealed class OntologyHarness(
    Agent agent,
    INovelStore store,
    CloudDiagnosisOptions options,
    ILogger<OntologyHarness>? logger = null
)
{
    private readonly Dictionary<string, string> _seenStamps = new(StringComparer.Ordinal);
    private bool _digestInjected;

    public async Task RunAsync(CancellationToken ct = default)
    {
        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrWhiteSpace(novelId))
        {
            return;
        }

        // 跨会话摘要：每个 Agent 实例只注入一次，不依赖草稿章、不依赖云端开关。
        if (!_digestInjected)
        {
            await TryInjectDigestAsync(novelId, ct);
        }

        if (!options.OntologyHarnessEnabled)
        {
            return;
        }

        try
        {
            var novel = await store.GetNovel(novelId, ct);
            if (novel is null)
            {
                return;
            }
            var chapters = await store.GetChapters(novelId, ct);
            var chapter =
                chapters
                    .Where(c => c.Status == ChapterStatus.Draft)
                    .OrderBy(c => c.Order)
                    .LastOrDefault()
                ?? chapters.OrderBy(c => c.Order).LastOrDefault();
            if (chapter is null)
            {
                return;
            }

            var journalStamp = await store.GetOntologyJournalStamp(novelId, ct);
            var stamp = $"{chapter.UpdatedAt.Ticks}:{journalStamp}";
            if (_seenStamps.TryGetValue(chapter.Id, out var previous) && previous == stamp)
            {
                return;
            }

            var result = await OntologyHarnessEvaluator.EvaluateAsync(
                store,
                novel,
                chapter,
                options,
                ct
            );
            _seenStamps[chapter.Id] = stamp;
            if (string.IsNullOrWhiteSpace(result.Render))
            {
                return;
            }

            agent.InjectQueue.Enqueue(new LlmChatMessage(ChatMessageRoles.User, result.Render));
            logger?.LogInformation(
                "本体 harness 已注入：{NovelId}/{ChapterId}，{SignalCount} 风险 / {MaterialCount} 本体变化",
                novelId,
                chapter.Id,
                result.Signals.Count,
                result.MaterialDeltas.Count
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "本体 harness 计算失败（已忽略）");
        }
    }

    private async Task TryInjectDigestAsync(string novelId, CancellationToken ct)
    {
        try
        {
            var digest = await Core.Evolution.OntologyDigestBuilder.BuildAsync(
                store,
                novelId,
                agent.SessionId,
                ct
            );
            if (!string.IsNullOrWhiteSpace(digest.Text))
            {
                agent.InjectQueue.Enqueue(new LlmChatMessage(ChatMessageRoles.User, digest.Text));
            }
            // 注入成功才推进游标（没有内容时也推进：历史已无可注入项）。
            await store.SetOntologyCursorAsync(novelId, digest.NextOffset, ct);
            _digestInjected = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "跨会话本体摘要注入失败（下次重试）");
        }
    }
}
