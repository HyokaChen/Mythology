using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// 自定义 PromptEditor，支持斜杠命令补全。
/// 拦截补全弹出窗口中的所有按键事件（包括 Backspace/Delete 和字符输入），
/// 并重新分派到编辑器。
///
/// 关键行为：
/// 1. 输入 "/" 打开补全弹出窗口。弹出窗口打开时，用户可以输入过滤、
///    按 Backspace/Delete 编辑，或使用 Up/Down 导航。所有非导航键关闭弹出窗口，
///    恢复焦点到编辑器，并重新分派以便编辑器自然处理。
/// 2. 选择候选（Enter/Space/Tab/点击）插入候选 + 尾部空格，关闭弹出窗口，
///    保持焦点在编辑器上。
/// 3. 选择后，退格删除字符并在文本再次匹配斜杠命令模式时重新打开弹出窗口。
/// 4. 单候选自动应用被抑制 — 弹出窗口始终打开。
/// </summary>
public sealed class MythPromptEditor : PromptEditor
{
    // ── ESC 中断回调 ─────────────────────────────────────────
    /// <summary>
    /// ESC 按键回调。在 base.OnKeyDown 之前调用。
    /// 返回 true 表示消费事件（中断 Agent），返回 false 表示不消费（让 PromptEditor 正常处理 ESC）。
    /// </summary>
    public Func<bool>? OnEscapePressed;

    // ── Ctrl+C 中断回调 ─────────────────────────────────────────
    /// <summary>
    /// Ctrl+C 按键回调（无选区时触发）。替换 TextEditor.Copy 的默认行为。
    /// 返回 true 表示消费事件（中断 Agent + 清空输入框），返回 false 表示不消费（执行复制）。
    /// </summary>
    public Func<bool>? OnCtrlCPressed;

    // ── 粘贴防重（简单版）── 复制内容含回车时不触发提交 ──
    // 假设：粘贴 = 文本长度突增（>5 字符）；粘贴后的 Enter = 不提交（存输入框，等待手动触发）
    private bool _pasteDetected;
    private int _prevTextLength;

    // ── 构造函数：替换 TextEditor.Copy Command ─────────────
    // TextEditorBase 注册了 TextEditor.Copy (Ctrl+C)，在 Command 路由中优先于
    // _rootVisual 上的 App.Interrupt。通过同 Id 替换，让 Ctrl+C 在无选区时
    // 触发 OnCtrlCPressed 回调（中断 Agent + 清空输入框），有选区时仍执行复制。
    public MythPromptEditor()
    {
        _prevTextLength = (Text ?? "").Length;
        AddCommand(
            new Command
            {
                Id = "TextEditor.Copy", // 同 Id 替换基类的 Copy Command
                LabelMarkup = string.Empty,
                Gesture = new KeyGesture(TerminalChar.CtrlC, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.None,
                // 无选区时：由 OnCtrlCPressed 处理（中断 Agent + 清空输入框）
                // 有选区时：CanExecute=false + ConsumesGestureWhenUnavailable=false，
                //   让 Ctrl+C 穿透到 FocusedElement.RaiseEvent，由 TextEditorCore.OnKeyDown 处理复制
                CanExecute = static v =>
                    !((MythPromptEditor)v).HasSelection
                    && ((MythPromptEditor)v).OnCtrlCPressed is not null,
                ConsumesGestureWhenUnavailable = false,
                Execute = static v =>
                {
                    var editor = (MythPromptEditor)v;
                    if (editor is { HasSelection: false, OnCtrlCPressed: not null })
                    {
                        editor.OnCtrlCPressed();
                    }
                },
            }
        );
    }

    // ── 实体提及补全（由 TuiApp 填充） ─────────────
    /// <summary>
    /// @ 提及补全的实体名称列表。
    /// 由 TuiApp 使用当前小说的实体名称填充。
    /// </summary>
    public List<string> EntityCompletions = [];

    /// <summary>
    /// 从 TuiApp 设置实体补全列表。
    /// </summary>
    /// <param name="names">实体名称列表。</param>
    public void SetEntityCompletions(List<string> names) => EntityCompletions = names;

    // ── 补全状态 ────────────────────────────────────────
    private AnchoredPopup<MythPromptEditor>? _myPopup; // 补全弹出窗口（null = 未打开）
    private string[] _myCandidates = []; // 候选数组
    private int _myReplaceStart; // 替换起始位置
    private int _myReplaceLength; // 替换长度
    private string _trailingOnApply = " "; // 应用时附加的尾部文本

    /// <summary>
    /// 选择应用文本后设置，以便下一帧知道跳过重新触发补全
    /// （否则会自动应用单个候选并撤销用户随后的退格操作）。
    /// </summary>
    private bool _selectionApplied;

    /// <summary>
    /// 由更新循环调用。当为 true 时，更新循环不应为当前文本更改触发补全。
    /// 消耗一次。
    /// </summary>
    /// <returns>是否已消耗选择应用标志。</returns>
    public bool ConsumeSelectionApplied()
    {
        if (_selectionApplied)
        {
            _selectionApplied = false;
            return true;
        }
        return false;
    }

    // ── 输入历史状态 ─────────────────────────────────────────
    private readonly List<string> _history = []; // 历史记录列表
    private int _historyIndex = -1; // -1 = 未导航
    private string? _savedText; // 历史导航开始前的文本

    /// <summary>
    /// 设置用于 Up/Down 导航的输入历史列表。
    /// 列表应按最新到最旧排序（所以 Up 会导航到更旧的条目）。
    /// </summary>
    /// <param name="history">历史记录列表。</param>
    public void SetHistory(List<string> history)
    {
        _history.Clear();
        _history.AddRange(history);
        _historyIndex = -1;
        _savedText = null;
    }

    // ── 更新循环的公共 API ──────────────────────────────

    /// <summary>
    /// 打开（或重新打开）补全弹出窗口。
    /// </summary>
    /// <param name="candidates">候选列表。</param>
    /// <param name="replaceStart">替换起始位置。</param>
    /// <param name="replaceLength">替换长度。</param>
    /// <param name="trailingText">应用时附加的尾部文本。</param>
    public void ShowCompletionPopup(
        string[] candidates,
        int replaceStart,
        int replaceLength,
        string trailingText = " "
    )
    {
        _trailingOnApply = trailingText;
        if (candidates.Length == 0)
        {
            CloseMyPopup();
            return;
        }

        _myCandidates = candidates;
        _myReplaceStart = replaceStart;
        _myReplaceLength = replaceLength;

        // Reuse existing popup — update items and anchor rect
        if (_myPopup is not null)
        {
            _myPopup.List.Items.Clear();
            foreach (var c in candidates)
                _myPopup.List.Items.Add(c);
            _myPopup.List.SelectedIndex = 0;

            // Keep anchor rect in sync with editor width
            var existing = Bounds;
            if (existing.Width > 0)
            {
                _myPopup.UpdateAnchorRect(
                    new Rectangle(existing.X, existing.Y, existing.Width, existing.Height)
                );
            }
            return;
        }

        // Create new popup — 生命周期（Popup/ItemTemplate/SelectedIndex/ItemActivated 先关后派发 /
        // Escape 关闭 + 焦点还原 / Show 后聚焦）收敛到 AnchoredPopup
        // Anchor the popup to the editor's full width (not just the caret
        // cell) so MatchAnchorWidth stretches it to match the input box.
        var editorBounds = Bounds;
        var anchorRect =
            editorBounds.Width > 0
                ? (Rectangle?)
                    new Rectangle(
                        editorBounds.X,
                        editorBounds.Y,
                        editorBounds.Width,
                        editorBounds.Height
                    )
                : null;

        _myPopup = AnchoredPopup<MythPromptEditor>.Open(
            owner: this,
            anchor: this,
            items: candidates,
            onActivated: static (owner, index) => owner.ApplyCandidateAt(index),
            matchAnchorWidth: true,
            placement: PopupPlacement.Above,
            closeOnTab: false, // We handle Tab ourselves
            anchorRect: anchorRect,
            onKeyDown: static (owner, sender, e) => owner.OnListKeyDown(sender, e),
            onSelectionChanged: null,
            onClosed: static owner => owner.OnMyPopupClosed(),
            restoreFocusToAnchor: true
        );

        if (_myPopup is not null)
        {
            // Click selects a candidate
            _myPopup.List.PointerPressed(OnListPointerPressed);
            // Character input while open: close + redispatch to editor
            _myPopup.List.TextInput(OnListTextInput);
        }
    }

    /// <summary>
    /// 关闭补全弹出窗口（如果已打开）。
    /// </summary>
    public void CloseMyPopup()
    {
        _myPopup?.Close(); // triggers OnMyPopupClosed
    }

    /// <summary>补全弹出窗口是否打开。</summary>
    public bool IsMyPopupOpen => _myPopup is not null;

    // ── Key handling in the editor ─────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 简单粘贴检测：内容突增（>20 字）且含换行 = 粘贴含回车（防重保护目标）
        // 输入法确认（突增但无 \n）不激活防重 → 正常 Enter 可提交
        var currentText = Text ?? "";
        var hasLineBreak = currentText.Contains('\n') || currentText.Contains('\r');
        if (Math.Abs(currentText.Length - _prevTextLength) > 20 && hasLineBreak)
            _pasteDetected = true;

        // 粘贴后的 Enter（内容已含回车，已存输入框）：不触发提交，等待手动再触发
        if (e.Key == TerminalKey.Enter && _pasteDetected)
        {
            _pasteDetected = false;
            _prevTextLength = currentText.Length;
            e.Handled = true; // 阻止 base.OnKeyDown（防粘贴含回车触发提交）
            return; // 内容已在输入框中，等待用户手动再提交
        }

        // 正常路径：输入法确认（无换行）或小变化 → 不激活防重，正常处理
        _prevTextLength = currentText.Length;
        _pasteDetected = false;

        // ESC interrupt
        if (e.Key == TerminalKey.Escape && OnEscapePressed is not null)
        {
            if (OnEscapePressed())
            {
                e.Handled = true;
                return;
            }
        }

        // Tab completion
        if (_myPopup is null && e.Key == TerminalKey.Tab)
        {
            var text = Text ?? "";
            if (text.StartsWith('/') && !text.Contains(' '))
            {
                _tabRequestedCompletion = true;
                e.Handled = true;
                return;
            }
        }

        // Up/Down history navigation
        if (_myPopup is null)
        {
            var text = Text ?? "";
            switch (e.Key)
            {
                // 多行文本且光标不在首行：先把光标上移到首行（交给基类做行内移动），
                // 光标已到首行（或单行文本）才触发上一条历史记录导航
                case TerminalKey.Up when CountLineBreaksBefore(text, CaretIndex) > 0:
                    base.OnKeyDown(e);
                    return;
                case TerminalKey.Up when _history.Count > 0:
                    switch (_historyIndex)
                    {
                        case < 0:
                            _savedText = Text ?? "";
                            _historyIndex = _history.Count - 1;
                            break;
                        case > 0:
                            _historyIndex--;
                            break;
                    }
                    Text = _history[_historyIndex];
                    CaretIndex = Text.Length;
                    e.Handled = true;
                    return;
                // 多行文本且光标不在末行：先把光标下移到末行（交给基类做行内移动），
                // 光标已到末行才触发下一条历史记录导航
                case TerminalKey.Down
                    when CountLineBreaksBefore(text, CaretIndex)
                        < CountLineBreaksBefore(text, text.Length):
                    base.OnKeyDown(e);
                    return;
                case TerminalKey.Down when _historyIndex >= 0:
                {
                    if (_historyIndex < _history.Count - 1)
                    {
                        _historyIndex++;
                        Text = _history[_historyIndex];
                    }
                    else
                    {
                        _historyIndex = -1;
                        Text = _savedText ?? "";
                    }
                    CaretIndex = Text.Length;
                    e.Handled = true;
                    return;
                }
            }
        }

        if (e.Key != TerminalKey.Up && e.Key != TerminalKey.Down)
        {
            if (_historyIndex >= 0)
            {
                _historyIndex = -1;
                _savedText = null;
            }
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// 统计文本中 index 之前的换行数（逻辑换行 = '\n'，或不属于 '\r\n' 的 '\r'）。
    /// 用于 Up/Down 在"行内光标移动"与"历史导航"之间的分界：
    /// 光标所在行 = CountLineBreaksBefore(text, CaretIndex)；
    /// 总行数 = CountLineBreaksBefore(text, text.Length) + 1。
    /// </summary>
    internal static int CountLineBreaksBefore(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index <= 0)
            return 0;

        var limit = Math.Min(index, text.Length);
        var count = 0;
        for (var i = 0; i < limit; i++)
        {
            var c = text[i];
            if (c == '\n' || c == '\r' && (i + 1 >= limit || text[i + 1] != '\n'))
                count++;
        }
        return count;
    }

    /// <summary>
    /// 用户在文本匹配斜杠命令模式时按 Tab 设置。
    /// 由更新循环消耗以触发补全。
    /// </summary>
    private bool _tabRequestedCompletion;

    /// <summary>消耗 Tab 请求补全标志（一次性）。</summary>
    public bool ConsumeTabRequestedCompletion()
    {
        if (_tabRequestedCompletion)
        {
            _tabRequestedCompletion = false;
            return true;
        }
        return false;
    }

    // ── OptionList 中的按键处理 ──────────────────────────────

    /// <summary>
    /// 补全弹出窗口中的按键处理。
    /// Enter/Space/Tab = 选择候选；Escape = 关闭弹出窗口；
    /// 导航键由 OptionList 处理；其他键关闭弹出窗口并重新分派到编辑器。
    /// </summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        // 选择键和 Escape：自行处理
        switch (e.Key)
        {
            case TerminalKey.Enter:
            case TerminalKey.Space:
            case TerminalKey.Tab:
                ApplySelectedCandidate();
                e.Handled = true;
                return;

            case TerminalKey.Escape:
                CloseMyPopup();
                // 焦点回到编辑器
                App?.Focus(this);
                e.Handled = true;
                return;
        }

        // 导航键（Up/Down/Home/End/PageUp/PageDown）已由 OptionList.OnKeyDown 处理。
        // 若 e.Handled=true，OptionList 已处理——不做任何事。
        if (e.Handled)
            return;

        // 其他键（Backspace/Delete/字母等）OptionList 不处理：
        // 关闭弹出窗口，恢复焦点到编辑器，重新分派让编辑器处理。
        CloseMyPopup();

        var app = App;
        app?.Focus(this);

        var redispatchArgs = new KeyEventArgs { RawEvent = e.RawEvent };
        RaiseEvent(KeyDownEvent, redispatchArgs);

        e.Handled = true;
    }

    /// <summary>
    /// 补全弹出窗口中的文本输入处理 — 关闭弹出窗口，恢复焦点，重新分派文本输入到编辑器。
    /// </summary>
    private void OnListTextInput(object? sender, TextInputEventArgs e)
    {
        // 弹出窗口打开时的字符输入：关闭弹出窗口，恢复焦点，重新分派

        var app = App;
        app?.Focus(this);

        var redispatchArgs = new TextInputEventArgs { Text = e.Text };
        RaiseEvent(TextInputEvent, redispatchArgs);

        e.Handled = true;
    }

    /// <summary>应用选中的补全候选 — 替换斜杠命令部分并附加尾部文本。</summary>
    private void ApplySelectedCandidate()
    {
        if (_myPopup is null || _myCandidates.Length == 0)
            return;

        ApplyCandidateAt(_myPopup.List.SelectedIndex);
    }

    /// <summary>按索引应用候选（AnchoredPopup.ItemActivated 先关面板后派发，因此此处面板已关）。</summary>
    internal void ApplyCandidateAt(int idx)
    {
        if (idx < 0 || idx >= _myCandidates.Length)
            return;

        var candidate = _myCandidates[idx];
        var text = Text ?? "";
        var start = Math.Clamp(_myReplaceStart, 0, text.Length);
        var length = Math.Clamp(_myReplaceLength, 0, text.Length - start);
        // 替换斜杠命令部分并附加尾部空格
        var before = text[..start];
        var after = text[(start + length)..];
        var newText = before + candidate + _trailingOnApply + after;

        Text = newText;
        CaretIndex = start + candidate.Length + _trailingOnApply.Length;

        var wasHashStage1 = _trailingOnApply != " ";
        _trailingOnApply = " ";

        _selectionApplied = !wasHashStage1;
        // 焦点回到编辑器
        App?.Focus(this);
    }

    // ── 弹出窗口事件处理 ────────────────────────────────────────

    /// <summary>列表点击处理 — 点击已通过 ActivateOnClick 改变 SelectedIndex，直接应用候选。</summary>
    private void OnListPointerPressed(object? sender, PointerEventArgs e)
    {
        if (e.Button != TerminalMouseButton.Left)
            return;

        // The click already changed SelectedIndex via ActivateOnClick.
        // Apply the now-selected candidate.
        ApplySelectedCandidate();
        e.Handled = true;
    }

    /// <summary>弹出窗口关闭时清理补全状态（AnchoredPopup.onClosed 回调）。</summary>
    private void OnMyPopupClosed()
    {
        _myPopup = null;
        _myCandidates = [];
        _myReplaceStart = 0;
        _myReplaceLength = 0;
    }
}
