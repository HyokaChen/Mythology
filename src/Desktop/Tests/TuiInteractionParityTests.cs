using System.Xml.Linq;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 对话 / 状态栏交互深度回归锁（TUI 对齐）。
///
/// 这些约定都不会让构建失败、只会让交互悄悄退化：
/// 1. **加载态动画**：对话加载行 / Agent 步骤行 / 状态栏必须使用 `BusySpinner`
///    （Braille 帧与 XenoAtom `SpinnerStyles.Dots` 同源、10 帧 @80ms）；旧 `ProgressBar` 不得回潮。
/// 2. **系统提示分支**：`MessageSender.System` 消息必须居中细注渲染（不得伪装为左右气泡）；
///    空思考文本不得占位（`HasThinking`）。
/// 3. **状态栏**：运行期诗词（`StatusTooltip` 全句补全）+ 各段可点击（会话 / 云端 / 上下文 / 模型 / 工具）。
/// 4. **补全弹窗**：鼠标点击条目直接插入（`PointerReleased` 接线）+ 键盘导航滚动保持可见。
/// 5. **压缩态**：`CompactStateChanged` 事件贯通 VM → 主窗（手动 `/compact` 期间状态栏同转）。
/// </summary>
public sealed class TuiInteractionParityTests
{
    private static string Find(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未找到 {Path.Combine(segments)}");
    }

    private static XElement Load(params string[] segments) => XDocument.Load(Find(segments)).Root!;

    private static string Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    private static List<XElement> Descendants(XElement root, string localName) =>
        [.. root.Descendants().Where(e => e.Name.LocalName == localName)];

    // ── ConversationView ─────────────────────────────────────────────

    [Fact]
    public void ConversationView_LoadingAndStepRows_UseBusySpinner()
    {
        var root = Load("src", "Desktop", "Views", "ConversationView.axaml");
        var spinners = Descendants(root, "BusySpinner");

        Assert.True(spinners.Count >= 2, "加载行与 Agent 步骤行均应出现 BusySpinner");
        Assert.Contains(spinners, s => Attr(s, "IsActive").Contains("{Binding Loading}"));
        Assert.Contains(spinners, s => Attr(s, "IsActive").Contains("{Binding IsProcessing}"));
        // 旧加载条已移除（TUI 为纯字符 dots,不再有进度条）
        Assert.Empty(Descendants(root, "ProgressBar"));
    }

    [Fact]
    public void ConversationView_StepRow_FallsBackToStaticGlyphWhenDone()
    {
        var root = Load("src", "Desktop", "Views", "ConversationView.axaml");
        // 静态回退图标 = LucideIcon（Kind 绑定 StatusIcon），不再是 emoji 文本
        var icon = Descendants(root, "LucideIcon")
            .Single(t => Attr(t, "Kind") == "{Binding StatusIcon}");

        Assert.Contains("IsProcessing", Attr(icon, "IsVisible"));
        Assert.Contains("InvertedBoolToVisibility", Attr(icon, "IsVisible"));
    }

    [Fact]
    public void ConversationView_SystemNotice_IsCenteredNote()
    {
        var root = Load("src", "Desktop", "Views", "ConversationView.axaml");
        var eyebrow = Descendants(root, "TextBlock").Single(t => Attr(t, "Text") == "系统");
        var branch = eyebrow.Parent!;

        Assert.Equal("StackPanel", branch.Name.LocalName);
        Assert.Equal("Center", Attr(branch, "HorizontalAlignment"));
        Assert.Contains("IsSystem", Attr(branch, "IsVisible"));
        Assert.Contains("ConverterParameter=System", Attr(branch, "IsVisible"));
    }

    [Fact]
    public void ConversationView_Hints_MentionSlashCommands()
    {
        var root = Load("src", "Desktop", "Views", "ConversationView.axaml");
        var hints = Descendants(root, "TextBlock").Select(t => Attr(t, "Text")).ToList();

        Assert.Contains(hints, t => t.Contains("/ 唤起命令"));
        Assert.Contains(hints, t => t.Contains("/ 命令补全"));
    }

    [Fact]
    public void ConversationView_CodeBehind_WiresClickInsertAndScrollIntoView()
    {
        var src = File.ReadAllText(Find("src", "Desktop", "Views", "ConversationView.axaml.cs"));

        Assert.Contains(
            "SuggestionList.PointerReleased += SuggestionList_OnPointerReleased;",
            src,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "CommandList.PointerReleased += CommandList_OnPointerReleased;",
            src,
            StringComparison.Ordinal
        );
        Assert.Contains("list.ScrollIntoView(index);", src, StringComparison.Ordinal);
    }

    // ── MainWindow 状态栏 ───────────────────────────────────────────

    [Fact]
    public void MainWindow_StatusSpinner_FollowsIsBusy()
    {
        var root = Load("src", "Desktop", "Views", "MainWindow.axaml");
        var spinner = Descendants(root, "BusySpinner")
            .Single(s => Attr(s, "Name") == "StatusSpinner");

        Assert.Equal("{Binding IsBusy}", Attr(spinner, "IsActive"));
        Assert.Equal("{Binding IsBusy}", Attr(spinner, "IsVisible"));
    }

    [Fact]
    public void MainWindow_StatusText_HasTooltipAndTrimming()
    {
        var root = Load("src", "Desktop", "Views", "MainWindow.axaml");
        var statusText = Descendants(root, "TextBlock")
            .Single(t => Attr(t, "Text") == "{Binding StatusText}");

        Assert.Equal("{Binding StatusTooltip}", Attr(statusText, "ToolTip.Tip"));
        Assert.Equal("CharacterEllipsis", Attr(statusText, "TextTrimming"));
        Assert.Equal("440", Attr(statusText, "MaxWidth"));
    }

    [Fact]
    public void MainWindow_StatusSegments_AreClickable()
    {
        var root = Load("src", "Desktop", "Views", "MainWindow.axaml");
        var clicks = Descendants(root, "Button").Select(b => Attr(b, "Click")).ToList();

        Assert.Contains("SessionButton_OnClick", clicks);
        Assert.Contains("CloudButton_OnClick", clicks);
        Assert.Contains("StatusContext_OnClick", clicks);
        Assert.Contains("ModelButton_OnClick", clicks);
        Assert.Contains("StatusTools_OnClick", clicks);
    }

    [Fact]
    public void MainWindow_CodeBehind_DefinesStatusBarHandlers()
    {
        var src = File.ReadAllText(Find("src", "Desktop", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("private void StatusContext_OnClick", src, StringComparison.Ordinal);
        Assert.Contains("private void StatusTools_OnClick", src, StringComparison.Ordinal);
        Assert.Contains("TryExecuteCommand(\"/state\")", src, StringComparison.Ordinal);
        Assert.Contains("OpenSettings(initialTab: 4)", src, StringComparison.Ordinal);
    }

    // ── ViewModel / 数据层 ──────────────────────────────────────────

    [Fact]
    public void MainWindowViewModel_ExposesBusyAndPoemStatus()
    {
        var src = File.ReadAllText(Find("src", "Desktop", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("IsBusy => IsProcessing || _isCompacting", src, StringComparison.Ordinal);
        Assert.Contains("public string? StatusTooltip", src, StringComparison.Ordinal);
        Assert.Contains("Core.Data.PoetryStore.Lines", src, StringComparison.Ordinal);
        Assert.Contains("vm.CompactStateChanged +=", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversationViewModel_ExposesCompactState()
    {
        var src = File.ReadAllText(
            Find("src", "Desktop", "ViewModels", "ConversationViewModel.cs")
        );

        Assert.Contains(
            "public event Action<bool>? CompactStateChanged;",
            src,
            StringComparison.Ordinal
        );
        Assert.Contains("CompactStateChanged?.Invoke(true);", src, StringComparison.Ordinal);
        Assert.Contains("CompactStateChanged?.Invoke(false);", src, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentStep_TracksProcessingAndThinking()
    {
        var src = File.ReadAllText(Find("src", "Desktop", "Models", "AgentStep.cs"));

        Assert.Contains(
            "public bool IsProcessing => Status == AgentStepStatus.Processing;",
            src,
            StringComparison.Ordinal
        );
        Assert.Contains("nameof(IsProcessing)", src, StringComparison.Ordinal);
        Assert.Contains(
            "public bool HasThinking => ThinkingBuilder.Length > 0;",
            src,
            StringComparison.Ordinal
        );
        Assert.Contains("nameof(HasThinking)", src, StringComparison.Ordinal);
    }

    [Fact]
    public void BusySpinner_MatchesTuiDotsFrames()
    {
        var src = File.ReadAllText(Find("src", "Desktop", "Controls", "BusySpinner.cs"));

        Assert.Contains(
            "[\"⠋\", \"⠙\", \"⠹\", \"⠸\", \"⠼\", \"⠴\", \"⠦\", \"⠧\", \"⠇\", \"⠏\"]",
            src,
            StringComparison.Ordinal
        );
        Assert.Contains("FrameIntervalMs = 80", src, StringComparison.Ordinal);
    }

    [Fact]
    public void PoetryStore_LivesInCore_SharedWithTui()
    {
        var store = File.ReadAllText(Find("src", "Core", "Data", "PoetryStore.cs"));
        Assert.Contains("namespace Core.Data;", store, StringComparison.Ordinal);

        var tuiStatusBar = File.ReadAllText(Find("src", "Tui", "Controls", "StatusBar.cs"));
        Assert.Contains("using Core.Data;", tuiStatusBar, StringComparison.Ordinal);
        Assert.Contains("PoetryStore", tuiStatusBar, StringComparison.Ordinal);
    }
}
