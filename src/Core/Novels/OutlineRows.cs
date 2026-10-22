using System.Globalization;
using System.Text;
using Core.Data;
using Core.Shared;

namespace Core.Novels;

/// <summary>
/// 大纲行类型（四层大纲 → DocumentFlow 虚拟化行模型）：
/// 根节点 = 当前小说 <see cref="Novel"/>（层级：小说 → 卷 → 章；详情入口由两端各自渲染）→
/// 卷头 <see cref="VolumeHeader"/> / 未分卷组头 <see cref="UnassignedHeader"/> →
/// 章节 <see cref="Chapter"/> → 续页 <see cref="More"/> / 空态 <see cref="Empty"/>。
/// （<see cref="Ontology"/> / <see cref="Check"/> 为历史行类型：现由书级分节承载，不再出行。）
///
/// <para>只物化「已加载页 + 1 条 More」——整棵树不展开，故上千章也走 DocumentFlow 虚拟化。</para>
/// </summary>
public enum OutlineRowKind
{
    Novel,
    Ontology,
    Check,
    VolumeHeader,
    UnassignedHeader,
    Chapter,
    More,
    Empty,
}

/// <summary>
/// 单行值快照（两端共用的渲染输入）。<see cref="Id"/> = 卷 id / 章节 id（供操作与展开定位）；
/// <see cref="GroupKey"/> = 分页分组键（卷 id 或 <see cref="OutlineRows.UnassignedKey"/>）。
/// </summary>
public sealed record OutlineRow
{
    public required OutlineRowKind Kind { get; init; }

    /// <summary>稳定行键（novel / ontology / check / v:{id} / {UnassignedKey} / ch:{id} / more:{group} / empty）。</summary>
    public string Key { get; init; } = "";

    /// <summary>实体 id（卷 / 章节）；前言行与组头为空串。</summary>
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>原始名（卷名 / 章名，无「第N卷 ·」与「{Order}.」前缀）；其余行为空串。</summary>
    public string Name { get; init; } = "";

    public string MetaLine { get; init; } = "";
    public string SummaryLine { get; init; } = "";
    public string StatusIcon { get; init; } = "";

    /// <summary>状态标签（章行：「草稿」/「审核」/「终稿」；Desktop 模板的状态 Tag 绑定）。</summary>
    public string StatusLabel { get; init; } = "";

    /// <summary>章节正文字符数（章行字数列；其余行 0）。</summary>
    public int CharCount { get; init; }

    /// <summary>卷内终稿章数（卷头进度列；其余行 0）。</summary>
    public int ProgressDone { get; init; }

    /// <summary>卷内总章数（卷头进度列；其余行 0）。</summary>
    public int ProgressTotal { get; init; }

    /// <summary>章节序号（章行用于渲染 "{Order}. {Title}" 前缀识别；其余行 0）。</summary>
    public int Order { get; init; }

    /// <summary>
    /// 卷内序号（1 起；章行专用）。在同一卷下自增，换卷从 1 重新计数——
    /// 与 <see cref="Order"/>（全书全局序）分开：显示用卷内号，落库/排序仍用全局 Order。
    /// 非章行、或无法归属到已展开分组时为 0（此时 <see cref="OrderText"/> 退回全局序格式）。
    /// </summary>
    public int GroupOrder { get; init; }

    public IReadOnlyList<string> DetailLines { get; init; } = [];
    public IReadOnlyList<string> Chips { get; init; } = [];
    public bool IsExpanded { get; init; }
    public string GroupKey { get; init; } = "";

    /// <summary>More 行：本分组尚未加载的行数。</summary>
    public int HiddenCount { get; init; }
    public int Depth { get; init; }
    public bool IsNewlyCreated { get; init; }
    public bool IsRevealOnly { get; init; }
    public string ContextLine { get; init; } = "";

    // ── 排版列（预格式化文本，两端直接绑定/拼接；规则见 <see cref="OutlineRowText"/>）──

    /// <summary>章序号列：卷内「第 N 章」（换卷从 1 重新计数），非章行为空。</summary>
    public string OrderText =>
        GroupOrder > 0 ? $"第 {GroupOrder} 章" : OutlineRowText.FormatOrder(Order);

    /// <summary>字数列：1,001 → "1.0k"（<see cref="OutlineRowText.FormatWords"/>）。</summary>
    public string WordsText => CharCount > 0 ? OutlineRowText.FormatWords(CharCount) : "";

    /// <summary>卷进度列：「终稿 2/3」（无章为空）。</summary>
    public string ProgressLine => ProgressTotal > 0 ? $"终稿 {ProgressDone}/{ProgressTotal}" : "";

    // ── 层级判定（两端 XAML / 条件绑定用）──

    /// <summary>是否小说根行。</summary>
    public bool IsNovel => Kind == OutlineRowKind.Novel;

    /// <summary>是否卷行（含未分卷组头）。</summary>
    public bool IsVolume => Kind is OutlineRowKind.VolumeHeader or OutlineRowKind.UnassignedHeader;

    /// <summary>是否章行。</summary>
    public bool IsChapter => Kind == OutlineRowKind.Chapter;
}

/// <summary>
/// 大纲视图状态（筛选 + 展开 + 分页窗口）。<see cref="Scope"/>：null = 全部章节、
/// ""（空串）= 仅未分卷、否则该卷 id；<see cref="LoadedRows"/> 缺失某分组键时回落 <see cref="PageSize"/>。
/// 集合字段容忍 null（等价空集合，调用方常传 null 表示"无"）。
/// </summary>
public sealed record OutlineViewState(
    IReadOnlySet<string>? ExpandedVolumes,
    IReadOnlySet<string>? ExpandedChapters,
    string? Scope,
    string? Keyword,
    ChapterStatus? Status,
    int PageSize,
    IReadOnlyDictionary<string, int>? LoadedRows,
    string? RevealVolumeId = null,
    string? RevealChapterId = null,
    /// <summary>根节点（小说行）是否展开（= 是否物化卷/章行）；默认展开。</summary>
    bool NovelExpanded = true
);

/// <summary>
/// <see cref="OutlineRows.Build"/> 的完整输出：结构行（卷/章/分页/空态）+ 书级摘要。
/// 书级内容（总纲/本体/检查）不再是行，两端各自渲染为顶部摘要条。
/// </summary>
public sealed record OutlineBuildResult(IReadOnlyList<OutlineRow> Rows, BookSummary Summary);

public sealed record BookSummary(
    /// <summary>书名（如「测试小说」）。</summary>
    string Title,
    /// <summary>总纲首行（书走向，一行截断）。</summary>
    string OutlineLine,
    /// <summary>卷数。</summary>
    int VolumeCount,
    /// <summary>章节总数。</summary>
    int ChapterCount,
    /// <summary>终稿章数（进度驱动的列，与字数同权）。</summary>
    int DoneCount,
    /// <summary>总字数。</summary>
    int TotalWords,
    /// <summary>设定检查的存活条数（0 = 未跑过 / 无未决问题）。</summary>
    int CheckFindings,
    /// <summary>本体概览字段数（供「本体」按钮的悬停/副文本）。</summary>
    int OntologyFields
);

/// <summary>
/// 大纲行构建（纯函数，无 UI 依赖；TUI / Desktop 共用同一行模型）。
///
/// <para>过滤：标题关键字（忽略大小写子串）+ 状态 + 范围（全部 / 单卷 / 未分卷）；
/// 过滤后 0 章的卷仍保留卷头（结构可见），只有章行消失。</para>
/// <para>分页：展开的分组只物化前 N 行（N = LoadedRows[组键] ?? PageSize），其余折叠为一条 More 行。</para>
///
/// <para>重建备注：本文件原为 2026-09-27 大纲功能的新建源码，被 .gitignore 未锚定的
/// <c>novels/</c> 规则静默吞掉从未入库（事故记录见仓库 .gitignore 178 行附近）。
/// 现按三套既有测试（Core/Desktop/Tui）钉死的行为重建；作者原版若补回，可直接替换本文件。</para>
/// </summary>
public static class OutlineRows
{
    /// <summary>默认分页窗口（每个展开分组物化的章行数）。</summary>
    public const int DefaultPageSize = 200;

    /// <summary>未分卷组的展开键 / 分组键（与卷 id 命名空间隔离）。</summary>
    public const string UnassignedKey = "__unassigned__";

    private static readonly string[] EmptyOutlineLine = ["（卷纲未填写）"];
    private static readonly string[] EmptyChapterOutlineLine = ["（细纲未填写）"];

    public static OutlineBuildResult Build(
        NovelStructure structure,
        OutlineViewState state,
        IReadOnlyDictionary<string, string> plotlineNames,
        IReadOnlyDictionary<string, string> characterNames,
        bool showOntology,
        bool showCheckResult
    )
    {
        var rows = new List<OutlineRow>();
        var volumes = structure
            .Volumes.OrderBy(volume => volume.Order)
            .ThenBy(volume => volume.Name, StringComparer.Ordinal)
            .ToList();
        var volumeById = volumes.ToDictionary(volume => volume.Id, StringComparer.Ordinal);
        var chaptersByVolume = new Dictionary<string, List<Chapter>>(StringComparer.Ordinal);
        foreach (var chapter in structure.Chapters)
        {
            if (
                chapter.VolumeId is not { Length: > 0 } volumeId
                || !volumeById.ContainsKey(volumeId)
            )
                continue;
            if (!chaptersByVolume.TryGetValue(volumeId, out var group))
                chaptersByVolume.Add(volumeId, group = []);
            group.Add(chapter);
        }

        // 书级内容不再出「行」——由两端各自渲染为顶部摘要条。
        var novelOutline = SplitLines(structure.Novel.Outline);
        var summary = new BookSummary(
            Title: structure.Novel.Title,
            OutlineLine: novelOutline.FirstOrDefault() ?? "（未填写总纲）",
            VolumeCount: volumes.Count,
            ChapterCount: structure.Chapters.Count,
            // 「终稿数」改按发布数算（审核/终稿状态已移除，用户报障）
            DoneCount: structure.Chapters.Count(c => c.Status == ChapterStatus.Published),
            TotalWords: structure.Chapters.Sum(c => c.CharCount),
            CheckFindings: 0, // 由调用方（面板）根据实际检查结果覆盖
            OntologyFields: 0 // 由调用方（面板）根据实际本体字段覆盖
        );

        // 根节点 = 当前小说（层级：小说 → 卷 → 章）。折叠时不再物化任何卷/章行——
        // 首屏只给「当前小说」，点开左侧箭头才展开底下的卷（两端共用同一行模型）。
        rows.Add(
            new OutlineRow
            {
                Kind = OutlineRowKind.Novel,
                Key = "novel",
                Id = structure.Novel.Id,
                Title = structure.Novel.Title,
                MetaLine = FormatNovelMetaLine(structure.Novel),
                SummaryLine = summary.OutlineLine,
                IsExpanded = state.NovelExpanded,
                Depth = 0,
            }
        );
        if (!state.NovelExpanded)
            return new OutlineBuildResult(rows, summary);

        var keyword = string.IsNullOrWhiteSpace(state.Keyword) ? null : state.Keyword.Trim();
        var statusFilter = state.Status;
        bool Matches(Chapter chapter) =>
            (statusFilter is null || chapter.Status == statusFilter)
            && FuzzySearch.Matches(chapter.Title, keyword);

        var scope = state.Scope;
        var includeVolumes = scope is null || scope.Length > 0;
        var includeUnassigned = scope is null || scope.Length == 0;
        var unassignedChapters = structure.Unassigned.OrderChapters().ToList();
        var matchedUnassigned = includeUnassigned ? unassignedChapters.Where(Matches).ToList() : [];
        var visibleMatched = matchedUnassigned.Count;
        var hasExpandedGroupEmpty = false;
        var revealedChapter = state.RevealChapterId is { Length: > 0 } revealId
            ? structure.Chapters.FirstOrDefault(chapter =>
                string.Equals(chapter.Id, revealId, StringComparison.Ordinal)
            )
            : null;

        if (includeVolumes)
        {
            foreach (var volume in volumes)
            {
                if (
                    scope is { Length: > 0 }
                    && !string.Equals(scope, volume.Id, StringComparison.Ordinal)
                )
                    continue;

                chaptersByVolume.TryGetValue(volume.Id, out var unsortedChapters);
                var allChapters = unsortedChapters?.OrderChapters().ToList() ?? [];
                var matched = allChapters.Where(Matches).ToList();
                visibleMatched += matched.Count;

                var expanded = IsVolumeExpanded(state, volume.Id);
                var isNewlyCreated = string.Equals(
                    state.RevealVolumeId,
                    volume.Id,
                    StringComparison.Ordinal
                );
                rows.Add(
                    CreateVolumeHeader(
                        volume,
                        matched,
                        plotlineNames,
                        characterNames,
                        expanded,
                        isNewlyCreated
                    )
                );
                if (!expanded)
                    continue;

                AppendChapterPage(
                    rows,
                    matched,
                    allChapters,
                    volume.Id,
                    state,
                    plotlineNames,
                    characterNames,
                    revealedChapter
                );
                if (matched.Count == 0)
                {
                    rows.Add(
                        new OutlineRow
                        {
                            Kind = OutlineRowKind.Empty,
                            Key = $"empty:{volume.Id}",
                            GroupKey = volume.Id,
                            Title =
                                allChapters.Count == 0 ? "本卷还没有章节" : "当前筛选没有命中章节",
                            Depth = 1,
                        }
                    );
                    hasExpandedGroupEmpty = true;
                }
            }
        }

        if (includeUnassigned && (unassignedChapters.Count > 0 || scope?.Length == 0))
        {
            var expanded = IsVolumeExpanded(state, UnassignedKey);
            rows.Add(
                new OutlineRow
                {
                    Kind = OutlineRowKind.UnassignedHeader,
                    Key = UnassignedKey,
                    Title = "未分卷",
                    MetaLine = FormatCountLine(matchedUnassigned),
                    IsExpanded = expanded,
                    GroupKey = UnassignedKey,
                }
            );
            if (expanded)
            {
                AppendChapterPage(
                    rows,
                    matchedUnassigned,
                    unassignedChapters,
                    UnassignedKey,
                    state,
                    plotlineNames,
                    characterNames,
                    revealedChapter
                );
                if (matchedUnassigned.Count == 0)
                {
                    rows.Add(
                        new OutlineRow
                        {
                            Kind = OutlineRowKind.Empty,
                            Key = $"empty:{UnassignedKey}",
                            GroupKey = UnassignedKey,
                            Title =
                                unassignedChapters.Count == 0
                                    ? "未分卷中还没有章节"
                                    : "当前筛选没有命中章节",
                            Depth = 1,
                        }
                    );
                    hasExpandedGroupEmpty = true;
                }
            }
        }

        AppendOutOfScopeRevealedVolume(rows, volumes, state, plotlineNames, characterNames);
        AppendRevealedChapterOutsideGroup(
            rows,
            revealedChapter,
            volumeById,
            scope,
            state,
            plotlineNames,
            characterNames
        );

        if (!hasExpandedGroupEmpty && structure.Chapters.Count == 0)
            rows.Add(
                new OutlineRow
                {
                    Kind = OutlineRowKind.Empty,
                    Key = "empty",
                    Title = "（无章节）",
                }
            );
        else if (!hasExpandedGroupEmpty && visibleMatched == 0)
            rows.Add(
                new OutlineRow
                {
                    Kind = OutlineRowKind.Empty,
                    Key = "empty",
                    Title =
                        keyword is null && statusFilter is null && scope is not null
                            ? "当前范围没有章节"
                            : "没有命中章节（试试清除筛选或切换范围）",
                }
            );

        return new OutlineBuildResult(rows, summary);
    }

    /// <summary>小说行副文本：作者 · 类型 · 目标字数（有值才出现）。</summary>
    private static string FormatNovelMetaLine(Novel novel)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(novel.Author))
            parts.Add($"作者: {novel.Author}");
        if (!string.IsNullOrWhiteSpace(novel.Genre))
            parts.Add(novel.Genre);
        if (novel.TargetWords > 0)
            parts.Add($"目标 {novel.TargetWords:N0} 字");
        return string.Join(" · ", parts);
    }

    /// <summary>Row fingerprint; changes whenever visible content, expansion, or reveal state changes.</summary>
    public static string Signature(IReadOnlyList<OutlineRow> rows)
    {
        var builder = new StringBuilder(rows.Count * 64);
        foreach (var row in rows)
        {
            builder
                .Append((int)row.Kind)
                .Append('|')
                .Append(row.Key)
                .Append('|')
                .Append(row.Id)
                .Append('|')
                .Append(row.Title)
                .Append('|')
                .Append(row.MetaLine)
                .Append('|')
                .Append(row.SummaryLine)
                .Append('|')
                .Append(row.StatusIcon)
                .Append('|')
                .Append(row.StatusLabel)
                .Append('|')
                .Append(row.Order)
                .Append('|')
                .Append(row.IsExpanded ? '1' : '0')
                .Append('|')
                .Append(row.GroupKey)
                .Append('|')
                .Append(row.HiddenCount)
                .Append('|')
                .Append(row.Depth)
                .Append('|')
                .Append(row.IsNewlyCreated ? '1' : '0')
                .Append('|')
                .Append(row.IsRevealOnly ? '1' : '0')
                .Append('|')
                .Append(row.ContextLine)
                .Append('|')
                .AppendJoin('\u001f', row.DetailLines)
                .Append('|')
                .AppendJoin('\u001f', row.Chips)
                .Append('\n');
        }
        var hash = 14695981039346656037UL;
        foreach (var ch in builder.ToString())
            hash = (hash ^ ch) * 1099511628211UL;
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    // ── 分组分页 ──

    private static void AppendChapterPage(
        List<OutlineRow> rows,
        List<Chapter> matched,
        List<Chapter> groupChapters,
        string groupKey,
        OutlineViewState state,
        IReadOnlyDictionary<string, string> plotlineNames,
        IReadOnlyDictionary<string, string> characterNames,
        Chapter? revealedChapter
    )
    {
        var pageSize = state.PageSize > 0 ? state.PageSize : DefaultPageSize;
        var loaded =
            state.LoadedRows is { } loadedRows
            && loadedRows.TryGetValue(groupKey, out var requested)
            && requested > 0
                ? requested
                : pageSize;
        var page = matched.Count <= loaded ? matched : matched.Take(loaded).ToList();
        var revealInGroup =
            revealedChapter is not null
            && groupChapters.Any(chapter =>
                string.Equals(chapter.Id, revealedChapter.Id, StringComparison.Ordinal)
            );
        var revealInPage =
            revealInGroup
            && page.Any(chapter =>
                string.Equals(chapter.Id, revealedChapter!.Id, StringComparison.Ordinal)
            );

        if (revealInGroup && !revealInPage)
            rows.Add(
                CreateChapterRow(
                    revealedChapter!,
                    groupKey,
                    state,
                    plotlineNames,
                    characterNames,
                    isNewlyCreated: true,
                    isRevealOnly: true,
                    contextLine: matched.Any(chapter =>
                        string.Equals(chapter.Id, revealedChapter!.Id, StringComparison.Ordinal)
                    )
                        ? "刚创建 · 当前页外"
                        : "刚创建 · 当前筛选外",
                    depth: 1,
                    groupOrder: groupChapters.FindIndex(c =>
                        string.Equals(c.Id, revealedChapter!.Id, StringComparison.Ordinal)
                    ) + 1
                )
            );

        foreach (var chapter in page)
        {
            var isNewlyCreated = string.Equals(
                chapter.Id,
                state.RevealChapterId,
                StringComparison.Ordinal
            );
            rows.Add(
                CreateChapterRow(
                    chapter,
                    groupKey,
                    state,
                    plotlineNames,
                    characterNames,
                    isNewlyCreated,
                    isRevealOnly: false,
                    contextLine: isNewlyCreated ? "刚创建" : "",
                    depth: 1,
                    // 卷内序号：本卷已排序章节里的位置（1 起）。用全量组内列表而不是当前页，
                    // 这样翻页/筛选时编号仍然连续，不会从 1 重新数。
                    groupOrder: groupChapters.FindIndex(c =>
                        string.Equals(c.Id, chapter.Id, StringComparison.Ordinal)
                    ) + 1
                )
            );
        }

        if (matched.Count > page.Count)
        {
            var hidden = matched.Count - page.Count;
            rows.Add(
                new OutlineRow
                {
                    Kind = OutlineRowKind.More,
                    Key = $"more:{groupKey}",
                    Title = $"还有 {hidden} 条",
                    GroupKey = groupKey,
                    HiddenCount = hidden,
                    Depth = 1,
                }
            );
        }
    }

    private static OutlineRow CreateChapterRow(
        Chapter chapter,
        string groupKey,
        OutlineViewState state,
        IReadOnlyDictionary<string, string> plotlineNames,
        IReadOnlyDictionary<string, string> characterNames,
        bool isNewlyCreated,
        bool isRevealOnly,
        string contextLine,
        int depth,
        int groupOrder = 0
    ) =>
        new()
        {
            Kind = OutlineRowKind.Chapter,
            Key = $"ch:{chapter.Id}",
            Id = chapter.Id,
            Title = $"{chapter.Order}. {chapter.Title}",
            Name = chapter.Title,
            MetaLine = FormatChapterMetaLine(chapter),
            SummaryLine = FirstLine(chapter.Summary),
            StatusIcon = StatusIconOf(chapter.Status),
            StatusLabel = StatusLabelOf(chapter.Status),
            Order = chapter.Order,
            GroupOrder = groupOrder,
            CharCount = chapter.CharCount,
            DetailLines = BuildChapterDetailLines(chapter, plotlineNames, characterNames),
            IsExpanded = state.ExpandedChapters?.Contains(chapter.Id) == true,
            GroupKey = groupKey,
            Depth = depth,
            IsNewlyCreated = isNewlyCreated,
            IsRevealOnly = isRevealOnly,
            ContextLine = contextLine,
        };

    private static OutlineRow CreateVolumeHeader(
        Volume volume,
        IReadOnlyCollection<Chapter> matching,
        IReadOnlyDictionary<string, string> plotlineNames,
        IReadOnlyDictionary<string, string> characterNames,
        bool isExpanded,
        bool isNewlyCreated,
        bool isRevealOnly = false,
        string contextLine = ""
    ) =>
        new()
        {
            Kind = OutlineRowKind.VolumeHeader,
            Key = $"v:{volume.Id}",
            Id = volume.Id,
            Title = FormatVolumeTitle(volume),
            Name = volume.Name,
            MetaLine = FormatCountLine(matching),
            SummaryLine = FirstLine(volume.Summary),
            DetailLines = BuildVolumeDetailLines(volume, plotlineNames, characterNames),
            Chips = BuildVolumeChips(volume, plotlineNames, characterNames),
            ProgressDone = matching.Count(c => c.Status == ChapterStatus.Published),
            ProgressTotal = matching.Count,
            IsExpanded = isExpanded,
            GroupKey = volume.Id,
            IsNewlyCreated = isNewlyCreated,
            IsRevealOnly = isRevealOnly,
            ContextLine =
                contextLine.Length > 0 ? contextLine
                : isNewlyCreated ? "刚创建"
                : "",
        };

    private static void AppendOutOfScopeRevealedVolume(
        List<OutlineRow> rows,
        List<Volume> volumes,
        OutlineViewState state,
        IReadOnlyDictionary<string, string> plotlineNames,
        IReadOnlyDictionary<string, string> characterNames
    )
    {
        if (
            state.RevealVolumeId is not { Length: > 0 } volumeId
            || state.Scope is null
            || string.Equals(state.Scope, volumeId, StringComparison.Ordinal)
        )
            return;

        var volume = volumes.FirstOrDefault(item =>
            string.Equals(item.Id, volumeId, StringComparison.Ordinal)
        );
        if (
            volume is null
            || rows.Any(row => row.Kind == OutlineRowKind.VolumeHeader && row.Id == volumeId)
        )
            return;

        rows.Add(
            CreateVolumeHeader(
                volume,
                Array.Empty<Chapter>(),
                plotlineNames,
                characterNames,
                isExpanded: false,
                isNewlyCreated: true,
                isRevealOnly: true,
                contextLine: "刚创建 · 当前章节范围外"
            )
        );
    }

    private static void AppendRevealedChapterOutsideGroup(
        List<OutlineRow> rows,
        Chapter? chapter,
        IReadOnlyDictionary<string, Volume> volumeById,
        string? scope,
        OutlineViewState state,
        IReadOnlyDictionary<string, string> plotlineNames,
        IReadOnlyDictionary<string, string> characterNames
    )
    {
        if (
            chapter is null
            || rows.Any(row => row.Kind == OutlineRowKind.Chapter && row.Id == chapter.Id)
        )
            return;

        var volumeId = ChapterOrdering.GroupKey(chapter.VolumeId);
        var volume =
            volumeId is not null && volumeById.TryGetValue(volumeId, out var existingVolume)
                ? existingVolume
                : null;
        var groupKey = volume?.Id ?? UnassignedKey;
        var groupTitle = volume is null ? "未分卷" : FormatVolumeTitle(volume);
        var groupIsInScope =
            groupKey == UnassignedKey
                ? scope is null or ""
                : scope is null || string.Equals(scope, groupKey, StringComparison.Ordinal);
        var groupIsExpanded = IsVolumeExpanded(state, groupKey);
        var contextLine = groupIsInScope
            ? groupIsExpanded
                ? "刚创建 · 当前筛选外"
                : $"刚创建 · {groupTitle}分组已折叠"
            : $"{groupTitle} · 当前章节范围外";

        rows.Add(
            CreateChapterRow(
                chapter,
                groupKey,
                state,
                plotlineNames,
                characterNames,
                isNewlyCreated: true,
                isRevealOnly: true,
                contextLine,
                depth: 1
            )
        );
    }

    private static bool IsVolumeExpanded(OutlineViewState state, string groupKey) =>
        state.ExpandedVolumes?.Contains(groupKey) == true;

    private static string FirstLine(string? text) => SplitLines(text).FirstOrDefault() ?? "";

    // ── 文案与明细 ──

    private static string FormatVolumeTitle(Volume volume) =>
        string.IsNullOrWhiteSpace(volume.Name)
            ? $"第{volume.Order}卷"
            : $"第{volume.Order}卷 · {volume.Name}";

    private static string FormatCountLine(IReadOnlyCollection<Chapter> chapters) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{chapters.Count} 章 · {chapters.Sum(chapter => chapter.CharCount):N0} 字"
        );

    private static string FormatChapterMetaLine(Chapter chapter) => StatusLabelOf(chapter.Status);

    private static string StatusLabelOf(ChapterStatus status) =>
        status switch
        {
            ChapterStatus.Draft => "草稿",
            ChapterStatus.Published => "发布",
            _ => status.ToString(),
        };

    private static string StatusIconOf(ChapterStatus status) =>
        status switch
        {
            // 用户报障：发布后一个 icon、草稿一个 icon，一眼分出发出去了没有
            ChapterStatus.Draft => "📝",
            ChapterStatus.Published => "📢",
            _ => "",
        };

    private static IReadOnlyList<string> BuildVolumeDetailLines(
        Volume volume,
        IReadOnlyDictionary<string, string> plotlineNames,
        IReadOnlyDictionary<string, string> characterNames
    )
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(volume.Goal))
        {
            lines.Add($"卷目标: {volume.Goal}");
        }
        var plotlines = ResolveNames(volume.PlotlineIds, plotlineNames);
        if (plotlines.Count > 0)
        {
            lines.Add($"推进情节线: {string.Join("、", plotlines)}");
        }
        var characters = ResolveNames(volume.CharacterIds, characterNames);
        if (characters.Count > 0)
        {
            lines.Add($"关键角色: {string.Join("、", characters)}");
        }
        if (!string.IsNullOrWhiteSpace(volume.Notes))
        {
            lines.Add($"备注: {volume.Notes}");
        }
        return lines.Count > 0 ? lines : EmptyOutlineLine;
    }

    private static IReadOnlyList<string> BuildChapterDetailLines(
        Chapter chapter,
        IReadOnlyDictionary<string, string> plotlineNames,
        IReadOnlyDictionary<string, string> characterNames
    )
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(chapter.Summary))
        {
            lines.Add($"细纲: {chapter.Summary}");
        }
        var plotlines = ResolveNames(chapter.PlotlineIds, plotlineNames);
        if (plotlines.Count > 0)
        {
            lines.Add($"计划推进: {string.Join("、", plotlines)}");
        }
        var characters = ResolveNames(chapter.CharacterIds, characterNames);
        if (characters.Count > 0)
        {
            // 名单只列前 8 个，其余折成「等 N 个」（长篇细纲行保持单行可读）。
            var head = string.Join("、", characters.Take(8));
            lines.Add(
                characters.Count > 8
                    ? $"计划出场: {head} 等 {characters.Count} 个"
                    : $"计划出场: {head}"
            );
        }
        if (!string.IsNullOrWhiteSpace(chapter.Notes))
        {
            lines.Add($"备注: {chapter.Notes}");
        }
        return lines.Count > 0 ? lines : EmptyChapterOutlineLine;
    }

    private static IReadOnlyList<string> BuildVolumeChips(
        Volume volume,
        IReadOnlyDictionary<string, string> plotlineNames,
        IReadOnlyDictionary<string, string> characterNames
    )
    {
        var chips = new List<string>();
        chips.AddRange(
            ResolveNames(volume.PlotlineIds, plotlineNames).Select(name => $"推进 {name}")
        );
        chips.AddRange(
            ResolveNames(volume.CharacterIds, characterNames).Select(name => $"关键角色 {name}")
        );
        return chips;
    }

    /// <summary>id → 名称（未登记的 id 原样保留，不丢结构信息）。</summary>
    private static List<string> ResolveNames(
        IReadOnlyList<string>? ids,
        IReadOnlyDictionary<string, string> names
    )
    {
        if (ids is null or { Count: 0 })
        {
            return [];
        }
        var resolved = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            resolved.Add(
                names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : id
            );
        }
        return resolved;
    }

    /// <summary>总纲正文 → 行序列（trim + 去空行）。</summary>
    private static IReadOnlyList<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];

    /// <summary>同组内排序：Order 升序，并列按标题（与 ChapterOrdering 语义一致）。</summary>
    private static IEnumerable<Chapter> OrderChapters(this IEnumerable<Chapter> chapters) =>
        chapters
            .OrderBy(chapter => chapter.Order)
            .ThenBy(chapter => chapter.Title, StringComparer.Ordinal);
}
