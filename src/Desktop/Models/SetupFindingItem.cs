using Desktop.ViewModels;
using Lucide.Avalonia;
using ReactiveUI;

namespace Desktop.Models;

/// <summary>
/// 设定检查 finding 绑定模型（CHANGE_10 · fiction.setup.check）。
/// 每个 finding 对应一条分析结果，含判定、证据、修复建议与反馈状态。
/// 继承 ViewModelBase 以支持 RaiseAndSetIfChanged 属性通知。
/// </summary>
public sealed class SetupFindingItem : ViewModelBase
{
    /// <summary>规则 ID（如 dup.scene.identical.{a}.{b}），用于采纳率埋点。</summary>
    public string Id
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>判定：conflict / anomaly / undetermined</summary>
    public string Verdict
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>判定图标（Lucide 单色描边，配色取 Adaline token——见 VerdictBrushKey）。</summary>
    public LucideIconKind VerdictIcon =>
        Verdict switch
        {
            "conflict" => LucideIconKind.OctagonX,
            "anomaly" => LucideIconKind.TriangleAlert,
            "undetermined" => LucideIconKind.CircleDotDashed,
            _ => LucideIconKind.Circle,
        };

    /// <summary>判定标签（字段备注：Fragment Mono 微标签）。</summary>
    public string VerdictLabel =>
        Verdict switch
        {
            "conflict" => "冲突",
            "anomaly" => "异常",
            "undetermined" => "待定",
            _ => "其他",
        };

    /// <summary>判定标签（字段备注：Fragment Mono 微标签）。</summary>
    public string Description
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>证据文本。</summary>
    public string? Evidence
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>修复候选（前端已格式化为 "A / B / C" 字符串）。</summary>
    public string? FixCandidates
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>建议文本。</summary>
    public string? Suggestion
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否有证据文本。</summary>
    public bool HasEvidence => !string.IsNullOrWhiteSpace(Evidence);

    /// <summary>是否有建议。</summary>
    public bool HasSuggestion => !string.IsNullOrWhiteSpace(Suggestion);

    /// <summary>是否有修复候选。</summary>
    public bool HasFixCandidates => !string.IsNullOrWhiteSpace(FixCandidates);

    /// <summary>涉及的实体 ID 列表（用于深链导航）。</summary>
    public IReadOnlyList<string>? InvolvedElementIds
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否有可解析的实体深链。</summary>
    public bool HasEntityLink => InvolvedElementIds is { Count: > 0 };

    /// <summary>是否已反馈（采纳/忽略后隐藏按钮）。</summary>
    public bool FeedbackSubmitted
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>反馈按钮是否可见。</summary>
    public bool CanFeedback => !FeedbackSubmitted;
}
