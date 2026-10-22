using System.Text;

namespace Core.Vector;

/// <summary>
/// 中文文本语义分块器：约 300 个汉字目标长度，语义边界优先。
///
/// 边界优先级：章节小标题 > 场景分隔 > 空行段落 > 句末标点 > 保底字符切分。
/// 相邻块保留约 40-60 个汉字的上下文重叠。
/// 短段落合并，长段落按句子切分。
/// </summary>
public sealed class ChineseTextChunker
{
    /// <summary>目标块长度（汉字数）。</summary>
    public int TargetLength { get; init; } = 300;

    /// <summary>可接受的最小长度。</summary>
    public int MinLength { get; init; } = 220;

    /// <summary>可接受的最大长度。</summary>
    public int MaxLength { get; init; } = 380;

    /// <summary>相邻块重叠长度。</summary>
    public int OverlapLength { get; init; } = 50;

    /// <summary>场景分隔标记。</summary>
    private static readonly string[] SceneBreakPatterns = ["***", "---", "* * *", "— — —"];

    /// <summary>中文句末标点。</summary>
    private static readonly HashSet<char> SentenceEndPunctuation =
    [
        '。',
        '！',
        '？',
        '；',
        '…',
        '”',
        '』',
        '」',
    ];

    /// <summary>Markdown 标题前缀。</summary>
    private static readonly HashSet<char> HeadingPrefixes = ['#'];

    /// <summary>
    /// 将文本分块，返回块列表（含 offset、序号、边界质量）。
    ///
    /// 算法流程：
    /// 1. SplitParagraphs：按场景分隔、小标题、空行分割段落
    /// 2. GroupParagraphs：将短段落合并为段落组，边界由场景分隔/小标题决定
    /// 3. 对每个段落组：若 ≤ MaxLength 直接作为一块，否则 SplitLongGroup 按句子切分
    /// 4. SplitLongGroup：在句子边界切分，相邻块保留约 OverlapLength 字符的上下文重叠
    /// 5. SetAdjacentChunkIds：设置相邻块 ID 链接
    /// </summary>
    public List<TextChunk> Chunk(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var span = text.AsSpan();
        var paragraphs = SplitParagraphs(span);
        if (paragraphs.Count == 0)
            return [];

        var groups = GroupParagraphs(paragraphs, text);
        var chunks = new List<TextChunk>();
        var ordinal = 0;
        string? previousChunkId = null;

        foreach (var group in groups)
        {
            var groupText = group.GetText(text.AsSpan());
            var groupStartOffset = group.StartOffset;

            if (groupText.Length <= MaxLength)
            {
                var chunk = CreateChunk(
                    groupText,
                    groupStartOffset,
                    ordinal++,
                    group.BoundaryQuality,
                    ref previousChunkId
                );
                chunks.Add(chunk);
            }
            else
            {
                var subChunks = SplitLongGroup(
                    groupText,
                    groupStartOffset,
                    ordinal,
                    group.BoundaryQuality,
                    ref previousChunkId
                );
                ordinal += subChunks.Count;
                chunks.AddRange(subChunks);
            }
        }

        SetAdjacentChunkIds(chunks);
        return chunks;
    }

    /// <summary>
    /// 将文本分割为段落列表。
    /// 先检测场景分隔标记（*** / --- / * * * / — — —），这些标记强制分块阻断；
    /// 再检测 Markdown 小标题（# 开头）；最后按空行分割普通段落。
    /// </summary>
    private List<Paragraph> SplitParagraphs(ReadOnlySpan<char> span)
    {
        var paragraphs = new List<Paragraph>();
        var pos = 0;
        var len = span.Length;

        while (pos < len)
        {
            while (pos < len && char.IsWhiteSpace(span[pos]))
                pos++;

            if (pos >= len)
                break;

            var start = pos;

            // 场景分隔检测：检查 *** / --- / * * * / — — — 等标记
            // 场景标记强制分块，确保读者感知的场景切换处不断块
            var sceneBreakEnd = TryMatchSceneBreak(span, pos);
            if (sceneBreakEnd > pos)
            {
                paragraphs.Add(
                    new Paragraph
                    {
                        StartOffset = start,
                        EndOffset = sceneBreakEnd,
                        IsSceneBreak = true,
                    }
                );
                pos = sceneBreakEnd;
                continue;
            }

            if (pos < len && HeadingPrefixes.Contains(span[pos]))
            {
                var headingEnd = FindLineEnd(span, pos);
                paragraphs.Add(
                    new Paragraph
                    {
                        StartOffset = start,
                        EndOffset = headingEnd,
                        IsHeading = true,
                    }
                );
                pos = headingEnd;
                continue;
            }

            var end = FindParagraphEnd(span, pos);
            paragraphs.Add(new Paragraph { StartOffset = start, EndOffset = end });
            pos = end;
        }

        return paragraphs;
    }

    private List<ParagraphGroup> GroupParagraphs(List<Paragraph> paragraphs, string text)
    {
        var groups = new List<ParagraphGroup>();
        var currentGroup = new ParagraphGroup { BoundaryQuality = "heading" };

        foreach (var para in paragraphs)
        {
            if (para.IsSceneBreak || para.IsHeading)
            {
                if (currentGroup.Paragraphs.Count > 0)
                {
                    groups.Add(currentGroup);
                    currentGroup = new ParagraphGroup
                    {
                        BoundaryQuality = para.IsSceneBreak ? "scene_break" : "heading",
                    };
                }
                else
                {
                    currentGroup.BoundaryQuality = para.IsSceneBreak ? "scene_break" : "heading";
                }

                currentGroup.Paragraphs.Add(para);
                continue;
            }

            var paraText = text[para.StartOffset..para.EndOffset].Trim();
            var currentText = currentGroup.GetText(text.AsSpan());

            if (
                currentText.Length + paraText.Length + 1 <= MaxLength
                || currentGroup.Paragraphs.Count == 0
            )
            {
                currentGroup.Paragraphs.Add(para);
            }
            else
            {
                groups.Add(currentGroup);
                currentGroup = new ParagraphGroup
                {
                    BoundaryQuality = "blank_line",
                    Paragraphs = { para },
                };
            }
        }

        if (currentGroup.Paragraphs.Count > 0)
            groups.Add(currentGroup);

        return groups;
    }

    private List<TextChunk> SplitLongGroup(
        string groupText,
        int groupStartOffset,
        int startOrdinal,
        string boundaryQuality,
        ref string? previousChunkId
    )
    {
        var chunks = new List<TextChunk>();
        var sentences = SplitSentences(groupText.AsSpan());
        var ordinal = startOrdinal;

        var currentBuilder = new StringBuilder();
        var currentOffset = groupStartOffset;

        foreach (var sentence in sentences)
        {
            if (
                currentBuilder.Length > 0
                && currentBuilder.Length + sentence.Text.Length > MaxLength
            )
            {
                var chunkText = currentBuilder.ToString();
                var chunk = CreateChunk(
                    chunkText,
                    currentOffset,
                    ordinal++,
                    boundaryQuality,
                    ref previousChunkId
                );
                chunks.Add(chunk);

                var overlapStart = Math.Max(0, chunkText.Length - OverlapLength);
                var overlap = chunkText.AsSpan()[overlapStart..].ToString();
                currentBuilder.Clear();
                currentBuilder.Append(overlap);
                currentOffset += overlapStart;
            }

            currentBuilder.Append(sentence.Text);
        }

        if (currentBuilder.Length > 0)
        {
            var chunkText = currentBuilder.ToString();
            var chunk = CreateChunk(
                chunkText,
                currentOffset,
                ordinal++,
                "sentence_end",
                ref previousChunkId
            );
            chunks.Add(chunk);
        }

        return chunks;
    }

    private List<Sentence> SplitSentences(ReadOnlySpan<char> span)
    {
        var sentences = new List<Sentence>();
        var pos = 0;
        var len = span.Length;

        while (pos < len)
        {
            var start = pos;
            var lastSentenceEnd = -1;

            while (pos < len)
            {
                if (SentenceEndPunctuation.Contains(span[pos]))
                {
                    lastSentenceEnd = pos + 1;
                    while (
                        lastSentenceEnd < len
                        && (
                            span[lastSentenceEnd] == '…'
                            || span[lastSentenceEnd] == '"'
                            || span[lastSentenceEnd] == '』'
                            || span[lastSentenceEnd] == '」'
                        )
                    )
                        lastSentenceEnd++;

                    if (lastSentenceEnd - start >= MinLength || pos + 1 >= len)
                    {
                        pos = lastSentenceEnd;
                        break;
                    }
                }
                pos++;
            }

            if (lastSentenceEnd < 0 && pos >= len && pos - start > MaxLength)
            {
                pos = start + MaxLength;
                sentences.Add(
                    new Sentence { Text = span[start..pos].ToString(), BoundaryQuality = "forced" }
                );
                continue;
            }

            var end = pos > start ? pos : len;
            if (end > start)
            {
                sentences.Add(
                    new Sentence
                    {
                        Text = span[start..end].ToString(),
                        BoundaryQuality = lastSentenceEnd > 0 ? "sentence_end" : "forced",
                    }
                );
            }
        }

        return sentences;
    }

    private TextChunk CreateChunk(
        string text,
        int offset,
        int ordinal,
        string boundaryQuality,
        ref string? previousChunkId
    )
    {
        var chunk = new TextChunk
        {
            Text = text,
            SourceOffsetStart = offset,
            SourceOffsetEnd = offset + text.Length,
            ChunkOrdinal = ordinal,
            BoundaryQuality = boundaryQuality,
            PrevChunkId = previousChunkId,
        };
        previousChunkId = null;
        return chunk;
    }

    private static void SetAdjacentChunkIds(List<TextChunk> chunks)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            if (i > 0)
                chunks[i].PrevChunkId = chunks[i - 1].ChunkId;
            if (i < chunks.Count - 1)
                chunks[i].NextChunkId = chunks[i + 1].ChunkId;
        }
    }

    private static int TryMatchSceneBreak(ReadOnlySpan<char> span, int pos)
    {
        foreach (var pattern in SceneBreakPatterns)
        {
            if (pos + pattern.Length <= span.Length && span.Slice(pos).StartsWith(pattern.AsSpan()))
            {
                var end = pos + pattern.Length;
                while (end < span.Length && (span[end] == '\r' || span[end] == '\n'))
                    end++;
                return end;
            }
        }
        return pos;
    }

    private static int FindLineEnd(ReadOnlySpan<char> span, int pos)
    {
        while (pos < span.Length && span[pos] != '\r' && span[pos] != '\n')
            pos++;
        while (pos < span.Length && (span[pos] == '\r' || span[pos] == '\n'))
            pos++;
        return pos;
    }

    private static int FindParagraphEnd(ReadOnlySpan<char> span, int pos)
    {
        var len = span.Length;

        while (pos < len)
        {
            if (pos + 1 < len && span[pos] == '\n' && span[pos + 1] == '\n')
                return pos + 2;
            if (
                pos + 2 < len
                && span[pos] == '\r'
                && span[pos + 1] == '\n'
                && span[pos + 2] == '\n'
            )
                return pos + 3;
            pos++;
        }

        return len;
    }

    // ── 内部类型 ──

    private sealed class Paragraph
    {
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }
        public bool IsSceneBreak { get; set; }
        public bool IsHeading { get; set; }
    }

    private sealed class ParagraphGroup
    {
        public List<Paragraph> Paragraphs { get; init; } = [];
        public string BoundaryQuality { get; set; } = "blank_line";

        public string GetText(ReadOnlySpan<char> fullText)
        {
            if (Paragraphs.Count == 0)
                return "";
            var start = Paragraphs[0].StartOffset;
            var end = Paragraphs[^1].EndOffset;
            return fullText[start..end].Trim().ToString();
        }

        public int StartOffset => Paragraphs.Count > 0 ? Paragraphs[0].StartOffset : 0;
    }

    private sealed class Sentence
    {
        public string Text { get; set; } = "";
        public string BoundaryQuality { get; set; } = "sentence_end";
    }
}

/// <summary>
/// 文本块：分块结果。
/// </summary>
public sealed class TextChunk
{
    /// <summary>块文本内容。</summary>
    public string Text { get; set; } = "";

    /// <summary>原文起始 offset。</summary>
    public int SourceOffsetStart { get; set; }

    /// <summary>原文结束 offset。</summary>
    public int SourceOffsetEnd { get; set; }

    /// <summary>块序号。</summary>
    public int ChunkOrdinal { get; set; }

    /// <summary>边界质量。</summary>
    public string BoundaryQuality { get; set; } = "sentence_end";

    /// <summary>块 ID（由外部设置，基于 document_id + ordinal）。</summary>
    public string ChunkId { get; set; } = "";

    /// <summary>前一块 ID。</summary>
    public string? PrevChunkId { get; set; }

    /// <summary>后一块 ID。</summary>
    public string? NextChunkId { get; set; }
}
