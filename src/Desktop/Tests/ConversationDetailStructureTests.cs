using System.Xml.Linq;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 对话步骤详情区的结构锁：
/// 思考正文与工具「参数 / 结果」共用可折叠详情区（思考默认展开、无需重复「思考」小标题）。
/// </summary>
public sealed class ConversationDetailStructureTests
{
    private static XElement LoadConversationView()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "src",
                "Desktop",
                "Views",
                "ConversationView.axaml"
            );
            if (File.Exists(candidate))
                return XDocument.Load(candidate).Root!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("未找到 src/Desktop/Views/ConversationView.axaml");
    }

    private static string Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    private static XElement DetailsBlock()
    {
        return LoadConversationView()
            .Descendants()
            .Single(e =>
                e.Name.LocalName == "StackPanel"
                && Attr(e, "Classes") == "StepDetails"
                && Attr(e, "IsVisible") == "{Binding Step.IsDetailsExpanded}"
            );
    }

    private static bool IsTextual(XElement element) =>
        element.Name.LocalName is "TextBlock" or "SelectableTextBlock";

    private static XElement TextBlockWithText(XElement root, string text) =>
        root.Descendants().Single(e => IsTextual(e) && Attr(e, "Text") == text);

    [Fact]
    public void DetailsBlock_HasNoCardChrome()
    {
        var details = DetailsBlock();
        var attributes = details.Attributes().Select(a => a.Name.LocalName).ToList();

        Assert.Equal("StackPanel", details.Name.LocalName);
        Assert.DoesNotContain("Background", attributes);
        Assert.DoesNotContain("BorderBrush", attributes);
        Assert.DoesNotContain("BorderThickness", attributes);
        Assert.DoesNotContain("CornerRadius", attributes);
        Assert.DoesNotContain("Padding", attributes);
        Assert.DoesNotContain("MaxWidth", attributes);
        Assert.Equal("Stretch", Attr(details, "HorizontalAlignment"));
    }

    [Fact]
    public void DetailsBlock_HasThinkingBodyAndToolLabels()
    {
        var details = DetailsBlock();
        var textual = details.Descendants().Where(IsTextual).Select(e => Attr(e, "Text")).ToList();

        Assert.Contains("参数", textual);
        Assert.Contains("结果", textual);
        Assert.Contains("{Binding Step.ThinkingText}", textual);
        Assert.DoesNotContain("思考", textual);

        var thinkingBody = TextBlockWithText(details, "{Binding Step.ThinkingText}");
        var thinkingScroll = thinkingBody
            .Ancestors()
            .Single(e => e.Name.LocalName == "ScrollViewer");
        Assert.Equal("{Binding Step.HasThinking}", Attr(thinkingScroll, "IsVisible"));

        // 工具详情的两个标题只在各自内容存在时出现。
        Assert.Equal(
            "{Binding Step.HasDetailArguments}",
            Attr(TextBlockWithText(details, "参数"), "IsVisible")
        );
        Assert.Equal(
            "{Binding Step.HasResultDetail}",
            Attr(TextBlockWithText(details, "结果"), "IsVisible")
        );
    }

    [Fact]
    public void ThinkingBody_IsVisibleOnlyWithThinkingContent_AndInsideCollapsibleDetails()
    {
        var root = LoadConversationView();
        var thinking = TextBlockWithText(root, "{Binding Step.ThinkingText}");

        Assert.Contains(
            thinking.Ancestors(),
            e =>
                e.Name.LocalName == "ScrollViewer"
                && Attr(e, "IsVisible") == "{Binding Step.HasThinking}"
        );
        Assert.Contains("TextWrapping", thinking.Attributes().Select(a => a.Name.LocalName));
        Assert.Contains(
            thinking.Ancestors(),
            e => e.Name.LocalName == "StackPanel" && Attr(e, "Classes") == "StepDetails"
        );
    }

    [Fact]
    public void ContentStep_RendersTextBody_WithoutStepHeaderChrome()
    {
        var root = LoadConversationView();
        var content = root.Descendants()
            .Single(e =>
                e.Name.LocalName == "MarkdownRenderer"
                && Attr(e, "MarkdownBuilder") == "{Binding ContentMarkdown}"
            );

        Assert.Contains("StepContentMarkdown", Attr(content, "Classes"));
        var host = root.Descendants()
            .Single(e =>
                e.Name.LocalName == "ContentControl"
                && Attr(e, "ContentTemplate") == "{StaticResource StepContentMarkdown}"
            );
        var card = host.Ancestors()
            .Single(e => e.Name.LocalName == "Border" && Attr(e, "Classes") == "AgentBubble");
        Assert.Equal("{Binding Step.IsContentStep}", Attr(card, "IsVisible"));
        Assert.Contains(
            root.Descendants(),
            e =>
                e.Name.LocalName == "StackPanel"
                && Attr(e, "IsVisible") == "{Binding Step.IsStepHeader}"
        );
    }

    [Fact]
    public void NoticeStep_RendersCompactStatusRow_WithoutStepHeader()
    {
        var root = LoadConversationView();
        var notice = root.Descendants()
            .Single(e =>
                IsTextual(e)
                && Attr(e, "Classes").Contains("AgentStepNoticeText", StringComparison.Ordinal)
            );

        Assert.Contains("SystemNoticeText", Attr(notice, "Classes"));
        Assert.Equal("{Binding Step.ContentText}", Attr(notice, "Text"));
        Assert.Equal("{Binding Step.IsNoticeStep}", Attr(notice, "IsVisible"));
    }

    [Fact]
    public void DetailsBlock_RendersStructuredDiff_AndHidesRawResultWhenDiffExists()
    {
        var details = DetailsBlock();

        // 原始结果文本只在没有结构化 diff 时回退显示（外层限高滚动）。
        var rawResult = TextBlockWithText(details, "{Binding Step.DetailResult}");
        var rawResultScroll = rawResult.Ancestors().First(e => e.Name.LocalName == "ScrollViewer");
        Assert.Equal("{Binding Step.ShowRawResultDetail}", Attr(rawResultScroll, "IsVisible"));

        var items = details
            .Descendants()
            .Single(e =>
                e.Name.LocalName == "ItemsControl"
                && Attr(e, "ItemsSource") == "{Binding Step.DiffLines}"
            );
        Assert.Contains(
            items.Ancestors(),
            e =>
                e.Name.LocalName == "StackPanel" && Attr(e, "IsVisible") == "{Binding Step.HasDiff}"
        );

        var template = items.Descendants().Single(e => e.Name.LocalName == "DataTemplate");
        Assert.Equal("models:DiffLineView", Attr(template, "DataType"));

        var rows = template.Descendants().Where(e => e.Name.LocalName == "Border").ToList();
        Assert.Contains(
            rows,
            e =>
                Attr(e, "Classes") == "DiffRow"
                && Attr(e, "Classes.DiffDelete") == "{Binding IsDelete}"
                && Attr(e, "Classes.DiffInsert") == "{Binding IsInsert}"
        );
        Assert.Contains(
            template.Descendants().Where(e => e.Name.LocalName == "TextBlock"),
            e =>
                Attr(e, "Classes") == "DiffText"
                && Attr(e, "Classes.DiffDelete") == "{Binding IsDelete}"
                && Attr(e, "Classes.DiffInsert") == "{Binding IsInsert}"
                && Attr(e, "Text") == "{Binding Text}"
        );
    }

    [Fact]
    public void DetailsToggleButton_BindsStateLabel_AndClickHandler()
    {
        var button = LoadConversationView()
            .Descendants()
            .Single(e =>
                e.Name.LocalName == "Button"
                && Attr(e, "Content") == "{Binding Step.DetailsToggleLabel}"
            );

        Assert.Equal("StepDetails_OnClick", Attr(button, "Click"));
        Assert.Equal("{Binding Step.HasDetails}", Attr(button, "IsVisible"));
    }
}
