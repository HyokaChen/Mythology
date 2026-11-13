using System.Collections.ObjectModel;
using System.ComponentModel;
using LiveMarkdown.Avalonia;

namespace Desktop.Models;

/// <summary>
/// 对话消息。流式内容使用 ObservableStringBuilder（LiveMarkdown 增量渲染），
/// 必须在 UI 线程上追加。
/// </summary>
public sealed class ChatMessage(MessageSender sender) : INotifyPropertyChanged
{
    private bool _isWaiting;
    private bool _hasVisibleContent;
    private bool _isInterrupted;
    private bool _isError;
    private bool _isProcessExpanded = true;
    private bool _isSpinnerVisible;
    private bool _isCompleted = sender != MessageSender.Agent;
    private bool _isEditing;
    private string _editDraft = "";
    private bool _isPointerOverMessage;

    /// <summary>消息 ID（Ulid）。</summary>
    public string Id { get; } = Ulid.NewUlid().ToString();

    /// <summary>持久化 entryId（会话恢复后可用于精确定位 retry/edit；实时新消息为空）。</summary>
    public string? EntryId { get; init; }

    /// <summary>本回合是否已经结束（正常结束、中断或错误收尾）。结束前不显示「重试」。</summary>
    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (_isCompleted == value)
                return;
            _isCompleted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompleted)));
        }
    }

    /// <summary>用户消息是否进入内联编辑态（气泡原位变成输入框）。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
                return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    /// <summary>内联编辑草稿；进入编辑态时用原正文填充，取消后不覆盖原消息。</summary>
    public string EditDraft
    {
        get => _editDraft;
        set
        {
            if (_editDraft == value)
                return;
            _editDraft = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditDraft)));
        }
    }

    /// <summary>
    /// 指针是否落在该消息的任意一行上。动作条用消息级 hover 而不是单行 hover：
    /// 这样没有正文的中断/步骤回合也能从任意可视行唤出复制/重试。
    /// </summary>
    public bool IsPointerOverMessage
    {
        get => _isPointerOverMessage;
        set
        {
            if (_isPointerOverMessage == value)
                return;
            _isPointerOverMessage = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsPointerOverMessage))
            );
        }
    }

    /// <summary>
    /// 回合仍在执行、但当前没有可见活动（等首个正文/步骤，或阶段之间等下一段思考/工具）。
    /// 阶段结束必须立即置回 true，否则对话看起来像已经结束。
    /// </summary>
    public bool IsWaiting
    {
        get => _isWaiting;
        set
        {
            if (_isWaiting == value)
                return;
            _isWaiting = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWaiting)));
        }
    }

    /// <summary>
    /// 系统状态行是否带旋转加载指示（如上下文压缩中）。
    /// 与 <see cref="IsWaiting"/> 分离：等待行属于 Agent 回合，系统状态行只借用同一个 BusySpinner。
    /// </summary>
    public bool IsSpinnerVisible
    {
        get => _isSpinnerVisible;
        set
        {
            if (_isSpinnerVisible == value)
                return;
            _isSpinnerVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpinnerVisible)));
        }
    }

    /// <summary>本轮是否被用户/系统中断。中断提示独立成行，不再塞进 Markdown 引用块。</summary>
    public bool IsInterrupted
    {
        get => _isInterrupted;
        set
        {
            if (_isInterrupted == value)
                return;
            _isInterrupted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInterrupted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
        }
    }

    /// <summary>本条系统通知是否为错误（用于警告卡片；普通状态行保持 dim）。</summary>
    public bool IsError
    {
        get => _isError;
        set
        {
            if (_isError == value)
                return;
            _isError = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsError)));
        }
    }

    /// <summary>
    /// 是否展开历史过程（步骤行）。历史预取页默认折叠成一行「过程 · N 步」；
    /// 点击后展开。当前 Turn 保持展开。
    /// </summary>
    public bool IsProcessExpanded
    {
        get => _isProcessExpanded;
        set
        {
            if (_isProcessExpanded == value)
                return;
            _isProcessExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProcessExpanded)));
        }
    }

    /// <summary>用户手动展开/收起过程后，禁止自动折叠（对齐 ToolCallGroup 的 touched 语义）。</summary>
    public bool ProcessTouchedByUser { get; set; }

    /// <summary>折叠过程行的摘要。</summary>
    public string ProcessSummary
    {
        get
        {
            if (AgentSteps.Count == 0)
                return "";
            var titles = AgentSteps.Take(3).Select(step => step.Title).Where(t => t.Length > 0);
            var head = string.Join(" · ", titles);
            var suffix =
                AgentSteps.Count > 3 ? $" 等 {AgentSteps.Count} 步" : $" · {AgentSteps.Count} 步";
            return head + suffix;
        }
    }

    /// <summary>发送者。</summary>
    public MessageSender Sender { get; init; } = sender;

    /// <summary>消息内容（可增量追加，LiveMarkdown 流式渲染）。</summary>
    public ObservableStringBuilder Content { get; } = new();

    /// <summary>
    /// 是否已有可见正文（至少一个非空白字符）。
    /// 流式开始时常先收到换行/空格；仅按 Content.Length 判断会渲染出空白气泡卡。
    /// </summary>
    public bool HasVisibleContent => _hasVisibleContent;

    /// <summary>追加上下文并更新可见正文状态；空白片段不触发气泡显示。</summary>
    public void AppendContent(string text)
    {
        Content.Append(text);
        if (_hasVisibleContent)
            return;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
                continue;
            _hasVisibleContent = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasVisibleContent)));
            return;
        }
    }

    /// <summary>清空当前正文（中间正文冻结为独立步骤后调用）。</summary>
    public void ResetContent()
    {
        Content.Clear();
        if (!_hasVisibleContent)
            return;

        _hasVisibleContent = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasVisibleContent)));
    }

    /// <summary>Agent 步骤（思考/工具调用）。</summary>
    public ObservableCollection<AgentStep> AgentSteps { get; } = [];

    /// <summary>信息来源（结束时提交显示）。</summary>
    public ObservableCollection<SourceInfo> Sources { get; } = [];

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    /// <summary>是否包含任何有效内容（空白正文不算）。</summary>
    public bool IsEmpty =>
        !HasVisibleContent && !IsInterrupted && AgentSteps.Count == 0 && Sources.Count == 0;

    public event PropertyChangedEventHandler? PropertyChanged;
}
