using Core.Data;
using Core.Shared;
using Core.Tools;
using Xunit;
using AgentType = Core.Agent.Agent;

namespace Core.Tests;

/// <summary>
/// decompress 工具作用域回归测试。
/// 背景：模型误以为"章节内容变少"= "被上下文压缩"，拿 decompress 找章节原文。
/// decompress 只恢复被上下文压缩（compact）折叠的对话消息，与章节/文件内容无关。
/// 此测试锁定：无压缩块时错误消息必须澄清作用域，防止模型继续误用。
/// </summary>
public sealed class DecompressToolTests
{
    [Fact]
    public async Task NoCompactBlocks_ErrorClarifiesScope_ChapterFilesAreNotBlocks()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"myth-decompress-{Guid.NewGuid():N}");
        try
        {
            var agent = new AgentType(new ToolRegistry(), tornadoApi: null, logger: null)
            {
                SessionId = "test-session",
            };
            var store = new FileSessionStore(
                novelsRoot: Path.Combine(tempRoot, "novels"),
                mythologyDir: tempRoot
            );
            var tool = new DecompressTool(agent, store);

            var result = await tool.ExecuteAsync(
                "call_1",
                new DecompressParams(),
                CancellationToken.None,
                onUpdate: null
            );

            Assert.True(result.IsError);
            var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
            Assert.Contains("当前没有可恢复的压缩块", text);
            // 作用域澄清：压缩块只保存被折叠的对话消息，与章节/文件内容无关
            Assert.Contains("对话消息", text);
            Assert.Contains("章节", text);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Description_ScopesToolToConversationMessages_NotChapterFiles()
    {
        var tool = new DecompressTool(
            new AgentType(new ToolRegistry(), tornadoApi: null, logger: null),
            new FileSessionStore(novelsRoot: Path.GetTempPath(), mythologyDir: Path.GetTempPath())
        );

        var description = tool.Description;
        // 工具描述必须明确：只恢复对话消息压缩块；章节是 read 读取的磁盘文件
        Assert.Contains("对话消息", description);
        Assert.Contains("章节", description);
        Assert.Contains("read", description);
    }
}
