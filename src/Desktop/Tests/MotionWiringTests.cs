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

    private static string Source(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Desktop", relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
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

    /// <summary>
    /// 行窗口会裁剪/重挂行；逐行 Motion.Enter/Fade/Pop 会在重建时重播。
    /// 整个行模板不得出现任何 motion 属性；列表外的空状态 / 输入区动效仍保留。
    /// </summary>
    [Fact]
    public void ConversationMessageTemplate_IsAnimationFree()
    {
        var templates = Load("Views/ConversationView.axaml")
            .Descendants()
            .Where(e =>
                e.Name.LocalName == "DataTemplate"
                && AttrPlain(e, "Key").StartsWith("Row", StringComparison.Ordinal)
            )
            .ToList();
        Assert.NotEmpty(templates);
        var animated = templates
            .SelectMany(template => template.Descendants())
            .Where(e => e.Attributes().Any(a => a.Name.NamespaceName == MotionNamespace))
            .Select(e => e.Name.LocalName)
            .ToList();

        Assert.Empty(animated);
    }

    [Fact]
    public void ConversationEmptyState_PlaysEnter()
    {
        var root = Load("Views/ConversationView.axaml");
        var anchor = Single(root, e => AttrPlain(e, "Text") == "开始创作");
        var panel = anchor.Ancestors().First(a => a.Name.LocalName == "StackPanel");

        Assert.Equal("True", Attr(panel, "Motion.Enter"));
    }

    [Theory]
    [InlineData("SuggestionPopup")]
    [InlineData("CommandPopup")]
    public void ConversationPopups_PlayEnterOnOpen(string popupName)
    {
        var root = Load("Views/ConversationView.axaml");
        var popup = Single(root, e =>
            e.Name.LocalName == "Popup"
            && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == popupName)
        );

        Assert.Equal("True", Attr(popup, "Motion.PopupEnter"));
    }

    [Fact]
    public void SetUpPanelTabPane_DoesNotAttachEnterAnimation()
    {
        // 翻页由 SetUpPanelView.AnimateTabChange 统一负责；模板再挂 Enter 会让 Opacity 双动画打架
        var owner = Single(Load("Views/SetUpPanelView.axaml"), e =>
            e.Name.LocalName == "TabControl.ContentTemplate"
        );
        var template = owner.Elements().First(e => e.Name.LocalName == "DataTemplate");
        var pane = template.Elements().First();
        Assert.NotEqual("True", Attr(pane, "Motion.Enter"));
    }

    [Fact]
    public void TodoPanelItem_PlaysEnterWithSmallRise()
    {
        var itemTemplate = Single(Load("Views/TodoPanelView.axaml"), e =>
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
        var banner = Single(root, e =>
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
        var panel = Single(root, e =>
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
        Assert.Contains(item.Elements(), s =>
            AttrPlain(s, "Property") == "Padding" && AttrPlain(s, "Value") == "0"
        );
        Assert.Contains(item.Elements(), s =>
            AttrPlain(s, "Property") == "CornerRadius" && AttrPlain(s, "Value") == "0"
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
                "ToggleButton, CheckBox, RadioButton, ToggleSwitch",
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
        var theme = Single(Load("Styles/MythologyTheme.axaml"), e =>
            e.Name.LocalName == "ControlTheme"
            && e.Attributes()
                .Any(a => a.Name.LocalName == "Key" && a.Value == "AdalineRowContainer")
        );

        // 行无内边距、无圆角：内边距由行模板出，圆角由外层 Card 出
        Assert.Contains(theme.Descendants().Where(e => e.Name.LocalName == "Setter"), s =>
            AttrPlain(s, "Property") == "Padding" && AttrPlain(s, "Value") == "0"
        );
        // 分隔线：仅底边 1px Mist（DESIGN.md §Code Block Panel "inner rows separated by 1px dividers"）
        Assert.Contains(theme.Descendants().Where(e => e.Name.LocalName == "Setter"), s =>
            AttrPlain(s, "Property") == "BorderThickness" && AttrPlain(s, "Value") == "0,0,0,1"
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
            var button = Single(root, e =>
                e.Name.LocalName == "Button" && AttrPlain(e, "Name") == name
            );
            Assert.Equal("True", Attr(button, "Motion.Fade"));
        }
    }

    [Fact]
    public void MainWindowUpdateBadge_PopsIn()
    {
        var dot = Single(Load("Views/MainWindow.axaml"), e =>
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
            var button = Single(root, e =>
                e.Name.LocalName == "Button" && AttrPlain(e, "Content") == content
            );
            Assert.Equal("True", Attr(button, "Motion.Fade"));
        }
    }

    [Fact]
    public void SettingsTabUnderline_FadesOnSelection_WithoutScale()
    {
        var root = Load("Views/SettingsView.axaml");
        var underline = Single(root, e =>
            e.Name.LocalName == "Border" && AttrPlain(e, "Name") == "PART_Underline"
        );
        Assert.Contains(underline.Descendants(), d =>
            d.Name.LocalName == "DoubleTransition" && AttrPlain(d, "Property") == "Opacity"
        );
        Assert.DoesNotContain(underline.Descendants(), d =>
            d.Name.LocalName == "TransformOperationsTransition"
        );
        Assert.DoesNotContain("scale(", underline.ToString(), StringComparison.Ordinal);

        var selected = Single(root, e =>
            e.Name.LocalName == "Style"
            && AttrPlain(e, "Selector") == "^:selected /template/ Border#PART_Underline"
        );
        Assert.Contains(selected.Elements(), s =>
            AttrPlain(s, "Property") == "Opacity" && AttrPlain(s, "Value") == "1"
        );
        Assert.DoesNotContain(selected.Elements(), s =>
            AttrPlain(s, "Property") == "RenderTransform"
        );
    }

    [Theory]
    [InlineData("Button.Primary:focus-visible /template/ ContentPresenter")]
    [InlineData("Button.Ghost:focus-visible")]
    [InlineData("Button.Danger:focus-visible")]
    [InlineData("Button.IconButton:focus-visible")]
    [InlineData("Button.MoreButton:focus-visible")]
    [InlineData("ListBoxItem:focus-visible")]
    [InlineData("TreeViewItem:focus-visible /template/ Border#SelectionBorder")]
    [InlineData("ComboBox:pointerover /template/ Border#BackgroundBorder")]
    [InlineData("ComboBox:focus /template/ Border#BackgroundBorder")]
    [InlineData("ToggleSwitch:focus-visible, CheckBox:focus-visible, RadioButton:focus-visible")]
    [InlineData("ToggleSwitch:focus-visible /template/ Grid#SwitchAreaGrid")]
    [InlineData("Button.Ghost:pressed")]
    [InlineData("Button.IconButton:pressed")]
    [InlineData("ListBoxItem:pressed")]
    [InlineData("ScrollBar Thumb:pressed")]
    [InlineData("Border.Card.Glow.Focused")]
    public void GlobalStyles_DeclareKeyboardFocusAndPressedStates(string selector)
    {
        var styles = Load("Styles/MythologyStyles.axaml");
        Assert.Contains(styles.Elements(), e =>
            e.Name.LocalName == "Style" && AttrPlain(e, "Selector") == selector
        );
    }

    [Fact]
    public void ContextMenu_OpenAnimation_FadesOnlyWithMicroToken()
    {
        var theme = Single(Load("Styles/MythologyTheme.axaml"), e =>
            e.Name.LocalName == "ControlTheme"
            && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "AdalineContextMenu")
        );
        var animations = theme.Descendants().Where(e => e.Name.LocalName == "Animation").ToList();
        var animation = Assert.Single(animations);
        Assert.Contains("Motion.Micro", AttrPlain(animation, "Duration"), StringComparison.Ordinal);
        Assert.Contains(
            "Motion.SoftEasing",
            AttrPlain(animation, "Easing"),
            StringComparison.Ordinal
        );

        var setters = animation
            .Descendants()
            .Where(e => e.Name.LocalName == "Setter")
            .Select(e => AttrPlain(e, "Property"))
            .ToList();
        Assert.Equal(new[] { "Opacity", "Opacity" }, setters);
        Assert.DoesNotContain("ScaleTransform", theme.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsTabHeader_UsesFastSoftTransition()
    {
        var presenter = Single(Load("Views/SettingsView.axaml"), e =>
            e.Name.LocalName == "ContentPresenter" && AttrPlain(e, "Name") == "PART_HeaderPresenter"
        );
        var transition = Assert.Single(
            presenter.Descendants().Where(e => e.Name.LocalName == "BrushTransition")
        );
        Assert.Contains("Motion.Fast", AttrPlain(transition, "Duration"), StringComparison.Ordinal);
        Assert.Contains(
            "Motion.SoftEasing",
            AttrPlain(transition, "Easing"),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ReducedMotionSetting_IsBoundInAppearanceTab()
    {
        var settings = Load("Views/SettingsView.axaml");
        var toggle = Single(settings, e =>
            e.Name.LocalName == "ToggleSwitch"
            && AttrPlain(e, "IsChecked") == "{Binding IsReducedMotion}"
        );
        Assert.NotNull(toggle);

        // 持久化本身不锁源码字符串（同义反复）：由偏好序列化/手动重启冒烟覆盖。
        Assert.Contains(
            "public bool IsReducedMotion",
            Source("ViewModels/SettingsViewModel.cs"),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ConversationInputCard_TracksFocusWithOliveBorder()
    {
        var root = Load("Views/ConversationView.axaml");
        var card = Single(root, e =>
            e.Name.LocalName == "Border" && AttrPlain(e, "Name") == "InputCard"
        );
        Assert.Contains(card.Descendants(), d =>
            d.Name.LocalName == "BrushTransition" && AttrPlain(d, "Property") == "BorderBrush"
        );

        var code = Source("Views/ConversationView.axaml.cs");
        Assert.Contains("InputBox.GotFocus", code, StringComparison.Ordinal);
        Assert.Contains("InputBox.LostFocus", code, StringComparison.Ordinal);
        Assert.Contains("Classes.Set(\"Focused\"", code, StringComparison.Ordinal);
        Assert.Contains(
            "Border.Card.Glow.Focused",
            Source("Styles/MythologyStyles.axaml"),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ChapterReader_OutlineFadesOnce_WithoutXamlEnter()
    {
        var code = Source("Controls/ChapterReaderWindow.axaml.cs");
        Assert.Contains("_outlineAnimated", code, StringComparison.Ordinal);
        Assert.Contains("Motion.PlayEnter(OutlinePanel", code, StringComparison.Ordinal);
        Assert.Contains("Motion.PlayPageEnter(ContentEditor", code, StringComparison.Ordinal);

        var panel = Single(Load("Controls/ChapterReaderWindow.axaml"), e =>
            e.Name.LocalName == "Border" && AttrPlain(e, "Name") == "OutlinePanel"
        );
        Assert.NotEqual("True", Attr(panel, "Motion.Enter"));
    }

    [Theory]
    [InlineData("Controls/ProjectPickerWindow.axaml", "EmptyNote")]
    [InlineData("Controls/SessionManagerWindow.axaml", "EmptyNote")]
    [InlineData("Controls/NotesWindow.axaml", "EmptyNote")]
    public void ListDialogEmptyStates_FadeIn(string relative, string name)
    {
        var note = Single(Load(relative), e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name)
        );
        Assert.Equal("True", Attr(note, "Motion.Fade"));
    }

    [Fact]
    public void SetUpAndTodoEmptyStates_FadeIn()
    {
        var setUp = Single(Load("Views/SetUpPanelView.axaml"), e =>
            e.Name.LocalName == "Border"
            && (AttrPlain(e, "Classes") ?? "").Contains("EmptyNote", StringComparison.Ordinal)
        );
        Assert.Equal("True", Attr(setUp, "Motion.Fade"));

        var todo = Single(Load("Views/TodoPanelView.axaml"), e =>
            e.Name.LocalName == "Border"
            && AttrPlain(e, "IsVisible").Contains("IsEmpty", StringComparison.Ordinal)
        );
        Assert.Equal("True", Attr(todo, "Motion.Fade"));
    }

    [Fact]
    public void AsyncPanels_PlayEnterWithSmallRise()
    {
        foreach (
            var (relative, name) in new[]
            {
                ("Controls/CloudLoginWindow.axaml", "DevicePanel"),
                ("Controls/CloudLoginWindow.axaml", "SuccessPanel"),
                ("Controls/WorldRulePanel.axaml", "RuleFormScroll"),
                ("Controls/WorldRulePanel.axaml", "SettingFormScroll"),
                ("Controls/TreeWindow.axaml", "LabelPanel"),
            }
        )
        {
            var panel = Single(Load(relative), e =>
                e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name)
            );
            Assert.Equal("True", Attr(panel, "Motion.Enter"));
            Assert.Equal("4", Attr(panel, "Motion.EnterRise"));
        }
    }

    [Fact]
    public void PlayExit_ConsumesExitEasingInBothChannels()
    {
        var source = Source("Styles/Motion.cs");
        var start = source.IndexOf("public static async Task PlayExit", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf(
            "private static void ResetTranslation",
            start,
            StringComparison.Ordinal
        );
        Assert.True(end > start);
        var body = source[start..end];
        Assert.True(
            body.Split("ExitEasing").Length - 1 >= 2,
            "PlayExit 必须在合成器与 UI 线程两条退场路径都使用 ExitEasing"
        );
        Assert.Contains("var actual = Exit;", body, StringComparison.Ordinal);
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
            var text = Single(root, e =>
                e.Name.LocalName == "TextBlock" && AttrPlain(e, "Text") == $"{{Binding {binding}}}"
            );
            Assert.Equal("True", Attr(text, "Motion.Fade"));
        }
    }

    [Fact]
    public void SetupCheckRunButton_HasBusySpinnerAndFade()
    {
        var root = Load("Controls/SetupCheckWindow.axaml");
        var button = Single(root, e =>
            e.Name.LocalName == "Button" && AttrPlain(e, "Name") == "RunCheckButton"
        );
        Assert.Contains("IsChecking", AttrPlain(button, "IsEnabled"), StringComparison.Ordinal);

        var spinner = Single(button, e =>
            e.Name.LocalName == "BusySpinner"
            && AttrPlain(e, "IsActive").Contains("IsChecking", StringComparison.Ordinal)
        );
        Assert.NotNull(spinner);

        var idle = Single(button, e =>
            e.Name.LocalName == "TextBlock" && AttrPlain(e, "Text") == "运行检查"
        );
        Assert.Equal("True", Attr(idle, "Motion.Fade"));

        var busy = Single(button, e =>
            e.Name.LocalName == "StackPanel"
            && AttrPlain(e, "IsVisible").Contains("IsChecking", StringComparison.Ordinal)
        );
        Assert.Equal("True", Attr(busy, "Motion.Fade"));
    }

    [Fact]
    public void TodoCompletedLabel_FadesIn()
    {
        var text = Single(Load("Views/TodoPanelView.axaml"), e =>
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
        var style = Single(Load("Styles/MythologyStyles.axaml"), e =>
            e.Name.LocalName == "Style" && AttrPlain(e, "Selector") == "ToolTip"
        );
        var animation = Single(style, e => e.Name.LocalName == "Animation");
        Assert.Contains("Motion.Fast", AttrPlain(animation, "Duration"), StringComparison.Ordinal);

        var setters = animation
            .Descendants()
            .Where(e => e.Name.LocalName == "Setter")
            .Select(e => (Property: AttrPlain(e, "Property"), Value: AttrPlain(e, "Value")))
            .ToList();
        Assert.DoesNotContain(setters, s =>
            s.Property.Contains("RenderTransform", StringComparison.Ordinal)
        );
        Assert.Equal(
            new[] { ("Opacity", "0"), ("Opacity", "1") },
            setters.Select(s => (s.Property, s.Value)).ToArray()
        );
        Assert.DoesNotContain(style.Elements(), e =>
            e.Name.LocalName == "Setter" && AttrPlain(e, "Property") == "RenderTransform"
        );
    }

    private static string AttrPlain(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value ?? "";
}
