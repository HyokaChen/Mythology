using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Microsoft.Extensions.Logging;

namespace CloudDiagnosis;

/// <summary>
/// 一次自动去 AI 味审查结果。Skipped=true 表示未调用云端（过短/已达次数/总开关关闭），
/// 调用方不应把它当成「云端不可用」；null 才表示调用失败/超时。
/// </summary>
public sealed record HumanizerAuditOutcome(
    DiagnosisReport? Report,
    int PassNumber,
    bool CloudAvailable = true,
    bool Skipped = false,
    string? SkipReason = null
);

/// <summary>
/// 自动去 AI 味审查（HARNESS-CLOUD 去AI味闭环）：
/// 与 story 调度解耦，每章写完/改写后独立跑 focused quick + humanizer focus（2 units）。
/// 同一章最多 <see cref="CloudDiagnosisOptions.HumanizerMaxPassesPerChapter"/> 次；
/// 同一内容指纹不重复调用；超时/失败返回 null，由 Hook 降级为本地自检，不阻塞创作。
/// 单写者假设（与 DiagnosisScheduler 相同）。
/// </summary>
public sealed class HumanizerAuditService(
    IFictionHarnessClient client,
    CloudDiagnosisOptions options,
    ILogger? logger = null
)
{
    private readonly IFictionHarnessClient _client = client;
    private readonly CloudDiagnosisOptions _options = options;
    private readonly ILogger? _logger = logger;

    /// <summary>chapterKey → 已成功审查次数（首检 + 复审）。</summary>
    private readonly Dictionary<string, int> _passCounts = new(StringComparer.Ordinal);

    /// <summary>chapterKey → 上次成功审查的内容指纹（相同内容不重复扣费）。</summary>
    private readonly Dictionary<string, string> _contentHashes = new(StringComparer.Ordinal);

    /// <summary>
    /// 执行一次自动审查。返回 null = 云端失败/超时（调用方注入「未完成」提醒）；
    /// 返回 Skipped = 无需调用（过短/已达次数/同内容/开关关闭）。
    /// </summary>
    public async Task<HumanizerAuditOutcome?> RunAsync(DiagnosisInput input, CancellationToken ct)
    {
        if (!_options.Enabled || !_options.HumanizerAutoPassEnabled)
            return Skipped("disabled");

        var content = input.CurrentContent ?? "";
        if (content.Length < _options.HumanizerMinContentChars)
            return Skipped("too-short");

        var key = $"{input.NovelId}#{input.ChapterId}";
        var hash = HashContent(content);
        if (_contentHashes.TryGetValue(key, out var previousHash) && previousHash == hash)
            return Skipped("same-content");

        var maxPasses = Math.Max(1, _options.HumanizerMaxPassesPerChapter);
        var completed = _passCounts.GetValueOrDefault(key);
        if (completed >= maxPasses)
            return Skipped("max-passes");

        var passNumber = completed + 1;
        var request = new HarnessRequest
        {
            NovelId = input.NovelId,
            Capability = "fiction.story.check",
            Depth = "quick",
            Locale = "zh-CN",
            Output = "report",
            Chapters =
            [
                new ChapterInput(
                    input.ChapterId,
                    input.ChapterOrder,
                    input.ChapterTitle,
                    "target",
                    content
                ),
            ],
            Options = new AnalysisOptionsInput(AnalyzerFocus: ["humanizer"]),
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(
                TimeSpan.FromSeconds(Math.Max(1, _options.HumanizerPassTimeoutSeconds))
            );

            var result = await _client.ExecuteAsync(
                request,
                $"humanizer-{input.NovelId}-{input.ChapterId}-{passNumber}-{hash[..16]}",
                timeout.Token
            );

            if (result.Result is not JsonElement root)
                return null;

            var report = ParseReport(root);
            if (report is null)
                return null;

            _passCounts[key] = passNumber;
            _contentHashes[key] = hash;
            return new HumanizerAuditOutcome(report, passNumber);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 本地超时：不消费次数，下一轮可重试；本次按不可用降级。
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "自动去AI味审查失败：{NovelId}/{ChapterId}",
                input.NovelId,
                input.ChapterId
            );
            return null;
        }
    }

    private static HumanizerAuditOutcome Skipped(string reason) =>
        new(null, 0, CloudAvailable: true, Skipped: true, SkipReason: reason);

    private static DiagnosisReport? ParseReport(JsonElement root)
    {
        try
        {
            var report = JsonSerializer.Deserialize<DiagnosisReport>(
                root.GetRawText(),
                ClientJsonDefaults.Cloud
            );
            if (
                report is null
                || string.IsNullOrWhiteSpace(report.Verdict)
                || string.IsNullOrWhiteSpace(report.Action)
                || string.IsNullOrWhiteSpace(report.Summary)
            )
                return null;
            return report with { Findings = report.Findings ?? [] };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string HashContent(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
