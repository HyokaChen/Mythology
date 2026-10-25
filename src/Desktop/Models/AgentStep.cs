using System.ComponentModel;
using Lucide.Avalonia;

namespace Desktop.Models;

/// <summary>
/// 智能体步骤：思考 / 工具调用 / 子步骤。
/// 在消息气泡内以步骤点列表展示（处理中 → 完成态动画）。
/// </summary>
public sealed class AgentStep : INotifyPropertyChanged
{
    private string? _argumentsSummary;
    private string? _resultSummary;
    private string? _fullArguments;
    private string? _fullResult;
    private IReadOnlyList<DiffLineView> _diffLines = [];
    private string? _diffSummary;
    private bool _isDetailsExpanded;

    /// <summary>步骤类型。</summary>
    public StepKind Kind { get; init; }

    /// <summary>
    /// 显示标题（工具名 / 思考步骤标签）。
    /// 思考步骤按状态派生：处理中「思考中…」，结束「思考过程」，避免结束态残留「思考中」。
    /// </summary>
    public string Title
    {
        get => Kind == StepKind.Thinking ? (IsProcessing ? "思考中…" : "思考过程") : field;
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    } = AgentStepStatus.Processing;

    /// <summary>是否进行中（驱动步骤行 spinner；对齐 TUI dots 动画）。</summary>
    public bool IsProcessing => Status == AgentStepStatus.Processing;

    /// <summary>是否为阶段正文步骤（无步骤头，轻量文本展示时间线）。</summary>
    public bool IsContentStep => Kind == StepKind.Content;

    /// <summary>是否为带步骤头的步骤（思考/工具/子步骤）。</summary>
    public bool IsNotContentStep => !IsContentStep;

    /// <summary>
    /// 阶段正文。工具或下一轮思考开始前，把当前流式正文冻结到独立步骤，
    /// 保证时间线是「思考 → 正文 → 工具 → 思考 → 最终正文」，而不是全部堆到回合末尾。
    /// 冻结后不再变化，用普通文本控件渲染，避免为每段中间正文新建 Markdown 渲染器造成卡顿。
    /// </summary>
    public string ContentText { get; init; } = "";

    /// <summary>创建一条已完成的历史/中间正文步骤。</summary>
    public static AgentStep CreateContent(string text) =>
        new()
        {
            Kind = StepKind.Content,
            Status = AgentStepStatus.Completed,
            ContentText = text,
        };

    /// <summary>状态图标（Lucide 单色描边，与 TodoPanel 状态同款体系）。</summary>
    public LucideIconKind StatusIcon =>
        Status switch
        {
            AgentStepStatus.Processing => LucideIconKind.LoaderCircle,
            AgentStepStatus.Completed => LucideIconKind.CircleCheck,
            AgentStepStatus.Failed => LucideIconKind.OctagonX,
            _ => LucideIconKind.Circle,
        };

    /// <summary>工具原始名（用于脱敏/调试；展示用 <see cref="Title"/>）。</summary>
    public string? ToolName { get; init; }

    /// <summary>参数摘要（工具调用，已去掉 JSON 结构）。</summary>
    public string? ArgumentsSummary
    {
        get => _argumentsSummary;
        set
        {
            _argumentsSummary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArgumentsSummary)));
            NotifyDetailsChanged();
        }
    }

    /// <summary>执行结果摘要（工具调用，语义化文本）。</summary>
    public string? ResultSummary
    {
        get => _resultSummary;
        set
        {
            _resultSummary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResult)));
            NotifyDetailsChanged();
        }
    }

    /// <summary>是否显示结果摘要。</summary>
    public bool HasResult => !string.IsNullOrWhiteSpace(_resultSummary);

    /// <summary>完整参数（详情展开）。</summary>
    public string? FullArguments
    {
        get => _fullArguments;
        set
        {
            _fullArguments = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullArguments)));
            NotifyDetailsChanged();
        }
    }

    /// <summary>完整结果（详情展开）。</summary>
    public string? FullResult
    {
        get => _fullResult;
        set
        {
            _fullResult = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullResult)));
            NotifyDetailsChanged();
        }
    }

    /// <summary>结构化 diff 行；空集合表示该步骤没有可渲染的 diff。</summary>
    public IReadOnlyList<DiffLineView> DiffLines => _diffLines;

    public string? DiffSummary => _diffSummary;

    public bool HasDiff => _diffLines.Count > 0;

    /// <summary>有结构化 diff 时隐藏原始结果文本，避免颜色 diff 与「已应用…」重复。</summary>
    public bool ShowRawResultDetail => HasResultDetail && !HasDiff;

    /// <summary>应用工具 Details 解析出的结构化 diff（算法与 TUI 共用）。</summary>
    public void ApplyDiff(IReadOnlyList<DiffLineView> lines, string? summary)
    {
        _diffLines = lines;
        _diffSummary = summary;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiffLines)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiffSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDiff)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRawResultDetail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDetails)));
    }

    /// <summary>详情中的参数文本：优先完整参数，历史数据回退摘要。</summary>
    public string? DetailArguments =>
        !string.IsNullOrWhiteSpace(_fullArguments) ? _fullArguments : _argumentsSummary;

    /// <summary>详情中的结果文本：优先完整结果，历史数据回退摘要。</summary>
    public string? DetailResult =>
        !string.IsNullOrWhiteSpace(_fullResult) ? _fullResult : _resultSummary;

    public bool HasDetailArguments => !string.IsNullOrWhiteSpace(DetailArguments);

    public bool HasResultDetail => !string.IsNullOrWhiteSpace(DetailResult);

    /// <summary>
    /// 是否显示「详情 / 展开 / 收起」按钮。
    /// 思考步骤：有思考正文即可折叠（创建时默认展开）；工具步骤：有原始参数/结果/diff 即可展开。
    /// </summary>
    public bool HasDetails =>
        Kind switch
        {
            StepKind.Thinking => HasThinking,
            StepKind.Content => false,
            _ => HasDetailArguments || HasResultDetail || HasDiff,
        };

    /// <summary>详情是否展开；思考步骤创建时默认展开，工具原始 JSON/diff 默认折叠。</summary>
    public bool IsDetailsExpanded
    {
        get => _isDetailsExpanded;
        set
        {
            if (_isDetailsExpanded == value)
                return;
            _isDetailsExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDetailsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsToggleLabel)));
        }
    }

    public string DetailsToggleLabel =>
        IsDetailsExpanded ? "收起"
        : Kind == StepKind.Thinking ? "展开"
        : "详情";

    /// <summary>是否错误。</summary>
    public bool IsError { get; set; }

    /// <summary>思考内容（Thinking 步骤的流式文本）。</summary>
    public System.Text.StringBuilder ThinkingBuilder { get; } = new();

    /// <summary>通知 UI 内容已变化（流式思考文本追加后调用）。</summary>
    public void NotifyThinkingChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThinking)));
        NotifyDetailsChanged();
    }

    /// <summary>思考文本（UI 绑定）。</summary>
    public string ThinkingText => ThinkingBuilder.ToString();

    /// <summary>是否有思考内容（空思考不再占位;对齐 TUI）。</summary>
    public bool HasThinking => ThinkingBuilder.Length > 0;

    private void NotifyDetailsChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailArguments)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailResult)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDetailArguments)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResultDetail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRawResultDetail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDetails)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
