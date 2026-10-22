using System.Xml.Linq;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// Adaline 设计系统回归锁（DESIGN.md 对齐）。
///
/// 覆盖三类会静默失效的约定——它们都不会让构建失败，只会让界面悄悄偏离规范：
///
/// 1. **悬空资源键**：`DynamicResource` 指向不存在的键不会抛异常，只回退默认色。
///    历史 bug：SettingsView 上下文 Tab 写 `BrushSageMint`，而 token 名是 `BrushSageMist`。
/// 2. **控件主题跨类型误用**：`Theme` 指向 `TargetType="ComboBox"` 的 ControlTheme 用在
///    TextBox 上不生效，控件静默回退到 SukiUI 默认外观（圆角 + 蓝色焦点环）。
///    历史 bug：WorldRuleWindow / TreeWindow 的输入框挂了 `AdalineComboBox` 主题。
/// 3. **emoji 图标**：彩色位图字形与 Lucide 1px 描边体系（DESIGN.md §Imagery）冲突。
/// </summary>
public sealed class AdalineDesignSystemTests
{
    private static XElement Load(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, "src", "Desktop", .. segments]);
            if (File.Exists(candidate))
                return XDocument.Load(candidate).Root!;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未找到 src/Desktop/{Path.Combine(segments)}");
    }

    private static string Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    /// <summary>取出 axaml 中所有 DynamicResource / StaticResource 引用的键名。</summary>
    private static List<string> ResourceKeys(XElement root)
    {
        var keys = new List<string>();
        foreach (
            var attr in root.DescendantsAndSelf()
                .SelectMany(e => e.Attributes())
                .Where(a => a.Name.LocalName is "DynamicResource" or "StaticResource")
        )
        {
            // 取第一个非空 token：`{DynamicResource Key}` 或 `{Binding Path, Key}`
            var value = attr.Value;
            var start = value.IndexOf('{') + 1;
            if (start <= 0)
                continue;
            var end = value.IndexOf('}', start);
            if (end < 0)
                continue;
            var token = value[start..end].Split(',')[0].Trim();
            if (token.Length > 0)
                keys.Add(token);
        }
        return keys;
    }

    private static HashSet<string> DeclaredThemeKeys()
    {
        var theme = Load("Styles", "MythologyTheme.axaml");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var element in theme
                .Descendants()
                .Where(e => e.Attributes().Any(a => a.Name.LocalName == "Key"))
        )
        {
            var key = Attr(element, "Key");
            if (key.Length > 0)
                keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// 所有 axaml 引用的 key 必须在 MythologyTheme.axaml 中声明。
    /// DynamicResource 的失败是静默的（回退默认色），编译期不报，只能这样锁。
    /// </summary>
    [Theory]
    [InlineData("Views", "SettingsView.axaml")]
    [InlineData("Views", "MainWindow.axaml")]
    [InlineData("Views", "SetUpPanelView.axaml")]
    [InlineData("Views", "ConversationView.axaml")]
    [InlineData("Views", "TodoPanelView.axaml")]
    [InlineData("Controls", "ProjectPickerWindow.axaml")]
    [InlineData("Controls", "TreeWindow.axaml")]
    [InlineData("Controls", "WorldRulePanel.axaml")]
    [InlineData("Controls", "CloudLoginWindow.axaml")]
    [InlineData("Controls", "SetupCheckWindow.axaml")]
    [InlineData("Controls", "EntityFilterDialog.axaml")]
    [InlineData("Controls", "NotesWindow.axaml")]
    public void ResourceKeys_AreAllDeclaredInTheme(params string[] segments)
    {
        var declared = DeclaredThemeKeys();
        var missing = ResourceKeys(Load(segments))
            .Where(k => !declared.Contains(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{Path.Combine(segments)} 引用了未声明的 token：{string.Join(", ", missing)}"
        );
    }

    /// <summary>
    /// 样式文件自己引用的 key 也必须已声明（Global default ComboBox 主题依赖 AdalineComboBox）。
    /// </summary>
    [Fact]
    public void StyleFile_ResourceKeys_AreAllDeclaredInTheme()
    {
        var declared = DeclaredThemeKeys();
        var missing = ResourceKeys(Load("Styles", "MythologyStyles.axaml"))
            .Where(k => !declared.Contains(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"MythologyStyles.axaml 引用了未声明的 token：{string.Join(", ", missing)}"
        );
    }

    /// <summary>
    /// 多行输入必须顶对齐：Avalonia TextBox 默认 <c>VerticalContentAlignment=Center</c>——
    /// 撑满内容区的多行框（章节正文编辑器 / 长文本字段）会把文字**竖直居中**
    /// （用户报障：章节内容面板怎么可能是垂直居中）。类级兜底 + 两处整块正文编辑器不得回退；
    /// 正文另须两端对齐（不是居中展示的短文案）。
    /// </summary>
    [Fact]
    public void MultilineTextInputs_AreTopAligned_AndContentEditorsJustified()
    {
        // 1) 类级兜底：Adaline / PromptBox 样式块必须显式设置垂直内容对齐
        var styles = File.ReadAllText(
            Path.Combine(DesktopDir(), "Styles", "MythologyStyles.axaml")
        );
        foreach (var selector in new[] { "TextBox.Adaline", "TextBox.PromptBox" })
        {
            var start = styles.IndexOf($"Selector=\"{selector}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"样式缺失：{selector}");
            var end = styles.IndexOf("</Style>", start, StringComparison.Ordinal);
            Assert.Contains(
                "Property=\"VerticalContentAlignment\"",
                styles[start..end],
                StringComparison.Ordinal
            );
        }

        // 2) 章节阅读器编辑态：顶对齐 + 两端对齐
        var reader = Load("Controls", "ChapterReaderWindow.axaml");
        var editor = reader.Descendants().Single(e => Attr(e, "Name") == "ContentEditor");
        Assert.Equal("Top", Attr(editor, "VerticalContentAlignment"));
        Assert.Equal("Justify", Attr(editor, "TextAlignment"));

        // 3) 设定编辑窗的「内容」作用域整块编辑器（代码构建）：同样顶对齐 + 两端对齐
        var editWindow = File.ReadAllText(
            Path.Combine(DesktopDir(), "Controls", "EntityEditWindow.axaml.cs")
        );
        Assert.Contains(
            "VerticalContentAlignment = VerticalAlignment.Top",
            editWindow,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "TextAlignment = TextAlignment.Justify",
            editWindow,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// TextBox 不得挂 ComboBox 的 ControlTheme——类型不匹配时 Avalonia 静默忽略，
    /// 控件回退到主题默认外观（表现为「输入框样式怎么跟别处不一样」）。
    /// </summary>
    [Theory]
    [InlineData("Controls", "WorldRulePanel.axaml")]
    [InlineData("Controls", "TreeWindow.axaml")]
    [InlineData("Views", "SettingsView.axaml")]
    [InlineData("Views", "SetUpPanelView.axaml")]
    [InlineData("Controls", "NotesWindow.axaml")]
    public void TextBox_NeverUsesComboBoxControlTheme(params string[] segments)
    {
        var offenders = Load(segments)
            .Descendants()
            .Where(e => e.Name.LocalName == "TextBox")
            .Select(e => Attr(e, "Theme"))
            .Where(t => t.Contains("ComboBox", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{Path.Combine(segments)} 中 TextBox 误用了 ComboBox 主题（应用 Classes=\"Adaline\"）"
        );
    }

    /// <summary>
    /// 全部 axaml 可见文本里不得出现 emoji：彩色位图字形与 Lucide 1px 描边体系冲突
    /// （DESIGN.md §Imagery）。用 lucide:LucideIcon 替代。
    /// 只扫 UI chrome 元素——会话内容里的 emoji（StreamEventHandler / SessionMessageMapper
    /// 注入的模型输出）属于用户数据，不属于设计体系管辖范围。
    /// </summary>
    [Fact]
    public void AllAxaml_ContainsNoEmoji()
    {
        var offenders = new List<string>();
        foreach (
            var path in Directory.EnumerateFiles(
                DesktopDir(),
                "*.axaml",
                SearchOption.AllDirectories
            )
        )
        {
            foreach (
                var element in XDocument
                    .Load(path)
                    .Root!.DescendantsAndSelf()
                    .Where(e =>
                        e.Name.LocalName is "TextBlock" or "Button" or "MenuItem" or "CheckBox"
                    )
            )
            {
                foreach (
                    var attr in element
                        .Attributes()
                        .Where(a => a.Name.LocalName is "Text" or "Header" or "Content" or "Title")
                )
                {
                    if (attr.Value.Any(char.IsSurrogate))
                        offenders.Add(
                            $"{Path.GetFileName(path)}:{element.Name.LocalName}.{attr.Name.LocalName}={attr.Value}"
                        );
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"axaml 仍含 emoji（改用 lucide:LucideIcon）：{string.Join(" | ", offenders)}"
        );
    }

    /// <summary>
    /// 窗口标题栏不得含 emoji——原生标题栏渲染 emoji 会与衬线/等宽体系割裂。
    /// </summary>
    [Fact]
    public void WindowTitles_ContainNoEmoji()
    {
        var offenders = new List<string>();
        foreach (
            var path in Directory.EnumerateFiles(
                DesktopDir(),
                "*.axaml",
                SearchOption.AllDirectories
            )
        )
        {
            foreach (var window in XDocument.Load(path).Root?.DescendantsAndSelf() ?? [])
            {
                if (window.Name.LocalName != "Window")
                    continue;
                var title = Attr(window, "Title");
                if (title.Any(char.IsSurrogate))
                    offenders.Add($"{Path.GetFileName(path)}: Title=\"{title}\"");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(" | ", offenders));
    }

    /// <summary>
    /// 卡片圆角走 token，不写裸数字：裸值会与 RadiusCards / RadiusCardsSm 漂移。
    /// 只查 Border——ProgressBar 等细条的圆角由自身高度决定（= 高度/2 的胶囊形），
    /// 不属于卡片 token 的适用范围。
    /// </summary>
    [Theory]
    [InlineData("Views", "SetUpPanelView.axaml")]
    [InlineData("Views", "TodoPanelView.axaml")]
    [InlineData("Views", "ConversationView.axaml")]
    [InlineData("Controls", "ProjectPickerWindow.axaml")]
    [InlineData("Controls", "SessionManagerWindow.axaml")]
    [InlineData("Controls", "SetupCheckWindow.axaml")]
    public void CornerRadius_UsesTokensNotLiteralNumbers(params string[] segments)
    {
        var offenders = Load(segments)
            .Descendants()
            .Where(e => e.Name.LocalName == "Border")
            .Select(e => Attr(e, "CornerRadius"))
            // 仅裸数字是问题；{StaticResource Token} 正是期望写法
            .Where(v => v.Length > 0 && !v.StartsWith('{'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{Path.Combine(segments)} 的 CornerRadius 未走 token：{string.Join(", ", offenders)}"
        );
    }

    private static string DesktopDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Desktop");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("未找到 src/Desktop");
    }
}
