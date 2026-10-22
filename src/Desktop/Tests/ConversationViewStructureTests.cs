using System.Xml.Linq;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 对话输入区弹窗 XAML 结构回归锁（@ / # 补全弹窗必须出现、必须与输入卡片等宽、必须双向同步）。
///
/// **历史 bug**：`IsOpen` 绑定为默认 OneWay → light-dismiss 关闭后 VM 残留 true，之后输入再也打不开弹窗；
/// 弹窗用 `MinWidth/MaxWidth` 内容自适应 → 与输入框不等宽。
/// 运行期（布局期 bounds）断言在进程内无头 Avalonia 下不稳定（实测会挂起），
/// 故用结构锁 + 应用内探针冒烟覆盖运行期。
/// </summary>
public sealed class ConversationViewStructureTests
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

    private static XElement Popup(XElement root, string name) =>
        root.Descendants()
            .Single(e =>
                e.Name.LocalName == "Popup"
                && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name)
            );

    private static XElement Child(XElement popup) => popup.Elements().Single();

    private static string Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    [Theory]
    [InlineData("SuggestionPopup")]
    [InlineData("CommandPopup")]
    public void Popup_AnchorsToInputCard_AndMatchesItsWidth(string popupName)
    {
        var popup = Popup(LoadConversationView(), popupName);

        Assert.Equal("TopEdgeAlignedLeft", Attr(popup, "Placement"));
        Assert.Contains(
            "ElementName=InputCard",
            Attr(popup, "PlacementTarget"),
            StringComparison.Ordinal
        );
        Assert.Contains("Bounds.Width", Attr(popup, "Width"), StringComparison.Ordinal);
        Assert.Contains("ElementName=InputCard", Attr(popup, "Width"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SuggestionPopup")]
    [InlineData("CommandPopup")]
    public void Popup_IsOpenBinding_IsTwoWay(string popupName)
    {
        var popup = Popup(LoadConversationView(), popupName);
        Assert.Contains("Mode=TwoWay", Attr(popup, "IsOpen"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SuggestionPopup")]
    [InlineData("CommandPopup")]
    public void Popup_Child_HasNoFixedWidthConstraints(string popupName)
    {
        var child = Child(Popup(LoadConversationView(), popupName));
        Assert.Equal("Border", child.Name.LocalName);
        Assert.DoesNotContain(
            child.Attributes(),
            a => a.Name.LocalName is "MinWidth" or "MaxWidth"
        );
    }
}
