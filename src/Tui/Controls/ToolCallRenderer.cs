using System.Text;
using Core.Shared;
using Core.Tools;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Tui.Controls;

/// <summary>
/// 工具调用渲染器 — 渲染工具调用的 UI 表示。
/// </summary>
public static class ToolCallRenderer
{
    // ── Desert 暗色主题颜色常量 ──
    /// <summary>按背景亮度判断深浅主题（不能按 Desert 夜空色值比较，其他主题背景不同）。</summary>
    private static bool IsDarkTheme(PanelColors colors) =>
        Services.ColorContrast.Luminance(colors.ConversationBg) < 0.5;

    private static readonly Color ToolIconColor = Color.Rgb(0xD6, 0x8A, 0x48);

    /// <summary>工具名称色（月光）。</summary>
    private static readonly Color ToolNameColor = Color.Rgb(0xC7, 0xB2, 0x99);

    /// <summary>工具副标题色（金色）。</summary>
    private static readonly Color ToolSubtitleColor = Color.Rgb(0xFA, 0xBD, 0x2F);

    /// <summary>工具参数色（暗月光）。</summary>
    private static readonly Color ToolParamColor = Color.Rgb(0x8F, 0x82, 0x70);

    /// <summary>成功状态色（鼠尾草绿）。</summary>
    private static readonly Color ToolSuccessColor = Color.Rgb(0x8E, 0xC0, 0x7C);

    /// <summary>错误状态色（赤陶红）。</summary>
    private static readonly Color ToolErrorColor = Color.Rgb(0xFB, 0x49, 0x34);

    // ── 浅色主题颜色常量 ──
    /// <summary>工具图标色（浅色主题）。</summary>
    private static readonly Color ToolIconColorLight = Color.Rgb(0xAF, 0x60, 0x14);

    /// <summary>工具名称色（浅色主题）。</summary>
    private static readonly Color ToolNameColorLight = Color.Rgb(0x3C, 0x38, 0x36);

    /// <summary>工具副标题色（浅色主题）。</summary>
    private static readonly Color ToolSubtitleColorLight = Color.Rgb(0xAF, 0x87, 0x14);

    /// <summary>工具参数色（浅色主题）。</summary>
    private static readonly Color ToolParamColorLight = Color.Rgb(0x9A, 0x8C, 0x7A);

    /// <summary>成功状态色（浅色主题）。</summary>
    private static readonly Color ToolSuccessColorLight = Color.Rgb(0x4E, 0x7E, 0x3E);

    /// <summary>错误状态色（浅色主题）。</summary>
    private static readonly Color ToolErrorColorLight = Color.Rgb(0xCC, 0x24, 0x1D);

    // ── Diff 颜色（Desert 主题） ──
    /// <summary>删除行颜色（赤陶红）。</summary>
    private const string DiffDeleteColor = "#fb4934";

    /// <summary>删除行暗色。</summary>
    private const string DiffDeleteDimColor = "#cc241d";

    /// <summary>插入行颜色（鼠尾草绿）。</summary>
    private const string DiffInsertColor = "#8ec07c";

    /// <summary>插入行暗色。</summary>
    private const string DiffInsertDimColor = "#4e7e3e";

    /// <summary>上下文行颜色（月光灰）。</summary>
    private const string DiffContextColor = "#bdae93";

    /// <summary>
    /// 构建工具调用气泡项（运行中状态）。
    /// 包含工具名称、副标题、参数和状态图标（🔄/✅/❌）。
    /// </summary>
    /// <param name="toolName">工具名称。</param>
    /// <param name="subtitle">副标题（从参数中提取的语义摘要）。</param>
    /// <param name="paramDisplay">参数显示文本。</param>
    /// <param name="isRunning">是否正在运行。</param>
    /// <param name="isError">是否出错。</param>
    /// <param name="colors">面板颜色配置。</param>
    /// <param name="argsJson">工具调用参数 JSON（可选，用于 ask_user 渲染）。</param>
    public static DocumentFlowItem BuildToolCallItem(
        string toolName,
        string subtitle,
        string paramDisplay,
        bool isRunning,
        bool isError,
        PanelColors colors,
        string? argsJson = null
    )
    {
        var isDark = IsDarkTheme(colors);
        var markupText = BuildToolHeaderMarkup(
            toolName,
            subtitle,
            paramDisplay,
            isRunning,
            isError,
            isDark
        );

        var markup = new Markup(markupText).Wrap(true);
        AttachCopyHandlers(markup);

        var doc = new FlowDocument().Add(markup);

        // For ask_user while running: render Q&A card with all options as hollow circles (○)
        if (toolName == "ask_user" && isRunning && !string.IsNullOrEmpty(argsJson))
            TryRenderAskUserResult(doc, argsJson, result: "", colors);

        return new DocumentFlowItem
        {
            Content = doc,
            Alignment = DocumentFlowAlignment.Stretch,
            BackgroundStyle = Style
                .None.WithBackground(colors.ToolBubbleBg)
                .WithForeground(colors.ToolBubbleFg),
            Padding = new Thickness(3, 1, 3, 1),
        };
    }

    /// <summary>
    /// 构建工具调用结果气泡项。
    /// 按优先级尝试：ask_user Q&A 卡片 → todo 摘要 → 变体探索 → 数据驱动 diff → 分类内容渲染。
    /// </summary>
    /// <param name="category">工具分类。</param>
    /// <param name="entityType">实体类型（可选）。</param>
    /// <param name="toolName">工具名称。</param>
    /// <param name="subtitle">副标题。</param>
    /// <param name="paramDisplay">参数显示文本。</param>
    /// <param name="isError">是否出错。</param>
    /// <param name="result">工具返回结果文本。</param>
    /// <param name="colors">面板颜色配置。</param>
    /// <param name="onNavigateToEntity">导航到实体的回调（可选）。</param>
    /// <param name="details">结构化详情对象（IDiffDetails/ExploreDetails，可选）。</param>
    /// <param name="argsJson">工具调用参数 JSON（可选）。</param>
    public static DocumentFlowItem BuildToolCallResultItem(
        ToolCategory category,
        string? entityType,
        string toolName,
        string subtitle,
        string paramDisplay,
        bool isError,
        string result,
        PanelColors colors,
        Action<string, string>? onNavigateToEntity = null,
        object? details = null,
        string? argsJson = null
    )
    {
        var headerItem = BuildToolCallItem(
            toolName,
            subtitle,
            paramDisplay,
            isRunning: false,
            isError,
            colors
        );
        var headerDoc = (FlowDocument)headerItem.Content;

        if (isError)
        {
            var errorMarkup = $"[red]{Escape(result)}[/]";
            headerDoc.Add(
                new Padder
                {
                    Content = new Markup(errorMarkup).Wrap(true),
                    Padding = new Thickness(2, 0, 0, 0),
                }
            );
        }
        else
        {
            // Try ask_user specific rendering first
            var askUserRendered =
                toolName == "ask_user"
                && TryRenderAskUserResult(headerDoc, argsJson, result, colors);

            if (!askUserRendered)
            {
                // Try todo_write list summary（流内一行摘要；完整实时视图在 TodoPanel）
                var todoRendered =
                    toolName == "todo_write" && TryRenderTodoSummary(headerDoc, argsJson, result);

                if (!todoRendered)
                {
                    // Try explore variant rendering
                    var exploreRendered = TryRenderExploreVariants(headerDoc, details, colors);

                    if (!exploreRendered)
                    {
                        // Try data-driven diff rendering
                        var diffRendered = TryRenderDataDrivenDiff(headerDoc, details);

                        if (!diffRendered)
                        {
                            RenderCategoryContent(
                                headerDoc,
                                category,
                                entityType,
                                result,
                                colors,
                                onNavigateToEntity
                            );
                        }
                    }
                }
            }
        }

        return new DocumentFlowItem
        {
            Content = headerDoc,
            Alignment = DocumentFlowAlignment.Stretch,
            BackgroundStyle = Style
                .None.WithBackground(colors.ToolBubbleBg)
                .WithForeground(colors.ToolBubbleFg),
            Padding = new Thickness(3, 1, 3, 1),
        };
    }

    /// <summary>
    /// Renders ask_user tool result as a structured Q&A card.
    /// Parses questions + options from argsJson, and selected answers from result text.
    /// Shows each question with its options, highlighting the selected answer.
    /// </summary>
    private static bool TryRenderAskUserResult(
        FlowDocument doc,
        string? argsJson,
        string result,
        PanelColors colors
    )
    {
        // Parse questions from args
        List<(string header, string question, string[] options, bool multiSelect)> questions;
        try
        {
            if (string.IsNullOrWhiteSpace(argsJson))
                return false;

            using var argsDoc = System.Text.Json.JsonDocument.Parse(argsJson);
            if (!argsDoc.RootElement.TryGetProperty("questions", out var questionsArr))
                return false;
            if (questionsArr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return false;

            questions = [];
            foreach (var q in questionsArr.EnumerateArray())
            {
                var header = q.TryGetProperty("header", out var h) ? h.GetString() ?? "" : "";
                var question = q.TryGetProperty("question", out var qu) ? qu.GetString() ?? "" : "";
                var multiSelect = q.TryGetProperty("multi_select", out var ms) && ms.GetBoolean();
                string[] options = [];
                if (
                    q.TryGetProperty("options", out var opts)
                    && opts.ValueKind == System.Text.Json.JsonValueKind.Array
                )
                    options = opts.EnumerateArray().Select(o => o.GetString() ?? "").ToArray();
                questions.Add((header, question, options, multiSelect));
            }
        }
        catch
        {
            return false;
        }

        if (questions.Count == 0)
            return false;

        // Parse selected answers from result text
        // Format: "用户回复:\n  [Header] Question\n    → Answer1, Answer2"
        var selectedAnswers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in result.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('→') || trimmed.StartsWith("->"))
                {
                    // This is an answer line — associate with the most recent header
                    var answerText = trimmed[1..].Trim();
                    // Find which question this belongs to by position
                    var currentIdx = selectedAnswers.Count;
                    if (currentIdx < questions.Count)
                    {
                        var answers = answerText
                            .Split([", ", "、"], StringSplitOptions.None)
                            .Select(a => a.Trim())
                            .ToArray();
                        selectedAnswers[questions[currentIdx].header] = answers;
                    }
                }
            }
        }
        catch
        { /* best effort parsing */
        }

        // Build the Q&A card markup
        var isDark = IsDarkTheme(colors);
        var questionColor = isDark ? ToolSubtitleColor : ToolSubtitleColorLight;
        var answerColor = isDark ? ToolSuccessColor : ToolSuccessColorLight;
        var dimColor = isDark ? ToolParamColor : ToolParamColorLight;

        var sb = new StringBuilder();

        foreach (var (header, question, options, _) in questions)
        {
            sb.Append(
                $"[bold #{questionColor.R:x2}{questionColor.G:x2}{questionColor.B:x2}]{Escape(header)}[/]"
            );
            if (!string.IsNullOrEmpty(question))
                sb.Append($": {Escape(question)}");
            sb.Append('\n');

            // Options with selection markers
            var selected = selectedAnswers.GetValueOrDefault(header, []);
            var selectedSet = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);

            foreach (var option in options)
            {
                var isSelected = selectedSet.Contains(option);
                if (isSelected)
                {
                    sb.Append(
                        $"  [#{answerColor.R:x2}{answerColor.G:x2}{answerColor.B:x2}]● {Escape(option)}[/]\n"
                    );
                }
                else
                {
                    sb.Append(
                        $"  [#{dimColor.R:x2}{dimColor.G:x2}{dimColor.B:x2}]○ {Escape(option)}[/]\n"
                    );
                }
            }

            // If no options were in args but we have answers, show them directly
            if (options.Length == 0 && selected.Length > 0)
            {
                sb.Append(
                    $"  [#{answerColor.R:x2}{answerColor.G:x2}{answerColor.B:x2}]→ {Escape(string.Join(", ", selected))}[/]\n"
                );
            }

            // Custom typed answer (not in options list)
            if (selected.Length > 0 && options.Length > 0)
            {
                var customAnswers = selected
                    .Where(s => !options.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (customAnswers.Count > 0)
                {
                    sb.Append(
                        $"  [#{answerColor.R:x2}{answerColor.G:x2}{answerColor.B:x2}]● {Escape(string.Join(", ", customAnswers))}[/] [dim italic](自定义)[/]\n"
                    );
                }
            }
        }

        var markup = new Markup(sb.ToString()).Wrap(true);
        AttachCopyHandlers(markup);
        doc.Add(new Padder { Content = markup, Padding = new Thickness(2, 0, 0, 0) });
        return true;
    }

    /// <summary>
    /// Renders todo_write list result as a one-line summary (📋 任务 x/y · 🔄 …),
    /// so the conversation flow keeps a compact snapshot while TodoPanel is the
    /// authoritative live view. Non-list actions fall through to plain text.
    /// </summary>
    private static bool TryRenderTodoSummary(FlowDocument doc, string? argsJson, string result)
    {
        // 仅 list 动作产生有意义的摘要；create/update/delete 结果是一行文本，直接原样呈现
        string action;
        try
        {
            if (string.IsNullOrWhiteSpace(argsJson))
                return false;
            using var argsDoc = System.Text.Json.JsonDocument.Parse(argsJson);
            if (!argsDoc.RootElement.TryGetProperty("action", out var actionProp))
                return false;
            action = actionProp.GetString() ?? "";
        }
        catch
        {
            return false;
        }

        if (!string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(result))
            return false;

        // 空列表：无图标行可统计，直接渲染空态摘要（与 TodoPanel 空态文案一致）
        if (result.Trim() == "(无任务)")
        {
            doc.Add(
                new Padder
                {
                    Content = new Markup("[dim]📋 暂无任务[/]").Wrap(true),
                    Padding = new Thickness(2, 0, 0, 0),
                }
            );
            return true;
        }

        var lines = result.Split('\n');
        string[] icons = ["⬜", "🔄", "✅", "❌"];
        var total = lines.Count(l =>
        {
            var t = l.TrimStart();
            return icons.Any(t.StartsWith);
        });
        if (total == 0)
            return false;

        var done = lines.Count(l => l.TrimStart().StartsWith('✅'));
        var inProgress = lines.FirstOrDefault(l => l.TrimStart().StartsWith("🔄"));
        var summary = inProgress is not null
            ? $"📋 任务 {done}/{total} · {inProgress.Trim()}"
            : $"📋 任务 {done}/{total}";

        doc.Add(
            new Padder
            {
                Content = new Markup($"[bold]{summary}[/]").Wrap(true),
                Padding = new Thickness(2, 0, 0, 0),
            }
        );
        return true;
    }

    /// <summary>
    /// Attempts to render an explore variants comparison view from structured Details.
    /// Detects ExploreDetails via pattern matching and renders a multi-column
    /// comparison card with preview content and selection hints.
    /// Returns true if variant data was found and rendered; false otherwise.
    /// </summary>
    private static bool TryRenderExploreVariants(
        FlowDocument doc,
        object? details,
        PanelColors colors
    )
    {
        if (details is not ExploreDetails explore)
            return false;

        var variants = explore.Variants;
        if (variants.Count == 0)
            return false;

        var isDark = IsDarkTheme(colors);
        var labelColor = isDark ? ToolSubtitleColor : ToolSubtitleColorLight;
        var dimColor = isDark ? ToolParamColor : ToolParamColorLight;

        var sb = new StringBuilder();

        // Header
        sb.Append($"[bold #{labelColor.R:x2}{labelColor.G:x2}{labelColor.B:x2}]🔍 探索结果[/] ");
        sb.Append($"[dim]— {variants.Count} 个变体[/]\n");

        if (explore.ApplyTarget is { } target)
            sb.Append($"[dim]目标: 第{target.Order}章「{Escape(target.Title)}」[/]\n");

        sb.Append('\n');

        // Render each variant as a numbered card
        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants[i];
            var idx = i + 1;
            var label = string.IsNullOrEmpty(variant.Label) ? $"变体{idx}" : variant.Label;
            var content = variant.Content;

            // Variant header: "1. Label"
            sb.Append(
                $"[bold #{labelColor.R:x2}{labelColor.G:x2}{labelColor.B:x2}]{idx}. {Escape(label)}[/]"
            );
            var charCount = content.Length;
            sb.Append($" [dim]{charCount} 字[/]");
            if (!string.IsNullOrEmpty(variant.Description))
                sb.Append($" [dim]— {Escape(variant.Description)}[/]");
            sb.Append('\n');

            // Content preview (first 3 lines, max 200 chars)
            var preview = content.Trim();
            if (preview.Length > 200)
                preview = preview[..197] + "…";
            var lines = preview.Split('\n');
            var previewLines = lines.Take(3).ToList();
            if (lines.Length > 3)
                previewLines.Add("…");

            foreach (var line in previewLines)
            {
                sb.Append($"  [#{dimColor.R:x2}{dimColor.G:x2}{dimColor.B:x2}]{Escape(line)}[/]\n");
            }

            sb.Append('\n');
        }

        // Selection hints
        sb.Append("[dim]Enter 查看变体  [bold]Esc[/] 稍后决定[/]\n");

        var markup = new Markup(sb.ToString()).Wrap(true);
        AttachCopyHandlers(markup);
        doc.Add(new Padder { Content = markup, Padding = new Thickness(2, 0, 0, 0) });
        return true;
    }

    /// <summary>
    /// Attempts to render a diff view from structured Details using Markup.
    /// Returns true if diff data was found and rendered; false otherwise.
    /// </summary>
    private static bool TryRenderDataDrivenDiff(FlowDocument doc, object? details)
    {
        if (details is null)
            return false;

        // Pattern match on IDiffDetails — no reflection needed
        EditHunk[]? hunks = null;
        string? filePath = null;

        if (details is IDiffDetails { Hunks.Length: > 0 } diff)
        {
            hunks = diff.Hunks;
            filePath = diff.Path;
        }

        if (hunks is null)
            return false;

        // Build diff lines from hunks

        List<DiffLine> allDiffLines = [];
        foreach (var hunk in hunks)
        {
            var hunkDiff = DiffComputer.Compute(hunk.OldText, hunk.NewText);
            if (allDiffLines.Count > 0 && hunkDiff.Count > 0)
            {
                allDiffLines.Add(new DiffLine(DiffLineType.Context, null, null, "···"));
            }
            allDiffLines.AddRange(hunkDiff);
        }
        var (addedTotal, removedTotal) = DiffComputer.CountChanges(allDiffLines);

        if (allDiffLines.Count == 0)
            return false;

        // ── Build markup text for diff ──
        var sb = new StringBuilder(allDiffLines.Count * 60);

        // Title: filename + stats
        if (filePath is not null)
        {
            var fileName = Path.GetFileName(filePath);
            sb.Append(
                $"[bold #{ToolSubtitleColor.R:x2}{ToolSubtitleColor.G:x2}{ToolSubtitleColor.B:x2}]📄 {Escape(fileName)}[/] [dim](+{addedTotal} -{removedTotal})[/]\n"
            );
        }

        // Apply GitHub-style context folding: keep 5 lines of context around each change,
        // skip everything else. This ensures all diff hunks are visible in the TUI.
        var foldedLines = DiffComputer.FoldContext(allDiffLines, contextLines: 5);

        // Diff lines with colored +/- prefix and line numbers
        foreach (var line in foldedLines)
        {
            var escapedText = Escape(line.Text);

            switch (line.Type)
            {
                case DiffLineType.Delete:
                {
                    var lineNum = line.OldLineNum?.ToString().PadLeft(3);
                    sb.Append(
                        $"[{DiffDeleteColor}]-{lineNum}[/] [{DiffDeleteDimColor}]{escapedText}[/]\n"
                    );
                    break;
                }
                case DiffLineType.Insert:
                {
                    var lineNum = line.NewLineNum?.ToString().PadLeft(3);
                    sb.Append(
                        $"[{DiffInsertColor}]+{lineNum}[/] [{DiffInsertDimColor}]{escapedText}[/]\n"
                    );
                    break;
                }
                case DiffLineType.Context:
                {
                    // Skip marker (⋯ N lines skipped ⋯) gets a dim centered style
                    if (line.Text.StartsWith('⋯'))
                    {
                        sb.Append($"[dim]  {escapedText}[/]\n");
                    }
                    else
                    {
                        var oldNum = line.OldLineNum?.ToString().PadLeft(3);
                        sb.Append(
                            $"[{DiffContextColor}] {oldNum}[/]  [{DiffContextColor}]{escapedText}[/]\n"
                        );
                    }
                    break;
                }
            }
        }

        var diffMarkup = new Markup(sb.ToString()).Wrap(true);
        AttachCopyHandlers(diffMarkup);

        doc.Add(new Padder { Content = diffMarkup, Padding = new Thickness(2, 0, 0, 0) });
        return true;
    }

    /// <summary>
    /// Renders content based on ToolCategory when no data-driven diff is available.
    /// </summary>
    private static void RenderCategoryContent(
        FlowDocument headerDoc,
        ToolCategory category,
        string? entityType,
        string result,
        PanelColors colors,
        Action<string, string>? onNavigateToEntity
    )
    {
        switch (category)
        {
            case ToolCategory.FileEdit:
            {
                // Fallback for FileEdit without structured diff data — plain text with ✅/❌ highlights
                var sb = new StringBuilder();
                foreach (var line in result.Split('\n').Take(20))
                {
                    var escaped = Escape(line);
                    if (line.TrimStart().StartsWith('✅'))
                        sb.Append($"[{DiffInsertColor}]{escaped}[/]\n");
                    else if (line.TrimStart().StartsWith('❌'))
                        sb.Append($"[{DiffDeleteColor}]{escaped}[/]\n");
                    else
                        sb.Append($"[dim]{escaped}[/]\n");
                }

                var markup = new Markup(sb.ToString()).Wrap(true);
                AttachCopyHandlers(markup);
                headerDoc.Add(new Padder { Content = markup, Padding = new Thickness(2, 0, 0, 0) });
                break;
            }
            case ToolCategory.BashTool:
            {
                var sb = new StringBuilder();
                foreach (var line in result.Split('\n').Take(20))
                    sb.Append($"[dim]{Escape(line)}[/]\n");
                var markup = new Markup(sb.ToString()).Wrap(true);
                AttachCopyHandlers(markup);
                headerDoc.Add(new Padder { Content = markup, Padding = new Thickness(2, 0, 0, 0) });
                break;
            }
            case ToolCategory.EntityCrud:
            {
                var sb = new StringBuilder();
                foreach (var line in result.Split('\n').Take(15))
                {
                    if (line.Contains('→'))
                    {
                        var arrowIdx = line.IndexOf('→');
                        var before = line[..arrowIdx].TrimEnd();
                        var after = line[(arrowIdx + 1)..].TrimStart();
                        sb.Append(
                            $"[dim]{Escape(before)}[/] [yellow]→[/] [green]{Escape(after)}[/]\n"
                        );
                    }
                    else if (
                        line.Contains("已创建")
                        || line.Contains("已删除")
                        || line.Contains("新建")
                    )
                        sb.Append($"[bold]{Escape(line)}[/]\n");
                    else
                        sb.Append($"[dim]{Escape(line)}[/]\n");
                }

                var logMarkup = new Markup(sb.ToString()).Wrap(true);
                AttachCopyHandlers(logMarkup);

                if (onNavigateToEntity is not null && entityType is not null)
                {
                    var entityId = TryExtractEntityId(result);
                    if (entityId is not null)
                    {
                        var navBtn = new Button(new Markup("📋 查看设定")).Style(
                            ThemeStyles.BuildButtonAccent(PanelThemes.Current)
                        );
                        var capturedEntityType = entityType;
                        var capturedEntityId = entityId;
                        navBtn.Click(() =>
                            onNavigateToEntity(capturedEntityType, capturedEntityId)
                        );
                        headerDoc.Add(
                            new Padder { Content = logMarkup, Padding = new Thickness(2, 0, 0, 0) }
                        );
                        headerDoc.Add(
                            new Padder { Content = navBtn, Padding = new Thickness(4, 0, 0, 0) }
                        );
                    }
                    else
                    {
                        headerDoc.Add(
                            new Padder { Content = logMarkup, Padding = new Thickness(2, 0, 0, 0) }
                        );
                    }
                }
                else
                {
                    headerDoc.Add(
                        new Padder { Content = logMarkup, Padding = new Thickness(2, 0, 0, 0) }
                    );
                }
                break;
            }
            case ToolCategory.ChapterManagement:
            {
                var summary = Truncate(result, 200);
                var paramColor = IsDarkTheme(colors) // 深浅主题
                    ? ToolParamColor
                    : ToolParamColorLight;
                var summaryMarkup =
                    $"[{paramColor.R:x2}{paramColor.G:x2}{paramColor.B:x2}]{Escape(summary)}[/]";
                var capMarkup = new Markup(summaryMarkup).Wrap(true);
                AttachCopyHandlers(capMarkup);
                headerDoc.Add(capMarkup);
                break;
            }
            case ToolCategory.EntityRead:
            {
                var sb = new StringBuilder();
                foreach (var line in Truncate(result, 300).Split('\n').Take(10))
                    sb.Append($"[dim]{Escape(line)}[/]\n");
                var markup = new Markup(sb.ToString()).Wrap(true);
                AttachCopyHandlers(markup);
                headerDoc.Add(new Padder { Content = markup, Padding = new Thickness(2, 0, 0, 0) });
                break;
            }
            case ToolCategory.SearchTool:
            {
                var sb = new StringBuilder();
                foreach (var line in result.Split('\n').Take(15))
                {
                    if (line.StartsWith('-') || line.StartsWith('['))
                        sb.Append($"[dim]{Escape(line)}[/]\n");
                    else if (line.Contains("http") || line.Contains("score"))
                        sb.Append($"[steelblue]{Escape(line)}[/]\n");
                    else
                        sb.Append($"[dim]{Escape(line)}[/]\n");
                }
                var markup = new Markup(sb.ToString()).Wrap(true);
                AttachCopyHandlers(markup);
                headerDoc.Add(new Padder { Content = markup, Padding = new Thickness(2, 0, 0, 0) });
                break;
            }
        }
    }

    /// <summary>
    /// 解析工具参数 JSON，提取副标题和内联参数显示。
    /// 副标题按 ToolParameterKeys.SubtitlePriority 优先级提取；
    /// 内联参数跳过 SubtitlePriority 和 Skip 集合中的键，最多显示 3 个。
    /// </summary>
    /// <param name="argsJson">工具参数 JSON 字符串。</param>
    /// <returns>(subtitle, inlineParams) 元组。</returns>
    public static (string subtitle, string inlineParams) ParseToolArgs(string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return ("", "");

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (Truncate(argsJson, 60), "");

            var allProps = doc.RootElement.EnumerateObject().ToList();

            var subtitle = "";
            var subtitleKey = "";
            foreach (var key in ToolParameterKeys.SubtitlePriority)
            {
                var found = allProps.FindIndex(p => p.Name == key);
                if (found >= 0)
                {
                    subtitle = ExtractValue(allProps[found].Value);
                    if (subtitle.Length > 50)
                        subtitle = subtitle[..47] + "…";
                    subtitleKey = key;
                    break;
                }
            }

            var parts = new List<string>();
            foreach (var prop in allProps)
            {
                if (parts.Count >= 3)
                    break;
                if (prop.Name == subtitleKey || ToolParameterKeys.Skip.Contains(prop.Name))
                    continue;

                var val = ExtractValue(prop.Value);
                if (val.Length > 30)
                    val = val[..27] + "…";
                parts.Add($"{prop.Name}={val}");
            }

            return (subtitle, string.Join("  ", parts));
        }
        catch
        {
            return (Truncate(argsJson, 60), "");
        }
    }

    /// <summary>
    /// 从 JsonElement 提取值的简短表示。
    /// 字符串/数字/布尔直接返回，数组/对象递归摘要。
    /// </summary>
    private static string ExtractValue(System.Text.Json.JsonElement value, int depth = 0) =>
        value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => value.GetRawText(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            System.Text.Json.JsonValueKind.Null => "null",
            System.Text.Json.JsonValueKind.Array => SummarizeArray(value, depth),
            System.Text.Json.JsonValueKind.Object => SummarizeObject(value, depth),
            _ => Truncate(value.GetRawText(), 20),
        };

    /// <summary>
    /// 智能摘要 JSON 数组：提取前 N 个元素的简短表示。
    /// 对于对象数组，优先提取 header/name/title 等语义字段。
    /// </summary>
    private static string SummarizeArray(System.Text.Json.JsonElement array, int depth)
    {
        var count = array.GetArrayLength();
        if (count == 0)
            return "[]";

        // 深度限制：避免嵌套过深时递归展开
        if (depth >= 2)
            return $"[{count} items]";

        // 取前 3 个元素做摘要
        var items = array
            .EnumerateArray()
            .Take(3)
            .Select(e => ExtractValue(e, depth + 1))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (items.Count == 0)
            return $"[{count}]";

        var summary = string.Join(", ", items);
        if (count > 3)
            summary += ", …";
        return count <= 3 ? summary : $"{count}: {summary}";
    }

    /// <summary>
    /// 智能摘要 JSON 对象：提取最有语义的 key-value 对。
    /// 优先提取 header/name/title/question 等字段。
    /// </summary>
    private static string SummarizeObject(System.Text.Json.JsonElement obj, int depth)
    {
        // 深度限制
        if (depth >= 2)
            return "{…}";

        // 优先提取的语义字段（按重要性排序）
        var priorityKeys = new[]
        {
            "header",
            "name",
            "title",
            "question",
            "description",
            "path",
            "query",
            "command",
            "url",
            "type",
            "action",
        };

        foreach (var key in priorityKeys)
        {
            if (obj.TryGetProperty(key, out var prop))
            {
                var val = ExtractValue(prop, depth + 1);
                if (!string.IsNullOrEmpty(val))
                    return val.Length > 40 ? val[..37] + "…" : val;
            }
        }

        // 回退：取第一个属性
        var firstProp = obj.EnumerateObject().FirstOrDefault();
        if (firstProp.Value.ValueKind != System.Text.Json.JsonValueKind.Undefined)
        {
            var val = ExtractValue(firstProp.Value, depth + 1);
            if (!string.IsNullOrEmpty(val))
                return $"{firstProp.Name}={val}";
        }

        return "{…}";
    }

    /// <summary>
    /// 构建工具头部 Markup 文本（图标 + 名称 + 副标题 + 参数 + 状态）。
    /// 使用 ANSI 颜色标签，深/浅色主题分别配色。
    /// </summary>
    private static string BuildToolHeaderMarkup(
        string toolName,
        string subtitle,
        string paramDisplay,
        bool isRunning,
        bool isError,
        bool isDark
    )
    {
        var iconColor = isDark ? ToolIconColor : ToolIconColorLight;
        var nameColor = isDark ? ToolNameColor : ToolNameColorLight;
        var subColor = isDark ? ToolSubtitleColor : ToolSubtitleColorLight;
        var paramColor = isDark ? ToolParamColor : ToolParamColorLight;
        var successColor = isDark ? ToolSuccessColor : ToolSuccessColorLight;
        var errorColor = isDark ? ToolErrorColor : ToolErrorColorLight;

        var sb = new StringBuilder();

        AppendAnsiSpan(sb, iconColor, false, "🔧 ");
        AppendAnsiSpan(sb, nameColor, true, toolName);

        if (!string.IsNullOrEmpty(subtitle))
        {
            sb.Append("  ");
            AppendAnsiSpan(sb, subColor, false, subtitle);
        }

        if (!string.IsNullOrEmpty(paramDisplay))
        {
            sb.Append("  ");
            AppendAnsiSpan(sb, paramColor, false, paramDisplay);
        }

        var statusColor =
            isRunning ? iconColor
            : isError ? errorColor
            : successColor;
        var statusText =
            isRunning ? " 🔄"
            : isError ? " ❌"
            : " ✅";
        AppendAnsiSpan(sb, statusColor, !isRunning && isError, statusText);

        return sb.ToString();
    }

    /// <summary>追加一段 ANSI 颜色标签包裹的文本到 StringBuilder。</summary>
    private static void AppendAnsiSpan(StringBuilder sb, Color color, bool bold, string text)
    {
        var r = color.R;
        var g = color.G;
        var b = color.B;
        if (bold)
            sb.Append($"[bold #{r:x2}{g:x2}{b:x2}]");
        else
            sb.Append($"[#{r:x2}{g:x2}{b:x2}]");
        sb.Append(Escape(text));
        sb.Append("[/]");
    }

    /// <summary>尝试从工具结果 JSON 中提取实体 ID（用于"查看设定"导航按钮）。</summary>
    private static string? TryExtractEntityId(string result)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("id", out var idEl))
                return idEl.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 转义 Markup 里的字面量方括号：<c>[</c> → <c>[[</c>、<c>]</c> → <c>]]</c>（XenoAtom 的转义写法）。
    /// **仅用于 Markup(string) 路径**（字符串拼接/变量的文案）；
    /// <c>new Markup($"…")</c> 走的是插值字符串处理器，取值会被自动转义——插值里再调用本方法就是二次转义。
    /// </summary>
    public static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");

    /// <summary>截断文本到指定长度，超出部分用省略号替代。</summary>
    public static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";

    /// <summary>
    /// 尝试复制 Markup 选中文本到剪贴板。
    /// 优先使用 XenoAtom 内部剪贴板，失败则回退到系统剪贴板。
    /// </summary>
    public static void TryCopySelection(Markup markup)
    {
        if (!markup.TryCopySelection(out var text) || string.IsNullOrEmpty(text))
            return;
        // Try XenoAtom's internal clipboard first; use platform-native fallback if it fails.
        if (!markup.App?.Terminal.Clipboard.TrySetText(text) ?? false)
            WriteSystemClipboard(text);
        ((ISelectionOwner)markup).ClearSelection();
    }

    /// <summary>
    /// Attaches both right-click (ContextMenuFactory) and selection-end (PointerReleased)
    /// copy handlers to a Markup control. Selected text is written to the system
    /// clipboard via OSC 52.
    /// <para>On PointerReleased the selection is copied but NOT cleared, so the user
    /// can still see what was selected and use platform-native paste afterwards.</para>
    /// </summary>
    public static void AttachCopyHandlers(Markup markup)
    {
        // 右键 → 复制选中文本并清除选区
        markup.ContextMenuFactory = _ =>
        {
            TryCopySelection(markup);
            return null!;
        };

        // 鼠标松开 → 复制选中文本但不清除选区
        markup.PointerReleased((_, _) => CopySelectionOnly(markup));

        // 键盘 Ctrl+C 后备：CBreak 模式下 Ctrl+C 作为 \x03 字节到达
        // （当 ISIG=0 时 SIGINT 信号被禁用，Ctrl+C 退化为文本输入）
        markup.TextInput(
            (_, e) =>
            {
                if (e.Text is "\x03")
                {
                    TryCopySelection(markup);
                    e.Handled = true;
                }
            }
        );
    }

    /// <summary>
    /// Copies the Markup's internal selection to the system clipboard WITHOUT clearing it.
    /// Uses pbcopy on macOS, xclip/wl-copy on Linux, OSC 52 as universal fallback.
    /// </summary>
    private static void CopySelectionOnly(Markup markup)
    {
        if (!markup.TryCopySelection(out var text) || string.IsNullOrEmpty(text))
            return;
        WriteSystemClipboard(text);
    }

    /// <summary>
    /// Writes text to the system clipboard. Tries platform-native tools first,
    /// then falls back to OSC 52 terminal escape sequence.
    /// </summary>
    private static void WriteSystemClipboard(string text)
    {
        // 1. Try platform-native clipboard program
        if (TryNativeClipboard(text))
            return;

        // 2. Fall back to OSC 52 escape sequence
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Console.Write("\e]52;c;{0}\e\\", base64);
        Console.Out.Flush();
    }

    /// <summary>尝试使用平台原生剪贴板程序（pbcopy/xclip/wl-copy/clip.exe）。</summary>
    private static bool TryNativeClipboard(string text)
    {
        try
        {
            var (prog, arg) = GetClipboardProgram();
            if (prog is null)
                return false;

            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = prog,
                Arguments = arg,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            proc.WaitForExit(1000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>根据操作系统返回剪贴板程序名和参数。</summary>
    private static (string?, string) GetClipboardProgram()
    {
        if (OperatingSystem.IsMacOS())
            return ("pbcopy", "");
        if (OperatingSystem.IsLinux())
        {
            // Try wl-copy first (Wayland), then xclip (X11)
            if (File.Exists("/usr/bin/wl-copy") || File.Exists("/usr/local/bin/wl-copy"))
                return ("wl-copy", "");
            if (File.Exists("/usr/bin/xclip") || File.Exists("/usr/local/bin/xclip"))
                return ("xclip", "-selection clipboard");
            if (File.Exists("/usr/bin/xsel") || File.Exists("/usr/local/bin/xsel"))
                return ("xsel", "--clipboard --input");
        }
        return OperatingSystem.IsWindows()
            ?
            // clip.exe copies stdin to clipboard on Windows
            ("clip", "")
            : (null, "");
    }
}
