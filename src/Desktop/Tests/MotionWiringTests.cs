using System.Xml.Linq;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 动效层 XAML 结构回归锁（<c>Desktop.Styles.Motion</c> 挂载点）。
///
/// 附加属性挂载漏掉**不会编译失败**，只会静默失去动画，故用结构锁钉死。
/// 挂载点按「新建视觉树 / 反复显隐 / 复用实例」三种情形分派：
///   - 消息行 / 步骤行 / 待办项 / 大纲面板（每次新建）→ <c>Motion.Enter</c>
///   - 空状态 / 吊销横幅 / 更新遮罩 / 行内新建条（反复显隐）→ <c>Motion.Enter</c>（含 EnterRise 方向）
///   - 补全弹窗（复用同一 Popup）→ <c>Motion.PopupEnter</c>
/// 运行期断言在进程内无头 Avalonia 下不稳定，故只锁结构 + 真机冒烟覆盖运行期。
/// </summary>
public sealed class MotionWiringTests
{
    private const string MotionNamespace = "clr-namespace:Desktop.Styles";
    private const string AvaloniaNamespace = "https://github.com/avaloniaui";

    private static XElement Load(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Desktop", relativePath);
            if (File.Exists(candidate))
                return XDocument.Load(candidate).Root!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未找到 src/Desktop/{relativePath}");
    }

    private static string Attr(XElement element, string localName) =>
        element
            .Attributes()
            .FirstOrDefault(a =>
                a.Name.NamespaceName == MotionNamespace && a.Name.LocalName == localName
            )
            ?.Value
        ?? "";

    private static XElement Single(XElement root, Func<XElement, bool> predicate) =>
        root.Descendants().Single(predicate);

    private static XElement FirstTemplateChild(XElement itemTemplate)
    {
        var template = itemTemplate
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "DataTemplate");
        return (template ?? itemTemplate).Elements().First();
    }

    private static XElement ItemTemplateOf(XElement root, string ownerLocalName)
    {
        var owner = Single(root, e => e.Name.LocalName == $"{ownerLocalName}.ItemTemplate");
        return FirstTemplateChild(owner);
    }

    [Theory]
    [InlineData(0, 1, 1d)]
    [InlineData(3, 1, -1d)]
    [InlineData(2, 2, 1d)]
    public void PageEnterOffset_FollowsTabDirection(int fromIndex, int toIndex, double sign)
    {
        // 位移量是 token（PageShift），这里只钉方向：前进向右、后退向左、原地也算前进
        Assert.Equal(
            sign * Desktop.Styles.Motion.PageShift,
            Desktop.Styles.Motion.PageEnterOffset(fromIndex, toIndex)
        );
    }

    [Fact]
    public void ConversationMessageRow_PlaysEnter()
    {
        var template = Single(
            Load("Views/ConversationView.axaml"),
            e => e.Name.LocalName == "AutoScrollItemsControl.ItemTemplate"
        );
        var row = FirstTemplateChild(template);

        Assert.Equal("True", Attr(row, "Motion.Enter"));
    }

    [Fact]
    public void ConversationEmptyState_PlaysEnter()
    {
        var root = Load("Views/ConversationView.axaml");
        var anchor = Single(root, e => AttrPlain(e, "Text") == "开始创作");
        var panel = anchor.Ancestors().First(a => a.Name.LocalName == "StackPanel");

        Assert.Equal("True", Attr(panel, "Motion.Enter"));
    }

    [Fact]
    public void ConversationAgentStepRow_PlaysEnterWithSmallRise()
    {
        var stepTemplate = Single(
            Load("Views/ConversationView.axaml"),
            e =>
                e.Name.LocalName == "DataTemplate"
                && e.Attributes()
                    .Any(a => a.Name.LocalName == "DataType" && a.Value.Contains("AgentStep"))
        );
        var row = stepTemplate.Elements().First();

        Assert.Equal("True", Attr(row, "Motion.Enter"));
        Assert.Equal("4", Attr(row, "Motion.EnterRise"));
    }

    [Fact]
    public void ConversationSourceChip_PlaysEnterWithSmallRise()
    {
        var root = Load("Views/ConversationView.axaml");
        var sourceTemplate = Single(
            root,
            e =>
                e.Name.LocalName == "DataTemplate"
                && e.Attributes()
                    .Any(a => a.Name.LocalName == "DataType" && a.Value.Contains("SourceInfo"))
        );
        var chip = FirstTemplateChild(sourceTemplate);

        Assert.Equal("True", Attr(chip, "Motion.Enter"));
        Assert.Equal("4", Attr(chip, "Motion.EnterRise"));
    }

    [Theory]
    [InlineData("SuggestionPopup")]
    [InlineData("CommandPopup")]
    public void ConversationPopups_PlayEnterOnOpen(string popupName)
    {
        var root = Load("Views/ConversationView.axaml");
        var popup = Single(
            root,
            e =>
                e.Name.LocalName == "Popup"
                && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == popupName)
        );

        Assert.Equal("True", Attr(popup, "Motion.PopupEnter"));
    }

    [Fact]
    public void SetUpPanelTabPane_DoesNotAttachEnterAnimation()
    {
        // 翻页由 SetUpPanelView.AnimateTabChange 统一负责；模板再挂 Enter 会让 Opacity 双动画打架
        var owner = Single(
            Load("Views/SetUpPanelView.axaml"),
            e => e.Name.LocalName == "TabControl.ContentTemplate"
        );
        var template = owner.Elements().First(e => e.Name.LocalName == "DataTemplate");
        var pane = template.Elements().First();
        Assert.NotEqual("True", Attr(pane, "Motion.Enter"));
    }

    [Fact]
    public void TodoPanelItem_PlaysEnterWithSmallRise()
    {
        var itemTemplate = Single(
            Load("Views/TodoPanelView.axaml"),
            e =>
                e.Name.LocalName == "DataTemplate"
                && e.Attributes()
                    .Any(a => a.Name.LocalName == "DataType" && a.Value.Contains("TodoItemRow"))
        );
        var item = itemTemplate.Elements().First();

        Assert.Equal("True", Attr(item, "Motion.Enter"));
        Assert.Equal("4", Attr(item, "Motion.EnterRise"));
    }

    [Fact]
    public void WorldRulePanelRoot_PlaysEnter()
    {
        Assert.Equal(
            "True",
            Attr(Load("Controls/WorldRulePanel.axaml").Elements().First(), "Motion.Enter")
        );
    }

    [Fact]
    public void MainWindowBanner_SlidesDownFromAbove()
    {
        var root = Load("Views/MainWindow.axaml");
        var banner = Single(
            root,
            e =>
                e.Name.LocalName == "Border"
                && e.Attributes()
                    .Any(a =>
                        a.Name.LocalName == "IsVisible"
                        && a.Value.Contains("IsCloudInvalidBannerVisible")
                    )
        );

        Assert.Equal("True", Attr(banner, "Motion.Enter"));
        Assert.Equal("-8", Attr(banner, "Motion.EnterRise"));
    }

    [Fact]
    public void UpdatePromptOverlay_FadesWithoutRise()
    {
        var root = Load("Controls/UpdatePromptDialog.axaml");
        var panel = Single(
            root,
            e =>
                e.Name.LocalName == "Panel"
                && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RootPanel")
        );

        Assert.Equal("True", Attr(panel, "Motion.Enter"));
        Assert.Equal("0", Attr(panel, "Motion.EnterRise"));
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("Button /template/ ContentPresenter")]
    [InlineData("ListBoxItem")]
    [InlineData("ComboBoxItem /template/ ContentPresenter")]
    [InlineData("MenuItem /template/ Border#root")]
    [InlineData("TabItem /template/ ContentPresenter")]
    [InlineData("TreeViewItem /template/ ContentPresenter")]
    [InlineData("TreeViewItem /template/ Border#SelectionBorder")]
    [InlineData("Border.OutlineRow")]
    [InlineData("ScrollBar Thumb")]
    [InlineData("ProgressBar")]
    public void GlobalStyles_DeclareTransitionsForKnownStateChanges(string selector)
    {
        var styles = Load("Styles/MythologyStyles.axaml");
        var match = styles
            .Elements()
            .FirstOrDefault(e =>
                e.Name.LocalName == "Style"
                && AttrPlain(e, "Selector") == selector
                && e.Descendants().Any(d => d.Name.LocalName == "Transitions")
            );

        Assert.True(match is not null, $"MythologyStyles.axaml 缺少 {selector} 的 Transitions");
    }

    [Fact]
    public void ListRowItem_HasBackgroundTransitionAndStateWash()
    {
        // SukiUI 抢注了 {x:Type ListBoxItem} 主题，AdalineRowContainer 的 /template/ 规则整条不生效；
        // 行的 hover/selected 薄雾必须挂在 ListBoxItem 自身（模板的 Background 由 TemplateBinding 来），
        // 且行自身要有 Background 过渡，否则雾是硬跳的。
        var styles = Load("Styles/MythologyStyles.axaml");

        var item = styles
            .Elements()
            .FirstOrDefault(e =>
                e.Name.LocalName == "Style"
                && AttrPlain(e, "Selector") == "ListBoxItem"
                && e.Descendants()
                    .Any(d =>
                        d.Name.LocalName == "BrushTransition"
                        && AttrPlain(d, "Property") == "Background"
                    )
            );
        Assert.True(item is not null, "MythologyStyles.axaml 缺少 ListBoxItem 的 Background 过渡");

        foreach (
            var (selector, brush) in new[]
            {
                ("ListBoxItem:pointerover", "BrushSageMistWash"),
                ("ListBoxItem:selected", "BrushSageMistSelected"),
            }
        )
        {
            var wash = styles
                .Elements()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "Style"
                    && AttrPlain(e, "Selector") == selector
                    && e.Elements()
                        .Any(s =>
                            AttrPlain(s, "Property") == "Background"
                            && AttrPlain(s, "Value")!.Contains(brush, StringComparison.Ordinal)
                        )
                );
            Assert.True(wash is not null, $"MythologyStyles.axaml 缺少 {selector} 的 {brush}");
        }
    }

    [Fact]
    public void ListRowItem_IsFlatWithoutPaddingOrRadius()
    {
        // SukiUI 行主题自带 4,2,4,2 内边距 + 圆角高亮块；扁平列表要求行自身 Padding/CornerRadius 归零
        // （内边距由行模板的 Margin 出，分隔线由 AdalineRowContainer 的底边框出）。
        var styles = Load("Styles/MythologyStyles.axaml");
        var item = styles
            .Elements()
            .FirstOrDefault(e =>
                e.Name.LocalName == "Style" && AttrPlain(e, "Selector") == "ListBoxItem"
            );

        Assert.True(item is not null, "MythologyStyles.axaml 缺少 ListBoxItem 样式");
        Assert.Contains(
            item.Elements(),
            s => AttrPlain(s, "Property") == "Padding" && AttrPlain(s, "Value") == "0"
        );
        Assert.Contains(
            item.Elements(),
            s => AttrPlain(s, "Property") == "CornerRadius" && AttrPlain(s, "Value") == "0"
        );
    }

    [Fact]
    public void ToggleLikeControls_DeclareStateTransitions()
    {
        // ToggleButton / CheckBox / RadioButton / Slider / Expander 没有 Adaline 模板，
        // 状态切换原本是硬跳；全局样式必须给它们补上 Background/BorderBrush 过渡。
        var styles = Load("Styles/MythologyStyles.axaml");

        foreach (
            var selector in new[]
            {
                "ToggleButton, CheckBox, RadioButton",
                "Slider",
                "Expander /template/ ToggleButton",
            }
        )
        {
            var match = styles
                .Elements()
                .FirstOrDefault(e =>
                    e.Name.LocalName == "Style"
                    && AttrPlain(e, "Selector") == selector
                    && e.Descendants().Any(d => d.Name.LocalName == "Transitions")
                );
            Assert.True(match is not null, $"MythologyStyles.axaml 缺少 {selector} 的 Transitions");
        }
    }

    [Fact]
    public void ListRowHosts_UseFlatRowContainerWithoutNestedCards()
    {
        // 行模板内再套 Classes="Card" 会画出双层边框（外层 Card 一条 Mist + 行内一条），
        // 且实色底会盖住 ListBoxItem 的状态雾——两层颜色不一致即用户看到的「不统一」。
        foreach (
            var relative in new[]
            {
                "Controls/SessionManagerWindow.axaml",
                "Controls/NotesWindow.axaml",
                "Controls/ProjectPickerWindow.axaml",
            }
        )
        {
            var list = Single(Load(relative), e => e.Name.LocalName == "ListBox");
            Assert.Contains(
                "AdalineRowContainer",
                AttrPlain(list, "ItemContainerTheme"),
                StringComparison.Ordinal
            );

            var nested = list.Descendants()
                .Any(e =>
                    e.Name.LocalName == "Border"
                    && (AttrPlain(e, "Classes") ?? "").Contains("Card", StringComparison.Ordinal)
                );
            Assert.False(nested, $"{relative} 的行模板里还套着 Card（双层边框 + 双层底色）");
        }
    }

    [Fact]
    public void RowContainerTheme_IsFlatWithHairlineDividerAndNoRowBorder()
    {
        var theme = Single(
            Load("Styles/MythologyTheme.axaml"),
            e =>
                e.Name.LocalName == "ControlTheme"
                && e.Attributes()
                    .Any(a => a.Name.LocalName == "Key" && a.Value == "AdalineRowContainer")
        );

        // 行无内边距、无圆角：内边距由行模板出，圆角由外层 Card 出
        Assert.Contains(
            theme.Descendants().Where(e => e.Name.LocalName == "Setter"),
            s => AttrPlain(s, "Property") == "Padding" && AttrPlain(s, "Value") == "0"
        );
        // 分隔线：仅底边 1px Mist（DESIGN.md §Code Block Panel "inner rows separated by 1px dividers"）
        Assert.Contains(
            theme.Descendants().Where(e => e.Name.LocalName == "Setter"),
            s => AttrPlain(s, "Property") == "BorderThickness" && AttrPlain(s, "Value") == "0,0,0,1"
        );
        // 状态雾用 Sage Mist 系（DESIGN.md §Components Pill Tab "subtle Sage Mist wash"）
        var text = theme.ToString();
        Assert.Contains("BrushSageMistWash", text, StringComparison.Ordinal);
        Assert.Contains("BrushSageMistSelected", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentTagInListRows_UsesOutlineNotSolidFill()
    {
        // DESIGN.md §Category Tag：「transparent with hairline border」。
        // 实色 Eucalyptus 块会把一行切成两个颜色，与行底状态雾不一致。
        var styles = Load("Styles/MythologyStyles.axaml");
        var outline = styles
            .Elements()
            .FirstOrDefault(e =>
                e.Name.LocalName == "Style"
                && AttrPlain(e, "Selector") == "Border.TagPillOutline"
                && e.Elements()
                    .Any(s =>
                        AttrPlain(s, "Property") == "Background"
                        && AttrPlain(s, "Value") == "Transparent"
                    )
            );

        Assert.True(
            outline is not null,
            "MythologyStyles.axaml 缺少 Border.TagPillOutline（透明底描边标签）"
        );
    }

    [Fact]
    public void ButtonPressedStyle_ScalesDownForPressFeedback()
    {
        var styles = Load("Styles/MythologyStyles.axaml");
        var pressed = styles
            .Elements()
            .FirstOrDefault(e =>
                e.Name.LocalName == "Style"
                && AttrPlain(e, "Selector") == "Button:pressed"
                && e.Descendants()
                    .Any(d =>
                        AttrPlain(d, "Property") == "RenderTransform"
                        && AttrPlain(d, "Value") == "scale(0.97)"
                    )
            );

        Assert.True(
            pressed is not null,
            "MythologyStyles.axaml 缺少 Button:pressed 的 scale(0.97)"
        );
    }

    [Fact]
    public void ConversationComposerModeButtons_FadeIn()
    {
        var root = Load("Views/ConversationView.axaml");
        foreach (var name in new[] { "StopButton", "SendButton" })
        {
            var button = Single(
                root,
                e => e.Name.LocalName == "Button" && AttrPlain(e, "Name") == name
            );
            Assert.Equal("True", Attr(button, "Motion.Fade"));
        }
    }

    [Fact]
    public void ConversationAgentStepCompletion_PopsIn()
    {
        var stepTemplate = Single(
            Load("Views/ConversationView.axaml"),
            e =>
                e.Name.LocalName == "DataTemplate"
                && e.Attributes()
                    .Any(a => a.Name.LocalName == "DataType" && a.Value.Contains("AgentStep"))
        );
        var icon = stepTemplate
            .Descendants()
            .Single(e =>
                e.Name.LocalName == "LucideIcon"
                && AttrPlain(e, "IsVisible")
                    .Contains("InvertedBoolToVisibility", StringComparison.Ordinal)
            );

        Assert.Equal("True", Attr(icon, "Motion.Pop"));
    }

    [Fact]
    public void ConversationResultSummary_FadesIn()
    {
        var text = Single(
            Load("Views/ConversationView.axaml"),
            e =>
                e.Name.LocalName == "TextBlock"
                && AttrPlain(e, "Text").Contains("ResultSummary", StringComparison.Ordinal)
        );

        Assert.Equal("True", Attr(text, "Motion.Fade"));
    }

    [Fact]
    public void ConversationToolDetails_PlaysEnterWithSmallRise()
    {
        var details = Single(
            Load("Views/ConversationView.axaml"),
            e =>
                e.Name.LocalName == "StackPanel"
                && AttrPlain(e, "Classes").Contains("StepDetails", StringComparison.Ordinal)
        );

        Assert.Equal("True", Attr(details, "Motion.Enter"));
        Assert.Equal("4", Attr(details, "Motion.EnterRise"));
    }

    [Fact]
    public void MainWindowUpdateBadge_PopsIn()
    {
        var dot = Single(
            Load("Views/MainWindow.axaml"),
            e =>
                e.Name.LocalName == "Ellipse"
                && AttrPlain(e, "IsVisible")
                    .Contains("UpdatePrompt.HasPendingUpdate", StringComparison.Ordinal)
        );

        Assert.Equal("True", Attr(dot, "Motion.Pop"));
    }

    [Fact]
    public void UpdatePromptActionButtons_FadeIn()
    {
        var root = Load("Controls/UpdatePromptDialog.axaml");
        foreach (var content in new[] { "重试", "开始安装" })
        {
            var button = Single(
                root,
                e => e.Name.LocalName == "Button" && AttrPlain(e, "Content") == content
            );
            Assert.Equal("True", Attr(button, "Motion.Fade"));
        }
    }

    [Fact]
    public void SettingsTabUnderline_GrowsOnSelection()
    {
        var root = Load("Views/SettingsView.axaml");
        var underline = Single(
            root,
            e => e.Name.LocalName == "Border" && AttrPlain(e, "Name") == "PART_Underline"
        );
        Assert.True(
            underline
                .Descendants()
                .Any(d =>
                    d.Name.LocalName == "TransformOperationsTransition"
                    && AttrPlain(d, "Property") == "RenderTransform"
                ),
            "PART_Underline 缺少 RenderTransform 过渡"
        );

        var selected = Single(
            root,
            e =>
                e.Name.LocalName == "Style"
                && AttrPlain(e, "Selector") == "^:selected /template/ Border#PART_Underline"
        );
        Assert.True(
            selected
                .Elements()
                .Any(s =>
                    AttrPlain(s, "Property") == "RenderTransform"
                    && AttrPlain(s, "Value").Contains("scale(1,1)", StringComparison.Ordinal)
                ),
            "PART_Underline 选中态缺少 scale(1,1)"
        );
    }

    [Fact]
    public void SettingsAsyncStatus_FadesIn()
    {
        var root = Load("Views/SettingsView.axaml");
        foreach (
            var binding in new[]
            {
                "ModelDiscoverStatus",
                "EmbeddingModelDiscoverStatus",
                "SaveStatus",
            }
        )
        {
            var text = Single(
                root,
                e =>
                    e.Name.LocalName == "TextBlock"
                    && AttrPlain(e, "Text") == $"{{Binding {binding}}}"
            );
            Assert.Equal("True", Attr(text, "Motion.Fade"));
        }
    }

    [Fact]
    public void SetupCheckRunButton_HasBusySpinnerAndFade()
    {
        var root = Load("Controls/SetupCheckWindow.axaml");
        var button = Single(
            root,
            e => e.Name.LocalName == "Button" && AttrPlain(e, "Name") == "RunCheckButton"
        );
        Assert.Contains("IsChecking", AttrPlain(button, "IsEnabled"), StringComparison.Ordinal);

        var spinner = Single(
            button,
            e =>
                e.Name.LocalName == "BusySpinner"
                && AttrPlain(e, "IsActive").Contains("IsChecking", StringComparison.Ordinal)
        );
        Assert.NotNull(spinner);

        var idle = Single(
            button,
            e => e.Name.LocalName == "TextBlock" && AttrPlain(e, "Text") == "运行检查"
        );
        Assert.Equal("True", Attr(idle, "Motion.Fade"));

        var busy = Single(
            button,
            e =>
                e.Name.LocalName == "StackPanel"
                && AttrPlain(e, "IsVisible").Contains("IsChecking", StringComparison.Ordinal)
        );
        Assert.Equal("True", Attr(busy, "Motion.Fade"));
    }

    [Fact]
    public void AskUserCustomPanel_PlaysEnterWithSmallRise()
    {
        var panel = Single(
            Load("Controls/AskUserWindow.axaml"),
            e => e.Name.LocalName == "StackPanel" && AttrPlain(e, "Name") == "CustomPanel"
        );

        Assert.Equal("True", Attr(panel, "Motion.Enter"));
        Assert.Equal("4", Attr(panel, "Motion.EnterRise"));
    }

    [Fact]
    public void TodoCompletedLabel_FadesIn()
    {
        var text = Single(
            Load("Views/TodoPanelView.axaml"),
            e =>
                e.Name.LocalName == "TextBlock"
                && AttrPlain(e, "IsVisible").Contains("IsCompleted", StringComparison.Ordinal)
        );

        Assert.Equal("True", Attr(text, "Motion.Fade"));
    }

    /// <summary>
    /// ToolTip 只能动画 Opacity。
    ///
    /// Avalonia 的 <c>Style.Animations</c> 没有 RenderTransform 的 animator：
    /// KeyFrame 里 Set <c>RenderTransform</c> 会在 <c>ToolTip.Open</c> 抛
    /// <c>InvalidOperationException: No animator registered for the property RenderTransform</c>。
    /// 实机崩溃日志（myth-desktop-crash.log）显示异常从 macOS 原生 RawMouseEvent 的
    /// Dispatcher.Send 路径冒出——鼠标一进窗口就打断输入帧，表现为悬浮/滚动全部卡死。
    /// 回归锁：动画里出现任何 RenderTransform setter 即失败。
    /// </summary>
    [Fact]
    public void ToolTip_FadesIn_WithoutRenderTransformAnimation()
    {
        var style = Single(
            Load("Styles/MythologyStyles.axaml"),
            e => e.Name.LocalName == "Style" && AttrPlain(e, "Selector") == "ToolTip"
        );
        var animation = Single(style, e => e.Name.LocalName == "Animation");
        Assert.Contains("Motion.Fast", AttrPlain(animation, "Duration"), StringComparison.Ordinal);

        var setters = animation
            .Descendants()
            .Where(e => e.Name.LocalName == "Setter")
            .Select(e => (Property: AttrPlain(e, "Property"), Value: AttrPlain(e, "Value")))
            .ToList();
        Assert.DoesNotContain(
            setters,
            s => s.Property.Contains("RenderTransform", StringComparison.Ordinal)
        );
        Assert.Equal(
            new[] { ("Opacity", "0"), ("Opacity", "1") },
            setters.Select(s => (s.Property, s.Value)).ToArray()
        );
        Assert.DoesNotContain(
            style.Elements(),
            e => e.Name.LocalName == "Setter" && AttrPlain(e, "Property") == "RenderTransform"
        );
    }

    private static string AttrPlain(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value ?? "";
}
