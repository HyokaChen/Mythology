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
                && Attr(e, "IsVisible") == "{Binding IsDetailsExpanded}"
            );
    }

    private static XElement TextBlockWithText(XElement root, string text) =>
        root.Descendants().Single(e => e.Name.LocalName == "TextBlock" && Attr(e, "Text") == text);

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
        var textual = details
            .Descendants()
            .Where(e => e.Name.LocalName == "TextBlock")
            .Select(e => Attr(e, "Text"))
            .ToList();

        Assert.Contains("参数", textual);
        Assert.Contains("结果", textual);
        Assert.Contains("{Binding ThinkingText}", textual);
        Assert.DoesNotContain("思考", textual);

        Assert.Equal(
            "{Binding HasThinking}",
            Attr(TextBlockWithText(details, "{Binding ThinkingText}"), "IsVisible")
        );

        // 工具详情的两个标题只在各自内容存在时出现。
        Assert.Equal(
            "{Binding HasDetailArguments}",
            Attr(TextBlockWithText(details, "参数"), "IsVisible")
        );
        Assert.Equal(
            "{Binding HasResultDetail}",
            Attr(TextBlockWithText(details, "结果"), "IsVisible")
        );
    }

    [Fact]
    public void ThinkingBody_IsVisibleOnlyWithThinkingContent_AndInsideCollapsibleDetails()
    {
        var root = LoadConversationView();
        var thinking = TextBlockWithText(root, "{Binding ThinkingText}");

        Assert.Equal("{Binding HasThinking}", Attr(thinking, "IsVisible"));
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
                e.Name.LocalName == "TextBlock" && Attr(e, "Text") == "{Binding ContentText}"
            );

        Assert.Equal("StepContentText", Attr(content, "Classes"));
        var card = content
            .Ancestors()
            .Single(e => e.Name.LocalName == "Border" && Attr(e, "Classes") == "AgentBubble");
        Assert.Equal("{Binding IsContentStep}", Attr(card, "IsVisible"));
        Assert.Contains(
            root.Descendants(),
            e =>
                e.Name.LocalName == "StackPanel"
                && Attr(e, "IsVisible") == "{Binding IsNotContentStep}"
        );
    }

    [Fact]
    public void DetailsBlock_RendersStructuredDiff_AndHidesRawResultWhenDiffExists()
    {
        var details = DetailsBlock();

        // 原始结果文本只在没有结构化 diff 时回退显示。
        Assert.Equal(
            "{Binding ShowRawResultDetail}",
            Attr(TextBlockWithText(details, "{Binding DetailResult}"), "IsVisible")
        );

        var items = details
            .Descendants()
            .Single(e =>
                e.Name.LocalName == "ItemsControl"
                && Attr(e, "ItemsSource") == "{Binding DiffLines}"
            );
        Assert.Contains(
            items.Ancestors(),
            e => e.Name.LocalName == "StackPanel" && Attr(e, "IsVisible") == "{Binding HasDiff}"
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
                e.Name.LocalName == "Button" && Attr(e, "Content") == "{Binding DetailsToggleLabel}"
            );

        Assert.Equal("StepDetails_OnClick", Attr(button, "Click"));
        Assert.Equal("{Binding HasDetails}", Attr(button, "IsVisible"));
    }
}
