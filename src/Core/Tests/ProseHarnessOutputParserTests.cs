using Core.Prose;
using Xunit;

namespace Core.Tests;

public sealed class ProseHarnessOutputParserTests
{
    [Fact]
    public void Parse_ExtractsRevisedChapterAndNotes()
    {
        var raw =
            "说明\n<revised_chapter>\n甲\n\n---\n\n乙\n</revised_chapter>\n"
            + "<separator_notes>保留场景切换</separator_notes>";

        var parsed = ProseHarnessOutputParser.Parse(raw);

        Assert.Equal("甲\n\n---\n\n乙", parsed.Content);
        Assert.Equal("保留场景切换", parsed.Notes);
    }

    [Fact]
    public void Parse_FallsBackToCodeFence()
    {
        var parsed = ProseHarnessOutputParser.Parse("```markdown\n甲\n\n---\n\n乙\n```");

        Assert.Equal("甲\n\n---\n\n乙", parsed.Content);
        Assert.Equal("", parsed.Notes);
    }

    [Fact]
    public void Parse_EmptyResponse_ReturnsEmptyContent()
    {
        var parsed = ProseHarnessOutputParser.Parse("   ");

        Assert.Equal("", parsed.Content);
        Assert.Equal("", parsed.Notes);
    }
}
