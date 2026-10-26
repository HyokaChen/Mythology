using Core.Vector;
using Xunit;

namespace Core.Tests;

public sealed class NovelLexiconTests
{
    [Fact]
    public void Update_KeepsEntityNamesWholeForThatNovel()
    {
        NovelLexicon.Clear();
        try
        {
            Assert.True(NovelLexicon.Update("novel-lex", ["皮皮龙", "松雪美乃", "善我"]));

            var tokens = LexicalText
                .ToIndexText("皮皮龙和松雪美乃的关系", "novel-lex")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Assert.Contains("皮皮龙", tokens);
            Assert.Contains("松雪美乃", tokens);
            Assert.False(NovelLexicon.Update("novel-lex", ["皮皮龙", "松雪美乃", "善我"]));
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }
}
