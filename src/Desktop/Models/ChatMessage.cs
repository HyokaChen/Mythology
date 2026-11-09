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
    private bool _isRichMarkdown = true;
    private bool _isProcessExpanded = true;

    /// <summary>消息 ID（Ulid）。</summary>
    public string Id { get; } = Ulid.NewUlid().ToString();

    /// <summary>是否等待 Agent 产出首个正文/步骤（对话内等待行）。</summary>
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

    /// <summary>
    /// 是否用 LiveMarkdown 渲染正文。历史预取页先置 false（轻量纯文本），
    /// 滚动停止后只对当前可见消息升级，避免把 Markdown 布局压进滚轮帧。
    /// </summary>
    public bool IsRichMarkdown
    {
        get => _isRichMarkdown;
        set
        {
            if (_isRichMarkdown == value)
                return;
            _isRichMarkdown = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRichMarkdown)));
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
