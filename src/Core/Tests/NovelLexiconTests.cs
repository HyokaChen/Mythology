using Core.Vector;
using Xunit;

namespace Core.Tests;

public sealed class NovelLexiconTests
{
    [Fact]
    public void Update_KeepsEntityNamesWhole()
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

    [Fact]
    public void Update_RegistersTwoCharNameAboveFrequencyThreshold()
    {
        NovelLexicon.Clear();
        try
        {
            // 两字名阈值 = f_A·f_B / Total；善我 需 > 31.9，freq=10 会输，freq=1e5 才整名
            NovelLexicon.Update("novel-freq", ["善我", "恶我"]);
            var tokens = LexicalText
                .ToIndexText("善我和恶我对峙", "novel-freq")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Assert.Contains("善我", tokens);
            Assert.Contains("恶我", tokens);
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }

    [Fact]
    public void Update_SquashesWhitespaceAndSplitsOnPunctuation()
    {
        NovelLexicon.Clear();
        try
        {
            // 小森 星罗 → 小森星罗；恋爱吧！折花君 → 恋爱吧 + 折花君（整名永不出现在正文）
            NovelLexicon.Update("novel-norm", ["小森 星罗", "恋爱吧！折花君"]);

            var tokens = LexicalText
                .ToIndexText("小森星罗遇见了折花君", "novel-norm")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            Assert.Contains("小森星罗", tokens);
            Assert.Contains("折花君", tokens);
            Assert.DoesNotContain("恋爱吧折花君", tokens);
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }

    [Fact]
    public void GetVersion_IsGlobalAndChangesWithNames()
    {
        NovelLexicon.Clear();
        try
        {
            var initial = NovelLexicon.GetVersion("anything");
            NovelLexicon.Update("novel-v1", ["皮皮龙"]);
            var afterFirst = NovelLexicon.GetVersion("other-novel");
            Assert.NotEqual(initial, afterFirst);

            // 词典是进程全局的：注册在 A 的名字，B 的版本也随之变化
            NovelLexicon.Update("novel-v2", ["松雪美乃"]);
            Assert.NotEqual(afterFirst, NovelLexicon.GetVersion("novel-v1"));
        }
        finally
        {
            NovelLexicon.Clear();
        }
    }
}
