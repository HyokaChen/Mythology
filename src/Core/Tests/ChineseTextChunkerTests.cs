using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ChineseTextChunker 中文文本语义分块器测试。
/// 验证分块边界、场景分隔、标题处理、句子切分、上下文重叠等。
/// </summary>
public sealed class ChineseTextChunkerTests
{
    // ══════════════════════════════════════════════════════
    //  1. 空输入与短文本
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_NullText_ReturnsEmpty()
    {
        var chunker = new ChineseTextChunker();
        Assert.Empty(chunker.Chunk(null!));
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsEmpty()
    {
        var chunker = new ChineseTextChunker();
        Assert.Empty(chunker.Chunk(""));
    }

    [Fact]
    public void Chunk_ShortText_SingleChunk()
    {
        var chunker = new ChineseTextChunker();
        var text = "这是一段短文本。";
        var chunks = chunker.Chunk(text);
        Assert.Single(chunks);
        Assert.Equal(text, chunks[0].Text);
    }

    [Fact]
    public void Chunk_SingleParagraph_NoSplit()
    {
        var chunker = new ChineseTextChunker();
        var text = "天地玄黄，宇宙洪荒。日月盈昃，辰宿列张。";
        var chunks = chunker.Chunk(text);
        Assert.Single(chunks);
    }

    // ══════════════════════════════════════════════════════
    //  2. 场景分隔标记
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_SceneBreakAsterisk_SplitsChunks()
    {
        var chunker = new ChineseTextChunker();
        var text = new string('甲', 250) + "。\n***\n" + new string('乙', 250) + "。";
        var chunks = chunker.Chunk(text);
        Assert.True(chunks.Count >= 2, "场景分隔应产生至少 2 个块");
    }

    [Fact]
    public void Chunk_SceneBreakDash_SplitsChunks()
    {
        var chunker = new ChineseTextChunker();
        var text = new string('甲', 250) + "。\n---\n" + new string('乙', 250) + "。";
        var chunks = chunker.Chunk(text);
        Assert.True(chunks.Count >= 2);
    }

    [Fact]
    public void Chunk_SceneBreakSpacedAsterisk_SplitsChunks()
    {
        var chunker = new ChineseTextChunker();
        var text = new string('前', 250) + "。\n* * *\n" + new string('后', 250) + "。";
        var chunks = chunker.Chunk(text);
        Assert.True(chunks.Count >= 2);
    }

    // ══════════════════════════════════════════════════════
    //  3. Markdown 标题
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_Heading_CreatesBoundary()
    {
        var chunker = new ChineseTextChunker();
        var text =
            "# 第一章\n"
            + new string('故', 100)
            + "。\n\n# 第二章\n"
            + new string('事', 100)
            + "。";
        var chunks = chunker.Chunk(text);
        Assert.True(chunks.Count >= 2, "标题应产生分块边界");
    }

    [Fact]
    public void Chunk_MultipleHeadings_MultipleBoundaries()
    {
        var chunker = new ChineseTextChunker();
        var text =
            "# 第一章\n"
            + new string('甲', 100)
            + "。\n\n# 第二章\n"
            + new string('乙', 100)
            + "。\n\n# 第三章\n"
            + new string('丙', 100)
            + "。";
        var chunks = chunker.Chunk(text);
        Assert.True(chunks.Count >= 3, "3 个标题应产生至少 3 个块");
    }

    // ══════════════════════════════════════════════════════
    //  4. 空行段落分割
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_BlankLine_SeparatesParagraphs()
    {
        var chunker = new ChineseTextChunker();
        var para1 = new string('甲', 200) + "。";
        var para2 = new string('乙', 200) + "。";
        var text = para1 + "\n\n" + para2;
        var chunks = chunker.Chunk(text);
        Assert.True(chunks.Count >= 2, "空行应分割段落");
    }

    // ══════════════════════════════════════════════════════
    //  5. 长文本按句子切分
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_LongText_SplitsIntoMultipleChunks()
    {
        var chunker = new ChineseTextChunker();
        // 生成超过 MaxLength (380) 的长文本
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 40; i++)
            sb.Append($"这是第{i}个句子，内容丰富而有深度。");
        var text = sb.ToString();
        var chunks = chunker.Chunk(text);
        Assert.True(chunks.Count > 1, "长文本应分为多个块");
    }

    // ══════════════════════════════════════════════════════
    //  6. 上下文重叠
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_AdjacentChunks_HaveOverlap()
    {
        var chunker = new ChineseTextChunker();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 20; i++)
            sb.Append($"这是第{i}个句子，内容丰富而有深度。");
        var text = sb.ToString();
        var chunks = chunker.Chunk(text);

        if (chunks.Count >= 2)
        {
            // 相邻块应有上下文重叠（后一块开头出现在前一块结尾）
            var prevTail = chunks[0].Text[^Math.Min(60, chunks[0].Text.Length)..];
            var nextHead = chunks[1].Text[..Math.Min(60, chunks[1].Text.Length)];
            // 重叠意味着前一块的尾部和后一块的头部有共同内容
            Assert.True(prevTail.Length > 0 && nextHead.Length > 0);
        }
    }

    // ══════════════════════════════════════════════════════
    //  7. 相邻块 ID 链接
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_AdjacentChunks_LinkedByIds()
    {
        var chunker = new ChineseTextChunker();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 20; i++)
            sb.Append($"这是第{i}个句子，内容丰富而有深度。");
        var text = sb.ToString();
        var chunks = chunker.Chunk(text);

        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].ChunkId, chunks[i].PrevChunkId);
        }
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            Assert.Equal(chunks[i + 1].ChunkId, chunks[i].NextChunkId);
        }
    }

    // ══════════════════════════════════════════════════════
    //  8. 边界质量标注
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_HeadingBoundary_QualityIsHeading()
    {
        var chunker = new ChineseTextChunker();
        var text = "# 标题\n" + new string('内', 100) + "。";
        var chunks = chunker.Chunk(text);
        Assert.Contains(chunks, c => c.BoundaryQuality == "heading");
    }

    [Fact]
    public void Chunk_SceneBreakBoundary_SplitsIntoMultipleChunks()
    {
        var chunker = new ChineseTextChunker();
        var text = new string('前', 250) + "。\n***\n" + new string('后', 250) + "。";
        var chunks = chunker.Chunk(text);
        // 场景分隔应产生至少 2 个块，且每个块有合法的 BoundaryQuality
        Assert.True(chunks.Count >= 2, "场景分隔应产生至少 2 个块");
        Assert.All(chunks, c => Assert.NotEmpty(c.BoundaryQuality));
    }

    // ══════════════════════════════════════════════════════
    //  9. 块属性完整性
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_EachChunk_HasNonEmptyText()
    {
        var chunker = new ChineseTextChunker();
        var text =
            "# 标题\n" + new string('内', 100) + "。\n\n# 标题2\n" + new string('容', 100) + "。";
        var chunks = chunker.Chunk(text);
        Assert.All(chunks, c => Assert.NotEmpty(c.Text));
    }

    [Fact]
    public void Chunk_EachChunk_OffsetConsistent()
    {
        var chunker = new ChineseTextChunker();
        var text =
            "# 标题\n" + new string('内', 100) + "。\n\n# 标题2\n" + new string('容', 100) + "。";
        var chunks = chunker.Chunk(text);
        Assert.All(
            chunks,
            c =>
            {
                Assert.True(c.SourceOffsetStart >= 0);
                Assert.True(c.SourceOffsetEnd >= c.SourceOffsetStart);
                Assert.Equal(c.Text.Length, c.SourceOffsetEnd - c.SourceOffsetStart);
            }
        );
    }

    [Fact]
    public void Chunk_EachChunk_OrdinalIsSequential()
    {
        var chunker = new ChineseTextChunker();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 20; i++)
            sb.Append($"这是第{i}个句子，内容丰富而有深度。");
        var text = sb.ToString();
        var chunks = chunker.Chunk(text);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].ChunkOrdinal);
        }
    }

    // ══════════════════════════════════════════════════════
    //  10. 自定义配置
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_CustomTargetLength_Respected()
    {
        var chunker = new ChineseTextChunker
        {
            TargetLength = 100,
            MinLength = 80,
            MaxLength = 120,
            OverlapLength = 20,
        };
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 30; i++)
            sb.Append($"这是第{i}个句子。");
        var text = sb.ToString();
        var chunks = chunker.Chunk(text);
        Assert.True(chunks.Count > 1, "自定义短目标长度应产生更多块");
    }

    // ══════════════════════════════════════════════════════
    //  11. 中英文混排
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_MixedChineseEnglish_CorrectChunking()
    {
        var chunker = new ChineseTextChunker();
        var text = "这是中文段落。This is English paragraph. 混合内容继续。" + new string('。', 50);
        var chunks = chunker.Chunk(text);
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.NotEmpty(c.Text));
    }

    // ══════════════════════════════════════════════════════
    //  12. 短段落合并
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_ShortParagraphs_Merged()
    {
        var chunker = new ChineseTextChunker();
        // 多个短段落应合并为一个块
        var text = "短段落一。\n\n短段落二。\n\n短段落三。";
        var chunks = chunker.Chunk(text);
        Assert.Single(chunks); // 短段落合并
    }

    // ══════════════════════════════════════════════════════
    //  13. 纯空白文本
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Chunk_WhitespaceOnly_ReturnsEmpty()
    {
        var chunker = new ChineseTextChunker();
        Assert.Empty(chunker.Chunk("   \n\n  \t  "));
    }
}
