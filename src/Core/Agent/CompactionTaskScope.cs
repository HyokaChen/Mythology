using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Core.Data;
using Core.Novels;

namespace Core.Agent;

internal enum CompactionTaskMode
{
    Write,
    Revise,
}

/// <summary>
/// compact 摘要里的任务边界。主路径由 <see cref="CompactionScopeExtractor"/> 调 LLM，
/// 从最近作者消息（跨分片）和上一份摘要提取；无 API 或提取失败时才用 TryCreate 做离线兜底。
/// 生成摘要后仍用它校验/修复 Where/Next。
/// </summary>
internal sealed class CompactionTaskScope
{
    public const string Heading = "## Scope / 任务边界";
    public const string Unrecorded = "- 未记录";

    private static readonly Regex VolumeIdPattern = new(
        @"(?:分卷\s*)?(?:\[\s*)?ID\s*[:：]\s*(?<id>[0-9A-Za-z_-]+)(?:\s*\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex VolumeOrderPattern = new(
        @"第\s*(?<n>[一二三四五六七八九十百千万零〇两\d]+)\s*卷|卷\s*(?<n>[一二三四五六七八九十百千万零〇两\d]+)",
        RegexOptions.Compiled
    );

    private static readonly Regex ChapterRangePattern = new(
        @"第\s*(?<a>[一二三四五六七八九十百千万零〇两\d]+)\s*章?\s*(?:-|—|–|~|～|至|到)\s*第?\s*(?<b>[一二三四五六七八九十百千万零〇两\d]+)\s*章",
        RegexOptions.Compiled
    );

    private static readonly Regex ChapterRefPattern = new(
        @"第\s*(?<n>[一二三四五六七八九十百千万零〇两\d]+)\s*章",
        RegexOptions.Compiled
    );

    private static readonly Regex AuthorInstructionPattern = new(
        @"作者指令\s*[:：]\s*(?<text>.+?)(?=\s+-\s+(?:任务类型|允许范围|硬约束|全局写作前沿说明|完成条件)\s*[:：]|$)",
        RegexOptions.Compiled
    );

    private static readonly Regex WriteActionPattern = new(
        @"写|续写|新建|创作|推进|开新章|落笔",
        RegexOptions.Compiled
    );

    private static readonly Regex ReviseActionPattern = new(
        @"优化|修订|修正|润色|重写|改写|打磨|迭代|复核|检查|调整|完善",
        RegexOptions.Compiled
    );

    private CompactionTaskScope(
        string instruction,
        CompactionTaskMode mode,
        IReadOnlyList<Volume> volumes,
        IReadOnlyList<Chapter> chapters
    )
    {
        Instruction = Truncate(instruction.ReplaceLineEndings(" ").Trim(), 600);
        Mode = mode;
        Volumes = volumes;
        Chapters = chapters;
        VolumeIds = volumes.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        VolumeOrders = volumes.Select(v => v.Order).ToHashSet();
        ChapterOrders = chapters.Select(c => c.Order).ToHashSet();
    }

    public string Instruction { get; }
    public CompactionTaskMode Mode { get; }
    public IReadOnlyList<Volume> Volumes { get; }
    public IReadOnlyList<Chapter> Chapters { get; }
    public IReadOnlySet<string> VolumeIds { get; }
    public IReadOnlySet<int> VolumeOrders { get; }
    public IReadOnlySet<int> ChapterOrders { get; }

    public int MinChapterOrder => Chapters.Count == 0 ? 0 : Chapters.Min(c => c.Order);
    public int MaxChapterOrder => Chapters.Count == 0 ? 0 : Chapters.Max(c => c.Order);

    /// <summary>
    /// 离线兜底：从最近作者消息或上一份摘要的 Scope 中提取范围。优先作者指令；
    /// 作者只说“继续”时继承 previousScopeText。正常路径由 LLM 提取，不依赖本方法。
    /// </summary>
    public static CompactionTaskScope? TryCreate(
        IReadOnlyList<string> instructions,
        NovelStructure? structure,
        string? previousScopeText = null
    )
    {
        if (structure is null || structure.Chapters.Count == 0)
            return null;

        foreach (var instruction in instructions)
        {
            if (TryParse(instruction, structure) is { } scope)
                return scope;
        }
        return TryParse(previousScopeText, structure);
    }

    /// <summary>
    /// 取最近一条携带实际任务信息的作者消息；“继续/好/可以”这类确认不构成新任务。
    /// 全部都是确认时返回最新一条，供提示词展示，但不会因此覆盖旧 Scope。
    /// </summary>
    internal static string? SelectTaskInstruction(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
            return null;
        foreach (var message in messages)
        {
            if (
                !string.IsNullOrWhiteSpace(message)
                && !SessionInternalMessages.IsContinuationOnlyAuthorMessage(message)
            )
                return message;
        }
        return messages[0];
    }

    /// <summary>
    /// 把 LLM 提取出的卷/章选择还原成 Scope。LLM 只给 ID/序号，
    /// 这里只做结构校验，不再从自然语言里猜范围。
    /// </summary>
    public static CompactionTaskScope? FromLlmSelection(
        string? instruction,
        string? mode,
        IReadOnlyList<string>? volumeIds,
        IReadOnlyList<int>? chapterOrders,
        NovelStructure? structure
    )
    {
        if (structure is null || structure.Chapters.Count == 0)
            return null;

        var selectedVolumeIds = (volumeIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var selectedOrders = (chapterOrders ?? []).ToHashSet();
        if (selectedVolumeIds.Count == 0 && selectedOrders.Count == 0)
            return null;

        var chapters = structure
            .Chapters.Where(c =>
                (
                    selectedVolumeIds.Count == 0
                    || (c.VolumeId is not null && selectedVolumeIds.Contains(c.VolumeId))
                ) && (selectedOrders.Count == 0 || selectedOrders.Contains(c.Order))
            )
            .OrderBy(c => c.Order)
            .ToList();
        if (chapters.Count == 0)
            return null;

        var volumes = structure
            .Volumes.Where(v =>
                selectedVolumeIds.Contains(v.Id)
                || chapters.Any(c => string.Equals(c.VolumeId, v.Id, StringComparison.Ordinal))
            )
            .OrderBy(v => v.Order)
            .ToList();
        var isRevise = string.Equals(mode, "revise", StringComparison.OrdinalIgnoreCase);
        return new CompactionTaskScope(
            instruction ?? "",
            isRevise ? CompactionTaskMode.Revise : CompactionTaskMode.Write,
            volumes,
            chapters
        );
    }

    private static CompactionTaskScope? TryParse(string? text, NovelStructure structure)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var source = text.ReplaceLineEndings(" ");
        var volumes = new List<Volume>();
        var ranges = new List<(int Min, int Max)>();
        var chapterOrders = new List<int>();

        void AddVolume(Volume? volume)
        {
            if (
                volume is null
                || volumes.Any(v => string.Equals(v.Id, volume.Id, StringComparison.Ordinal))
            )
                return;
            volumes.Add(volume);
        }

        foreach (Match match in VolumeIdPattern.Matches(source))
        {
            if (IsNegated(source, match.Index))
                continue;
            var id = match.Groups["id"].Value;
            AddVolume(
                structure.Volumes.FirstOrDefault(v =>
                    string.Equals(v.Id, id, StringComparison.Ordinal)
                )
            );
        }

        foreach (Match match in VolumeOrderPattern.Matches(source))
        {
            if (IsNegated(source, match.Index))
                continue;
            if (match.Groups["n"].Success && ParseNumber(match.Groups["n"].Value) is { } order)
            {
                AddVolume(structure.Volumes.FirstOrDefault(v => v.Order == order));
            }
        }

        foreach (var volume in structure.Volumes)
        {
            if (volume.Name.Length < 2)
                continue;
            var index = source.IndexOf(volume.Name, StringComparison.Ordinal);
            if (index >= 0 && !IsNegated(source, index))
                AddVolume(volume);
        }

        foreach (Match match in ChapterRangePattern.Matches(source))
        {
            if (IsNegated(source, match.Index))
                continue;
            if (
                ParseNumber(match.Groups["a"].Value) is { } min
                && ParseNumber(match.Groups["b"].Value) is { } max
            )
            {
                ranges.Add((Math.Min(min, max), Math.Max(min, max)));
            }
        }

        // Range endpoints would otherwise be mistaken for two single-chapter refs.
        if (ranges.Count == 0)
        {
            foreach (Match match in ChapterRefPattern.Matches(source))
            {
                if (IsNegated(source, match.Index))
                    continue;
                if (ParseNumber(match.Groups["n"].Value) is { } order)
                    chapterOrders.Add(order);
            }
        }

        if (volumes.Count == 0 && ranges.Count == 0 && chapterOrders.Count == 0)
            return null;

        IEnumerable<Chapter> candidates = structure.Chapters;
        if (volumes.Count > 0)
            candidates = candidates.Where(c =>
                volumes.Any(v => string.Equals(v.Id, c.VolumeId, StringComparison.Ordinal))
            );
        if (ranges.Count > 0)
            candidates = candidates.Where(c =>
                ranges.Any(r => c.Order >= r.Min && c.Order <= r.Max)
            );
        else if (chapterOrders.Count > 0)
            candidates = candidates.Where(c => chapterOrders.Contains(c.Order));

        var chapters = candidates.OrderBy(c => c.Order).ToList();
        if (chapters.Count == 0)
            return null;

        // 只按章节范围定位时，把所属卷也补进来，保证“卷三下一章”这类越界文本能被识别。
        if (volumes.Count == 0)
        {
            foreach (
                var volumeId in chapters
                    .Select(c => c.VolumeId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct()
            )
            {
                AddVolume(
                    structure.Volumes.FirstOrDefault(v =>
                        string.Equals(v.Id, volumeId, StringComparison.Ordinal)
                    )
                );
            }
        }

        var isRevise = ReviseActionPattern.IsMatch(source);
        var isWrite = WriteActionPattern.IsMatch(source);
        var mode =
            isRevise || (!isWrite && volumes.Count > 0)
                ? CompactionTaskMode.Revise
                : CompactionTaskMode.Write;

        var instruction = ExtractInstruction(source);
        return new CompactionTaskScope(
            instruction,
            mode,
            volumes.OrderBy(v => v.Order).ToList(),
            chapters
        );
    }

    public string FormatSection() => $"{Heading}\n{FormatBody()}";

    public string FormatBody()
    {
        var modeText =
            Mode == CompactionTaskMode.Revise
                ? "修订/优化已有章节（不是续写新章）"
                : "续写/新建正文";
        var sb = new StringBuilder();
        sb.AppendLine($"- 任务类型：{modeText}");
        sb.AppendLine($"- 作者指令：{Instruction}");
        sb.AppendLine($"- 允许范围：{AllowedRangeText()}");
        sb.AppendLine(
            "- 硬约束：Next 必须是上述范围内的下一步；不得写入/改动范围外的卷或章；不得把全局写作前沿的下一章当作本任务 Next。"
        );
        sb.AppendLine(
            "- 全局写作前沿说明：小说状态中的“当前章/下一章”只是全书写作进度，不等于本任务进度；两者冲突时以本节 Scope 为准。"
        );
        sb.AppendLine(
            Mode == CompactionTaskMode.Revise
                ? "- 完成条件：范围内全部章节完成，并经作者明确确认后，才可推进下一卷。"
                : "- 完成条件：范围内目标章节完成；不得顺手扩写到范围外。"
        );
        return sb.ToString().TrimEnd();
    }

    public Chapter? FindFocusChapter(CompactionTaskProgress? progress = null)
    {
        if (progress is not null)
        {
            if (
                progress.CurrentOrder is int progressOrder
                && Chapters.FirstOrDefault(c => c.Order == progressOrder) is { } progressChapter
            )
                return progressChapter;

            var pending = Chapters
                .OrderBy(c => c.Order)
                .FirstOrDefault(c => !progress.EditedOrders.Contains(c.Order));
            return pending ?? Chapters.OrderBy(c => c.Order).FirstOrDefault();
        }

        var ordered = Chapters.OrderBy(c => c.Order).ToList();
        return ordered.LastOrDefault(c => c.CharCount > 0) ?? ordered.FirstOrDefault();
    }

    public Chapter? FindNextChapter(CompactionTaskProgress? progress = null)
    {
        if (
            progress?.NextOrder is int progressOrder
            && Chapters.FirstOrDefault(c => c.Order == progressOrder) is { } progressChapter
        )
            return progressChapter;

        var current = FindFocusChapter(progress);
        var currentOrder = current?.Order ?? 0;
        return Chapters.Where(c => c.Order > currentOrder).OrderBy(c => c.Order).FirstOrDefault();
    }

    /// <summary>
    /// 只检查文本引用的章节/卷是否都在 Scope 内，不检查“写/修订”措辞。
    /// 用于从 Todo 清单选择下一步：Todo 的文案可能写“补写”，但不代表越界。
    /// </summary>
    public bool IsTextWithinScope(string? text)
    {
        var normalized = (text ?? "").ReplaceLineEndings(" ");
        foreach (Match match in ChapterRefPattern.Matches(normalized))
        {
            if (ParseNumber(match.Groups["n"].Value) is { } order && !ChapterOrders.Contains(order))
                return false;
        }

        foreach (Match match in VolumeOrderPattern.Matches(normalized))
        {
            if (ParseNumber(match.Groups["n"].Value) is { } order && !VolumeOrders.Contains(order))
                return false;
        }

        foreach (Match match in VolumeIdPattern.Matches(normalized))
        {
            if (!VolumeIds.Contains(match.Groups["id"].Value))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 检查摘要的 Next 是否仍在 Scope 内。明确出现范围外章节/卷直接拒绝；
    /// 修订任务里只出现“写/推进”而没有“优化/修订”也拒绝，防止把续写当前沿。
    /// </summary>
    public bool IsNextWithinScope(string next)
    {
        var text = next.ReplaceLineEndings(" ").Trim();
        if (text.Length == 0)
            return false;

        if (
            Mode == CompactionTaskMode.Revise
            && WriteActionPattern.IsMatch(text)
            && !ReviseActionPattern.IsMatch(text)
        )
        {
            return false;
        }

        foreach (Match match in ChapterRefPattern.Matches(text))
        {
            if (ParseNumber(match.Groups["n"].Value) is { } order && !ChapterOrders.Contains(order))
            {
                return false;
            }
        }

        foreach (Match match in VolumeOrderPattern.Matches(text))
        {
            if (
                VolumeOrders.Count > 0
                && ParseNumber(match.Groups["n"].Value) is { } order
                && !VolumeOrders.Contains(order)
            )
            {
                return false;
            }
        }

        foreach (Match match in VolumeIdPattern.Matches(text))
        {
            if (VolumeIds.Count > 0 && !VolumeIds.Contains(match.Groups["id"].Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 提取文本里引用的全部章节序号（“第N章”，含中文数字）。
    /// EnforceTaskScope 用它核对 Next 是否与 Progress 的下一章一致。
    /// </summary>
    public IReadOnlyList<int> ReferencedChapterOrders(string? text)
    {
        var orders = new List<int>();
        if (string.IsNullOrWhiteSpace(text))
            return orders;
        foreach (Match match in ChapterRefPattern.Matches(text.ReplaceLineEndings(" ")))
        {
            if (ParseNumber(match.Groups["n"].Value) is { } order && !orders.Contains(order))
                orders.Add(order);
        }
        return orders;
    }

    /// <summary>
    /// 生成范围内下一步。优先使用工具进度给出的下一章；没有进度时沿用上一份
    /// 合法的 Next，避免把“卷二的下一章”重置成泛化描述。
    /// </summary>
    public string BuildSafeNext(
        string? preferredNext = null,
        CompactionTaskProgress? progress = null
    )
    {
        if (
            progress?.NextOrder is int progressOrder
            && Chapters.FirstOrDefault(c => c.Order == progressOrder) is { } progressChapter
        )
            return BuildNextText(progressChapter);

        if (!string.IsNullOrWhiteSpace(preferredNext) && IsNextWithinScope(preferredNext))
            return preferredNext.Trim();

        if (Mode == CompactionTaskMode.Revise)
        {
            var pending = progress is null
                ? Chapters.FirstOrDefault(c => c.CharCount == 0)
                : Chapters
                    .OrderBy(c => c.Order)
                    .FirstOrDefault(c => !progress.EditedOrders.Contains(c.Order));
            pending ??= Chapters.OrderBy(c => c.Order).FirstOrDefault();
            if (pending is not null)
                return BuildNextText(pending);
            return "按 Scope 继续优化范围内章节；不要推进范围外内容。";
        }

        var target = Chapters.FirstOrDefault(c => c.CharCount == 0) ?? Chapters.FirstOrDefault();
        if (target is not null)
            return BuildNextText(target);
        return "按 Scope 继续写范围内章节；不要推进范围外内容。";
    }

    private string BuildNextText(Chapter target) =>
        Mode == CompactionTaskMode.Revise
            ? $"优化/补写第{target.Order}章《{target.Title}》；只在 Scope 范围内推进，不得跳到范围外。"
            : $"写第{target.Order}章《{target.Title}》；只写 Scope 范围内章节，不推进范围外内容。";

    private string AllowedRangeText()
    {
        var chapterText =
            MinChapterOrder == MaxChapterOrder
                ? $"第{MinChapterOrder}章"
                : $"第{MinChapterOrder}-{MaxChapterOrder}章";
        var chapterPart = $"{chapterText}，共{Chapters.Count}章";
        if (Volumes.Count == 0)
            return chapterPart;

        var volumePart = string.Join(
            "；",
            Volumes.OrderBy(v => v.Order).Select(v => $"第{v.Order}卷《{v.Name}》（ID:{v.Id}）")
        );
        return $"{volumePart}，{chapterPart}";
    }

    private static string ExtractInstruction(string source)
    {
        var match = AuthorInstructionPattern.Match(source);
        return match.Success ? match.Groups["text"].Value.Trim() : source.Trim();
    }

    private static bool IsNegated(string text, int index)
    {
        var start = Math.Max(0, index - 10);
        var prefix = text[start..index];
        return Regex.IsMatch(prefix, @"(?:不|别|勿|禁止|不得|无需)[^，。；]{0,6}$");
    }

    /// <summary>解析阿拉伯数字或中文数字（含“十/百”进位）；混入其它字符返回 null。</summary>
    internal static int? ParseNumber(string raw)
    {
        raw = raw.Trim();
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number;

        var digits = new Dictionary<char, int>
        {
            ['零'] = 0,
            ['〇'] = 0,
            ['一'] = 1,
            ['二'] = 2,
            ['两'] = 2,
            ['三'] = 3,
            ['四'] = 4,
            ['五'] = 5,
            ['六'] = 6,
            ['七'] = 7,
            ['八'] = 8,
            ['九'] = 9,
        };

        var total = 0;
        var current = 0;
        foreach (var ch in raw)
        {
            if (digits.TryGetValue(ch, out var digit))
            {
                current = digit;
                continue;
            }
            if (ch == '十')
            {
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
                continue;
            }
            if (ch == '百')
            {
                total += (current == 0 ? 1 : current) * 100;
                current = 0;
                continue;
            }
            return null;
        }
        return total + current;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
