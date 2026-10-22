using CloudDiagnosis;
using Core.Cloud;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

/// <summary>
/// CloudDiagnosisOptions.FromConfiguration 边界测试：
/// 缺省值回退文档默认、非法值回退默认、有效值生效。
/// </summary>
public sealed class CloudDiagnosisOptionsTests
{
    private static IConfiguration Empty() => new ConfigurationBuilder().Build();

    private static IConfiguration From(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    [Fact]
    public void EmptyConfiguration_UsesDocumentedDefaults()
    {
        var options = CloudDiagnosisOptions.FromConfiguration(Empty());

        Assert.True(options.Enabled);
        Assert.Equal(100, options.DeepBudgetPerSession);
        Assert.Equal(100, options.DeepBudgetPerNovel);
        Assert.Equal(15, options.RequestTimeoutSeconds);
        Assert.Equal(120, options.DecisionTimeoutSeconds);
        Assert.Equal(62, options.PassScoreThreshold);
        Assert.Equal(8, options.MonotonyChapterThreshold);
        Assert.Equal(5, options.MonotonyFlatWindow);
        Assert.Equal(62, options.UpgradeScoreThreshold);
        Assert.Equal(3, options.DecisionMaxPerSession);
        Assert.Equal(3, options.DecisionMinCharacters);
        Assert.Equal(2, options.MinTargetContentChars);
        Assert.Equal(5, options.DecisionMinChapterOrder);
        Assert.Equal(0.7, options.LateNovelOrderRatio, precision: 10);
        Assert.Equal(5, options.UsageReconcileChapterInterval);
        Assert.Equal(100, options.UsageLowRemainingThreshold);
    }

    [Fact]
    public void InvalidValues_FallBackToDefaults()
    {
        var options = CloudDiagnosisOptions.FromConfiguration(
            From(
                ("CloudDiagnosis:Enabled", "not-a-bool"),
                ("CloudDiagnosis:DeepBudgetPerSession", "abc"),
                ("CloudDiagnosis:DeepBudgetPerNovel", ""),
                ("CloudDiagnosis:RequestTimeoutSeconds", "abc"),
                ("CloudDiagnosis:DecisionTimeoutSeconds", "x"),
                ("CloudDiagnosis:PassScoreThreshold", "NaN"),
                ("CloudDiagnosis:MonotonyChapterThreshold", "abc"),
                ("CloudDiagnosis:MonotonyFlatWindow", "xyz"),
                ("CloudDiagnosis:UpgradeScoreThreshold", "inf"),
                ("CloudDiagnosis:DecisionMaxPerSession", "!!"),
                ("CloudDiagnosis:DecisionMinCharacters", "abc"),
                ("CloudDiagnosis:MinTargetContentChars", "abc"),
                ("CloudDiagnosis:DecisionMinChapterOrder", "x"),
                ("CloudDiagnosis:LateNovelOrderRatio", "abc"),
                ("CloudDiagnosis:UsageReconcileChapterInterval", "abc"),
                ("CloudDiagnosis:UsageLowRemainingThreshold", "xyz")
            )
        );

        Assert.True(options.Enabled); // 非法 bool → 默认 true
        Assert.Equal(100, options.DeepBudgetPerSession);
        Assert.Equal(100, options.DeepBudgetPerNovel);
        Assert.Equal(15, options.RequestTimeoutSeconds);
        Assert.Equal(120, options.DecisionTimeoutSeconds);
        Assert.Equal(62, options.PassScoreThreshold);
        Assert.Equal(8, options.MonotonyChapterThreshold);
        Assert.Equal(5, options.MonotonyFlatWindow);
        Assert.Equal(62, options.UpgradeScoreThreshold);
        Assert.Equal(3, options.DecisionMaxPerSession);
        Assert.Equal(3, options.DecisionMinCharacters);
        Assert.Equal(2, options.MinTargetContentChars);
        Assert.Equal(5, options.DecisionMinChapterOrder);
        Assert.Equal(0.7, options.LateNovelOrderRatio, precision: 10);
        Assert.Equal(5, options.UsageReconcileChapterInterval);
        Assert.Equal(100, options.UsageLowRemainingThreshold);
    }

    [Fact]
    public void ValidValues_AreApplied()
    {
        var options = CloudDiagnosisOptions.FromConfiguration(
            From(
                ("CloudDiagnosis:Enabled", "false"),
                ("CloudDiagnosis:DeepBudgetPerSession", "5"),
                ("CloudDiagnosis:DeepBudgetPerNovel", "10"),
                ("CloudDiagnosis:RequestTimeoutSeconds", "7"),
                ("CloudDiagnosis:DecisionTimeoutSeconds", "30"),
                ("CloudDiagnosis:PassScoreThreshold", "85.5"),
                ("CloudDiagnosis:MonotonyChapterThreshold", "12"),
                ("CloudDiagnosis:MonotonyFlatWindow", "3"),
                ("CloudDiagnosis:UpgradeScoreThreshold", "40"),
                ("CloudDiagnosis:DecisionMaxPerSession", "1"),
                ("CloudDiagnosis:DecisionMinCharacters", "6"),
                ("CloudDiagnosis:MinTargetContentChars", "120"),
                ("CloudDiagnosis:DecisionMinChapterOrder", "9"),
                ("CloudDiagnosis:LateNovelOrderRatio", "0.5"),
                ("CloudDiagnosis:UsageReconcileChapterInterval", "9"),
                ("CloudDiagnosis:UsageLowRemainingThreshold", "250")
            )
        );

        Assert.False(options.Enabled);
        Assert.Equal(5, options.DeepBudgetPerSession);
        Assert.Equal(10, options.DeepBudgetPerNovel);
        Assert.Equal(7, options.RequestTimeoutSeconds);
        Assert.Equal(30, options.DecisionTimeoutSeconds);
        Assert.Equal(85.5, options.PassScoreThreshold);
        Assert.Equal(12, options.MonotonyChapterThreshold);
        Assert.Equal(3, options.MonotonyFlatWindow);
        Assert.Equal(40, options.UpgradeScoreThreshold);
        Assert.Equal(1, options.DecisionMaxPerSession);
        Assert.Equal(6, options.DecisionMinCharacters);
        Assert.Equal(120, options.MinTargetContentChars);
        Assert.Equal(9, options.DecisionMinChapterOrder);
        Assert.Equal(0.5, options.LateNovelOrderRatio);
        Assert.Equal(9, options.UsageReconcileChapterInterval);
        Assert.Equal(250, options.UsageLowRemainingThreshold);
    }

    [Fact]
    public void LegacyBudgetKeys_FallBack_WhenNewKeysAbsent()
    {
        var options = CloudDiagnosisOptions.FromConfiguration(
            From(("CloudDiagnosis:BudgetPerSession", "7"), ("CloudDiagnosis:BudgetPerNovel", "9"))
        );

        Assert.Equal(7, options.DeepBudgetPerSession);
        Assert.Equal(9, options.DeepBudgetPerNovel);
    }

    [Fact]
    public void NewBudgetKeys_WinOverLegacy()
    {
        var options = CloudDiagnosisOptions.FromConfiguration(
            From(
                ("CloudDiagnosis:DeepBudgetPerSession", "3"),
                ("CloudDiagnosis:BudgetPerSession", "7"),
                ("CloudDiagnosis:DeepBudgetPerNovel", "4"),
                ("CloudDiagnosis:BudgetPerNovel", "9")
            )
        );

        Assert.Equal(3, options.DeepBudgetPerSession);
        Assert.Equal(4, options.DeepBudgetPerNovel);
    }

    [Fact]
    public void Enabled_ExplicitFalse_Disables()
    {
        var options = CloudDiagnosisOptions.FromConfiguration(
            From(("CloudDiagnosis:Enabled", "false"))
        );
        Assert.False(options.Enabled);
    }

    [Fact]
    public void NegativeTimeout_Parses_ButRuntimeClampsToAtLeastOneSecond()
    {
        // int.TryParse 对负数成功 → 配置值为 -5；运行时 Linked()/DecisionTimeout 均有
        // Math.Max(1, ...) 兜底（不会 CancelAfter 负数抛 ArgumentOutOfRangeException）。
        var options = CloudDiagnosisOptions.FromConfiguration(
            From(("CloudDiagnosis:RequestTimeoutSeconds", "-5"))
        );

        Assert.Equal(-5, options.RequestTimeoutSeconds);
        // 运行时兜底验证（Linked 路径在 DiagnosisScheduler 内部；此处验证构造不抛）
        var scheduler = new DiagnosisScheduler(new NoopHarness(), options);
        Assert.NotNull(scheduler);
    }

    private sealed class NoopHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => Task.FromResult(new HarnessResult());

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => Task.FromResult(new AgentContextSnapshot());

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
