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
    private string _reasoningPreview = "";

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ShowStreamingPreview))
            );
        }
    } = AgentStepStatus.Processing;

    /// <summary>是否进行中（驱动步骤行 spinner；对齐 TUI dots 动画）。</summary>
    public bool IsProcessing => Status == AgentStepStatus.Processing;

    /// <summary>是否为阶段正文步骤（无步骤头，轻量文本展示时间线）。</summary>
    public bool IsContentStep => Kind == StepKind.Content;

    /// <summary>是否为实体变更提示步骤（轻量系统行，穿插在工具步骤之间）。</summary>
    public bool IsNoticeStep => Kind == StepKind.Notice;

    /// <summary>是否为回合内系统事件（错误/中断/系统提醒），随回合动作条一起收尾。</summary>
    public bool IsSystemEvent => Kind == StepKind.SystemEvent;

    /// <summary>是否为带步骤头的步骤（思考/工具/子步骤）。</summary>
    public bool IsStepHeader => Kind is StepKind.Thinking or StepKind.ToolCall or StepKind.SubStep;

    /// <summary>创建一条已完成的历史/中间正文步骤。</summary>
    public static AgentStep CreateContent(string text) =>
        new()
        {
            Kind = StepKind.Content,
            Status = AgentStepStatus.Completed,
            ContentText = text,
        };

    /// <summary>创建一条实体变更提示步骤（按事件发生顺序插入当前 Agent 步骤）。</summary>
    public static AgentStep CreateNotice(string text) =>
        new()
        {
            Kind = StepKind.Notice,
            Status = AgentStepStatus.Completed,
            ContentText = text,
        };

    /// <summary>创建一条回合内系统事件步骤（错误/中断/系统提醒）。</summary>
    public static AgentStep CreateSystemEvent(string text, bool isError = false) =>
        new()
        {
            Kind = StepKind.SystemEvent,
            Status = AgentStepStatus.Completed,
            IsError = isError,
            ContentText = text,
        };

    /// <summary>
    /// 阶段正文 / 实体变更提示。阶段正文用于工具或下一轮思考开始前冻结当前流式正文，
    /// 保证时间线是「思考 → 正文 → 工具 → 思考 → 最终正文」；提示步骤则把
    /// 「📝 修改情节线…」插在触发它的工具步骤之后，而不是堆到回合末尾。
    /// </summary>
    private string _contentText = "";

    public string ContentText
    {
        get => _contentText;
        init
        {
            _contentText = value;
            ContentMarkdown.Append(value);
        }
    }

    /// <summary>阶段正文的 Markdown 源（冻结后不再变化；供 LiveMarkdown 渲染）。</summary>
    public LiveMarkdown.Avalonia.ObservableStringBuilder ContentMarkdown { get; } = new();

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderText)));
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderText)));
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsView)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsToggleLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ShowStreamingPreview))
            );
        }
    }

    public string DetailsToggleLabel =>
        IsDetailsExpanded ? "收起"
        : Kind == StepKind.Thinking ? "展开"
        : "详情";

    /// <summary>
    /// 单行步骤头：状态字形 + 标题 + 参数摘要 + 详情开关。用单个字符串
    /// 交给一个 TextBlock，避免每行多个 TextBlock/Run 的文本布局开销。
    /// </summary>
    public string HeaderText
    {
        get
        {
            var glyph = Status switch
            {
                AgentStepStatus.Completed => "✓ ",
                AgentStepStatus.Failed => "✕ ",
                _ => "",
            };
            var args = string.IsNullOrWhiteSpace(_argumentsSummary) ? "" : "  " + _argumentsSummary;
            var toggle = HasDetails ? "  " + DetailsToggleLabel : "";
            return glyph + Title + args + toggle;
        }
    }

    /// <summary>
    /// 展开时才非 null，供详情模板懒创建。收起状态没有内容，避免每条步骤行
    /// 预先实例化多个 ScrollViewer/SelectableTextBlock，换窗时拖慢滚动。
    /// </summary>
    public AgentStep? DetailsView => IsDetailsExpanded ? this : null;

    /// <summary>是否错误。</summary>
    public bool IsError { get; set; }

    /// <summary>思考内容（Thinking 步骤的流式文本）。</summary>
    public System.Text.StringBuilder ThinkingBuilder { get; } = new();

    /// <summary>通知 UI 内容已变化（流式思考文本追加后调用）。</summary>
    public void NotifyThinkingChanged()
    {
        _reasoningPreview = ReasoningPreviewTail(ThinkingBuilder.ToString());
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReasoningPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowStreamingPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThinking)));
        NotifyDetailsChanged();
    }

    /// <summary>
    /// 流式思考预览：只取最新约 220 字符，空白折成单空格。行高固定为两行，
    /// 模型持续输出时 TextBlock 只换文字、不改变布局高度（借鉴 TKCode.Arc ReasoningBlock）。
    /// </summary>
    public string ReasoningPreview => _reasoningPreview;

    /// <summary>处理中且未展开详情时，显示固定高度的最新推理预览。</summary>
    public bool ShowStreamingPreview =>
        Kind == StepKind.Thinking && IsProcessing && !IsDetailsExpanded && HasThinking;

    private static string ReasoningPreviewTail(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var folded = new System.Text.StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space && folded.Length > 0)
                {
                    folded.Append(' ');
                    space = true;
                }
                continue;
            }
            folded.Append(c);
            space = false;
        }
        const int max = 220;
        if (folded.Length <= max)
            return folded.ToString();
        var whole = folded.ToString();
        var start = whole.Length - max;
        var gap = whole.IndexOf(' ', start);
        return "…" + (gap < 0 ? whole[start..] : whole[(gap + 1)..]);
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderText)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
