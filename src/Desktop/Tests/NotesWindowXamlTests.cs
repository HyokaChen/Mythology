using System.Xml.Linq;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 笔记窗口结构锁（XAML 静态断言）：计数/空态/卡片列表、唯一 Primary、无本地视觉裸值、无 emoji。
/// </summary>
public sealed class NotesWindowXamlTests
{
    private static XElement Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "src",
                "Desktop",
                "Controls",
                "NotesWindow.axaml"
            );
            if (File.Exists(candidate))
                return XDocument.Load(candidate).Root!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("NotesWindow.axaml");
    }

    [Fact]
    public void HasCountEmptyStateAndCardList()
    {
        var root = Load();
        var names = root.Descendants()
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        Assert.Contains("CountNote", names);
        Assert.Contains("EmptyNote", names);
        Assert.Contains("NoteList", names);

        var cardBorders = root.Descendants()
            .Count(e => e.Name.LocalName == "Border" && e.Attribute("Classes")?.Value == "Card");
        Assert.True(cardBorders >= 2, "列表与详情都应包在 Card 里");
    }

    [Fact]
    public void ExactlyOnePrimaryButton()
    {
        var root = Load();
        var primaries = root.Descendants()
            .Where(e => e.Name.LocalName == "Button")
            .Where(e =>
                (e.Attribute("Classes")?.Value ?? "").Contains("Primary", StringComparison.Ordinal)
            )
            .ToList();

        Assert.Single(primaries);
        Assert.Equal(
            "EditButton",
            primaries[0].Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value
        );
    }

    [Fact]
    public void NoLocalBackgroundOrCornerRadiusOnControls()
    {
        var root = Load();
        // 窗口自身可用 BrushLinen 底色；其余控件只挂 Classes（本地值会压过 Adaline 样式）
        var offenders = root.Descendants()
            .Where(e => e.Name.LocalName != "Window")
            .Where(e =>
                e.Attribute("Background")?.Value is { } bg
                && !bg.StartsWith("{DynamicResource BrushSurfaceGlow}", StringComparison.Ordinal)
                && !bg.StartsWith("{DynamicResource BrushMist}", StringComparison.Ordinal)
                && !bg.StartsWith("Transparent", StringComparison.Ordinal)
            )
            .Select(e => e.Name.LocalName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"禁止本地 Background 裸值：{string.Join(", ", offenders)}"
        );
    }

    [Fact]
    public void TitleAndTexts_HaveNoEmoji()
    {
        var root = Load();
        var texts = root.Descendants()
            .Select(e => e.Attribute("Text")?.Value)
            .Where(t => !string.IsNullOrEmpty(t));

        var offenders = texts
            .Where(t =>
                t!
                    .EnumerateRunes()
                    .Any(r => r.Value >= 0x1F300 || (r.Value >= 0x2600 && r.Value <= 0x27BF))
            )
            .ToList();

        Assert.True(offenders.Count == 0, $"UI 文本不得含 emoji：{string.Join(" / ", offenders)}");
    }
}
