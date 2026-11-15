using System.Text;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using AgentCore = Core.Agent.Agent;

namespace Core.Prose;

/// <summary>
/// 基于当前配置模型的分隔符 harness。
/// 一次调用返回完整修订章节，由 <see cref="ProseHarnessValidator"/> 只校验正文是否被改动。
/// </summary>
public sealed class LlmProseHarness(AgentCore agent, ILogger<LlmProseHarness>? logger = null)
    : IProseHarness
{
    private static readonly TimeSpan ReviewTimeout = TimeSpan.FromMinutes(3);
    private const int MaxAttempts = 2;

    public async Task<ProseHarnessResult> ReviewAsync(
        ProseHarnessRequest request,
        CancellationToken ct
    )
    {
        var original = request.Content ?? "";
        if (string.IsNullOrWhiteSpace(original))
            return ProseHarnessResult.Unchanged(original, 0);

        var api = agent.TornadoApi;
        var modelName = agent.State.ModelName;
        if (api is null || string.IsNullOrWhiteSpace(modelName))
        {
            logger?.LogInformation("AI 分隔符 harness 未运行：未配置 LLM");
            return ProseHarnessResult.Unavailable(original, "未配置 LLM");
        }

        string? repairHint = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var raw = await CallAsync(api, modelName, request, repairHint, ct);
                var parsed = ProseHarnessOutputParser.Parse(raw);
                if (
                    ProseHarnessValidator.TryValidate(
                        original,
                        parsed.Content,
                        out var content,
                        out var summary,
                        out var error
                    )
                )
                {
                    var status = string.Equals(content, original, StringComparison.Ordinal)
                        ? ProseHarnessStatus.Unchanged
                        : ProseHarnessStatus.Applied;
                    logger?.LogInformation(
                        "AI 分隔符 harness {Status}：{NovelId}/{ChapterTitle}，{Original} → {Revised} 处；{Notes}",
                        status,
                        request.NovelId,
                        request.ChapterTitle,
                        summary.OriginalSeparators,
                        summary.RevisedSeparators,
                        Summarize(parsed.Notes)
                    );
                    return new ProseHarnessResult(status, content, summary, parsed.Notes);
                }

                repairHint = error;
                logger?.LogWarning(
                    "AI 分隔符 harness 输出未通过正文保持校验（第 {Attempt}/{MaxAttempts} 次）：{Error}",
                    attempt,
                    MaxAttempts,
                    error
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                repairHint = "调用超时";
                logger?.LogWarning(
                    "AI 分隔符 harness 调用超时（第 {Attempt}/{MaxAttempts} 次）",
                    attempt,
                    MaxAttempts
                );
            }
            catch (Exception ex)
            {
                repairHint = "调用失败：" + ex.Message;
                logger?.LogWarning(
                    ex,
                    "AI 分隔符 harness 调用失败（第 {Attempt}/{MaxAttempts} 次）",
                    attempt,
                    MaxAttempts
                );
            }
        }

        return ProseHarnessResult.Failed(original, repairHint ?? "未返回可用正文");
    }

    private static async Task<string> CallAsync(
        LlmTornado.TornadoApi api,
        string modelName,
        ProseHarnessRequest request,
        string? repairHint,
        CancellationToken ct
    )
    {
        var chatRequest = new ChatRequest
        {
            Model = modelName,
            Temperature = 0.2,
            MaxTokens = Math.Clamp(request.Content.Length + 4096, 8192, 32768),
            ReasoningFormat = ChatReasoningFormats.Parsed,
            ReasoningEffort = ChatReasoningEfforts.Medium,
        };

        var conversation = api.Chat.CreateConversation(chatRequest);
        conversation.AddSystemMessage(ProseHarnessPrompt.System);
        conversation.AddUserMessage(ProseHarnessPrompt.BuildUser(request, repairHint));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReviewTimeout);

        var builder = new StringBuilder(request.Content.Length + 1024);
        await foreach (var chunk in conversation.StreamResponseEnumerable(token: timeout.Token))
            builder.Append(chunk);

        return builder.ToString();
    }

    private static string Summarize(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return "";

        var compact = notes.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 800 ? compact : compact[..800] + "…";
    }
}
