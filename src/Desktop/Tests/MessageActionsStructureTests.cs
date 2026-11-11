using System.Xml.Linq;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 消息动作条的结构锁：用户消息只能复制/编辑，助手最终正文才有复制/重试；
/// hover 默认透明、进入块区域才显示。运行期行为由进程内探针覆盖。
/// </summary>
public sealed class MessageActionsStructureTests
{
    [Fact]
    public void UserRow_HasSelectableBubble_AndInlineEditor()
    {
        var row = Template("RowUser");

        Assert.Contains(row.Descendants(), e => e.Name.LocalName == "SelectableTextBlock");
        var bar = Assert.Single(row.Descendants(), e => e.Name.LocalName == "MessageActionBar");
        Assert.Equal("True", Attr(bar, "ShowEdit"));
        Assert.Equal("", Attr(bar, "RetryRequested"));
        Assert.Contains(
            "UserMessageEdit_OnRequested",
            Attr(bar, "EditRequested"),
            StringComparison.Ordinal
        );

        var editor = Assert.Single(row.Descendants(), e => Attr(e, "Name") == "UserEditBox");
        Assert.Equal("TextBox", editor.Name.LocalName);
        Assert.Contains("Message.EditDraft", Attr(editor, "Text"), StringComparison.Ordinal);

        var cancel = Assert.Single(
            row.Descendants(),
            e => e.Name.LocalName == "Button" && Attr(e, "Click") == "CancelUserEdit_OnClick"
        );
        var send = Assert.Single(
            row.Descendants(),
            e => e.Name.LocalName == "Button" && Attr(e, "Click") == "SubmitUserEdit_OnClick"
        );
        Assert.Contains(cancel.Descendants(), e => e.Name.LocalName == "LucideIcon");
        Assert.Contains(send.Descendants(), e => e.Name.LocalName == "LucideIcon");
    }

    [Fact]
    public void ContentRow_HasCopyAndRetry_RetryOnlyAfterTurnCompletion()
    {
        var row = Template("RowContent");
        var bar = Assert.Single(row.Descendants(), e => e.Name.LocalName == "MessageActionBar");

        Assert.Contains("Message.IsCompleted", Attr(bar, "ShowRetry"), StringComparison.Ordinal);
        Assert.Equal("", Attr(bar, "EditRequested"));
        Assert.Contains(
            "MessageRetry_On_Requested",
            Attr(bar, "RetryRequested"),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void StepRow_HasCopyOnly_AndNoRetry()
    {
        var row = Template("RowStep");
        var copy = Assert.Single(
            row.Descendants(),
            e =>
                e.Name.LocalName == "Button"
                && Attr(e, "Classes").Contains("StepHeaderAction", StringComparison.Ordinal)
        );

        Assert.Contains("StepCopy_OnClick", Attr(copy, "Click"), StringComparison.Ordinal);
        Assert.DoesNotContain(
            row.Descendants().Where(e => e.Name.LocalName == "MessageActionBar"),
            bar => Attr(bar, "RetryRequested").Length > 0
        );
    }

    [Fact]
    public void ContentlessAssistant_HasRetryActionsRow()
    {
        var row = Template("RowActions");
        var bar = Assert.Single(row.Descendants(), e => e.Name.LocalName == "MessageActionBar");

        Assert.Equal("True", Attr(bar, "ShowRetry"));
        Assert.Contains(
            "MessageRetry_On_Requested",
            Attr(bar, "RetryRequested"),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void HoverStyles_HideActionsUntilPointerEntersBlock()
    {
        var root = Load("src", "Desktop", "Views", "ConversationView.axaml");
        var styles = root.Descendants().Where(e => e.Name.LocalName == "Style").ToList();

        var hidden = Assert.Single(
            styles,
            s =>
                Attr(s, "Selector").Contains("MessageActions", StringComparison.Ordinal)
                && !Attr(s, "Selector").Contains(":pointerover", StringComparison.Ordinal)
        );
        Assert.Contains(
            hidden.Descendants(),
            setter => Attr(setter, "Property") == "Opacity" && Attr(setter, "Value") == "0"
        );
        Assert.Contains(
            hidden.Descendants(),
            setter =>
                Attr(setter, "Property") == "IsHitTestVisible" && Attr(setter, "Value") == "False"
        );

        var shown = Assert.Single(
            styles,
            s =>
                Attr(s, "Selector").Contains("MessageActions", StringComparison.Ordinal)
                && Attr(s, "Selector").Contains(":pointerover", StringComparison.Ordinal)
        );
        Assert.Contains(
            shown.Descendants(),
            setter => Attr(setter, "Property") == "Opacity" && Attr(setter, "Value") == "1"
        );
    }

    [Fact]
    public void ActionBar_ExposesCopyEditRetryButtons()
    {
        var root = Load("src", "Desktop", "Controls", "MessageActionBar.axaml");
        var names = root.Descendants()
            .Where(e => e.Name.LocalName == "Button")
            .Select(e => Attr(e, "Name"))
            .ToList();

        Assert.Contains("CopyButton", names);
        Assert.Contains("EditButton", names);
        Assert.Contains("RetryButton", names);
    }

    private static XElement Template(string key)
    {
        var root = Load("src", "Desktop", "Views", "ConversationView.axaml");
        return root.Descendants()
            .Single(e => e.Name.LocalName == "DataTemplate" && Attr(e, "Key") == key);
    }

    private static XElement Load(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate))
                return XDocument.Load(candidate).Root!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未找到 {Path.Combine(parts)}");
    }

    private static string Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";
}
