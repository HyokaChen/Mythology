using System.ComponentModel;
using Lucide.Avalonia;

namespace Desktop.Models;

/// <summary>
/// 智能体步骤：思考 / 工具调用 / 子步骤。
/// 在消息气泡内以步骤点列表展示（处理中 → 完成态动画）。
/// </summary>
public sealed class AgentStep : INotifyPropertyChanged
{
    private string? _resultSummary;

    /// <summary>步骤类型。</summary>
    public StepKind Kind { get; init; }

    /// <summary>显示标题（如工具名 / "思考中…"）。</summary>
    public string Title
    {
        get;
        init
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    } = "";

    /// <summary>当前状态。</summary>
    public AgentStepStatus Status
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProcessing)));
        }
    } = AgentStepStatus.Processing;

    /// <summary>是否进行中（驱动步骤行 spinner；对齐 TUI dots 动画）。</summary>
    public bool IsProcessing => Status == AgentStepStatus.Processing;

    /// <summary>状态图标（Lucide 单色描边，与 TodoPanel 状态同款体系）。</summary>
    public LucideIconKind StatusIcon =>
        Status switch
        {
            AgentStepStatus.Processing => LucideIconKind.LoaderCircle,
            AgentStepStatus.Completed => LucideIconKind.CircleCheck,
            AgentStepStatus.Failed => LucideIconKind.OctagonX,
            _ => LucideIconKind.Circle,
        };

    /// <summary>参数摘要（工具调用）。</summary>
    public string? ArgumentsSummary { get; set; }

    /// <summary>执行结果摘要。</summary>
    public string? ResultSummary
    {
        get => _resultSummary;
        set
        {
            _resultSummary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResult)));
        }
    }

    /// <summary>是否显示结果摘要。</summary>
    public bool HasResult => !string.IsNullOrWhiteSpace(_resultSummary);

    /// <summary>是否错误。</summary>
    public bool IsError { get; set; }

    /// <summary>思考内容（Thinking 步骤的流式文本）。</summary>
    public System.Text.StringBuilder ThinkingBuilder { get; } = new();

    /// <summary>通知 UI 内容已变化（流式思考文本追加后调用）。</summary>
    public void NotifyThinkingChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThinking)));
    }

    /// <summary>思考文本（UI 绑定）。</summary>
    public string ThinkingText => ThinkingBuilder.ToString();

    /// <summary>是否有思考内容（空思考不再占位;对齐 TUI）。</summary>
    public bool HasThinking => ThinkingBuilder.Length > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
}
