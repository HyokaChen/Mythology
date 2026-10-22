using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;
using MythologyAgent = Core.Agent.Agent;

namespace Core.Tests;

/// <summary>
/// Agent.CurrentNovelId 会话切换行为测试。
/// 验证切换小说时清空 ToolInjector 的已展开工具缓存（设计文档 §10.1）。
/// </summary>
public sealed class AgentNovelSwitchTests
{
    [Fact]
    /// <summary>切换小说（CurrentNovelId 变化）应清空已展开工具缓存。</summary>
    public async Task CurrentNovelIdChange_ClearsExpandedTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool("manage_character", "管理角色", "创建更新角色", ["角色管理"], []),
            "Novel Writing Tools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var injector = new ToolInjector(registry, engine);
        var agent = new MythologyAgent(registry) { ToolInjector = injector };

        // 展开工具
        await injector.ExpandAsync("角色管理", topK: 5, TestContext.Current.CancellationToken);
        Assert.True(injector.ExpandedTools.ContainsKey("manage_character"));

        // 切换小说 → 清空
        agent.CurrentNovelId = "novel-A";
        agent.CurrentNovelId = "novel-B";
        Assert.False(injector.ExpandedTools.ContainsKey("manage_character"));
    }

    [Fact]
    /// <summary>同一小说 ID 重复设置不应清空（无意义切换）。</summary>
    public async Task CurrentNovelIdSameValue_KeepsExpandedTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool("manage_character", "管理角色", "创建更新角色", ["角色管理"], []),
            "Novel Writing Tools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var injector = new ToolInjector(registry, engine);
        var agent = new MythologyAgent(registry) { ToolInjector = injector };

        // 先完成一次小说切换（null → novel-A，会清空一次），再展开
        agent.CurrentNovelId = "novel-A";
        await injector.ExpandAsync("角色管理", topK: 5, TestContext.Current.CancellationToken);
        agent.CurrentNovelId = "novel-A"; // 相同值，不应清空

        Assert.True(injector.ExpandedTools.ContainsKey("manage_character"));
    }

    // ── 辅助类 ──

    private sealed class DummyTool(
        string name,
        string label,
        string description,
        string[]? aliases = null,
        string[]? examples = null
    ) : AgentTool
    {
        public override string Name => name;
        public override string Label => label;
        public override string Description => description;
        public override string[] Aliases => aliases ?? [];
        public override string[] ExampleQueries => examples ?? [];
        public override System.Text.Json.JsonElement? Parameters => null;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            System.Text.Json.JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        )
        {
            return Task.FromResult(
                new AgentToolResult { Content = [new TextContent($"ok: {name}")] }
            );
        }
    }
}
