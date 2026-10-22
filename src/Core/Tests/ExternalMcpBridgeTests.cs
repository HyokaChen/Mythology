using System.Text.Json;
using Core.Data;
using Core.Mcp;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ExternalMcpBridge / ExternalMcpConnection / ExternalMcpTool 测试（桩 IJsonRpcTransport）。
/// 验证：连接/工具发现/名称清洗/disabled 跳过/单台失败不阻断整体/工具调用映射。
/// </summary>
public sealed class ExternalMcpBridgeTests
{
    // ── 桩传输：按 JSON-RPC method 返回预制响应 ──

    private sealed class StubTransport : IJsonRpcTransport
    {
        private readonly string _toolsJson;
        private readonly string _callResultJson;
        private readonly bool _failInit;
        private readonly bool _callIsError;

        public bool IsConnected => true;
        public string? LastRequest { get; private set; }

        public StubTransport(
            string toolsJson = DefaultTools,
            string? callResultJson = null,
            bool failInit = false,
            bool callIsError = false
        )
        {
            _toolsJson = toolsJson;
            _callResultJson = callResultJson ?? DefaultCallResult;
            _failInit = failInit;
            _callIsError = callIsError;
        }

        public Task<string> SendAsync(string requestJson, CancellationToken ct = default)
        {
            LastRequest = requestJson;
            using var doc = JsonDocument.Parse(requestJson);
            var method = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = doc.RootElement.GetProperty("id").GetInt32();

            return Task.FromResult(
                method switch
                {
                    "initialize" => _failInit
                        ? Error(id, -32603, "init failed")
                        : Ok(
                            id,
                            new
                            {
                                protocolVersion = "2025-06-18",
                                capabilities = new { tools = new { } },
                                serverInfo = new { name = "stub" },
                            }
                        ),
                    "tools/list" => Ok(
                        id,
                        new { tools = JsonDocument.Parse(_toolsJson).RootElement.Clone() }
                    ),
                    "tools/call" => _callIsError
                        ? Ok(
                            id,
                            new
                            {
                                content = new[] { new { type = "text", text = "boom" } },
                                isError = true,
                            }
                        )
                        : Ok(id, JsonDocument.Parse(_callResultJson).RootElement.Clone()),
                    _ => Error(id, -32601, "method not found"),
                }
            );
        }

        private static string Ok(int id, object result) =>
            JsonSerializer.Serialize(
                new
                {
                    jsonrpc = "2.0",
                    id,
                    result,
                }
            );

        private static string Error(int id, int code, string message) =>
            JsonSerializer.Serialize(
                new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code, message },
                }
            );

        public Task DisconnectAsync() => Task.CompletedTask;

        public const string DefaultTools =
            """[{"name":"fetch_page","description":"抓取网页","inputSchema":{"type":"object","properties":{"url":{"type":"string"}}}}]""";
        public const string DefaultCallResult =
            """{"content":[{"type":"text","text":"页面内容 OK"}]}""";
    }

    private static McpServerEntry Enabled(string name, string transport = "http") =>
        new()
        {
            Name = name,
            Transport = transport,
            Url = "http://stub/mcp",
            Command = "",
            Enabled = true,
        };

    private static ExternalMcpBridge Bridge(StubTransport transport) => new(_ => transport);

    // ── 测试 ──

    [Fact]
    public async Task ConnectAsync_ReturnsWrappedTools()
    {
        var bridge = Bridge(new StubTransport());
        var tools = await bridge.ConnectAsync([Enabled("test-server")], CancellationToken.None);

        var tool = Assert.Single(tools);
        Assert.Equal("mcp_test-server_fetch_page", tool.Name);
        Assert.True(tool.DeferLoading);
        Assert.Contains("test-server", tool.Description);
        Assert.NotNull(tool.Parameters);
        Assert.Contains("fetch_page", tool.Label);
    }

    [Fact]
    public async Task ConnectAsync_DisabledServer_Skipped()
    {
        var bridge = Bridge(new StubTransport());
        var disabled = Enabled("disabled-server");
        disabled.Enabled = false;
        var servers = new List<McpServerEntry> { disabled };
        var tools = await bridge.ConnectAsync(servers, CancellationToken.None);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task ConnectAsync_BrokenServer_OtherContinues()
    {
        var bridge = Bridge(new StubTransport(failInit: true));
        // 同一工厂桩 → 两台都 failInit；用两个不同桩验证「单台失败跳过」：
        var mixed = new ExternalMcpBridge(entry =>
            entry.Name == "broken" ? new StubTransport(failInit: true) : new StubTransport()
        );
        var servers = new List<McpServerEntry> { Enabled("broken"), Enabled("good") };
        var tools = await mixed.ConnectAsync(servers, CancellationToken.None);
        var tool = Assert.Single(tools);
        Assert.StartsWith("mcp_good_", tool.Name);
    }

    [Fact]
    public async Task ExternalMcpTool_ExecuteAsync_ReturnsContentText()
    {
        var bridge = Bridge(new StubTransport());
        var tools = await bridge.ConnectAsync([Enabled("svr")], CancellationToken.None);
        var tool = Assert.Single(tools);

        var result = await tool.ExecuteAsync(
            "call-1",
            JsonDocument.Parse("{}").RootElement,
            CancellationToken.None
        );
        Assert.False(result.IsError);
        var text = Assert.IsType<Core.Shared.TextContent>(Assert.Single(result.Content));
        Assert.Contains("页面内容 OK", text.Text);
    }

    [Fact]
    public async Task ExternalMcpTool_ExecuteAsync_IsError_MapsToErrorResult()
    {
        var bridge = Bridge(new StubTransport(callIsError: true));
        var tools = await bridge.ConnectAsync([Enabled("svr")], CancellationToken.None);
        var tool = Assert.Single(tools);

        var result = await tool.ExecuteAsync(
            "call-1",
            JsonDocument.Parse("{}").RootElement,
            CancellationToken.None
        );
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExternalMcpTool_ExecuteAsync_TransportThrows_ReturnsErrorNotThrow()
    {
        // 工具执行时传输抛异常（连接断开/超时）→ 返回 IsError 可读文本，不抛给调用方。
        var transport = new ThrowingCallTransport();
        var connection = new ExternalMcpConnection("svr", transport);
        var descriptor = new McpToolDescriptor(
            "fetch_page",
            "抓取网页",
            JsonDocument.Parse("""{"type":"object"}""").RootElement
        );
        var tool = new ExternalMcpTool(connection, "svr", descriptor);

        var result = await tool.ExecuteAsync(
            "call-1",
            JsonDocument.Parse("{}").RootElement,
            CancellationToken.None
        );
        Assert.True(result.IsError);
        var text = Assert.IsType<Core.Shared.TextContent>(Assert.Single(result.Content));
        Assert.Contains("外部 MCP 工具调用失败", text.Text);
    }

    [Fact]
    public async Task NameSanitization_IllegalCharsReplaced()
    {
        var bridge = Bridge(new StubTransport());
        var tools = await bridge.ConnectAsync([Enabled("test server!@#")], CancellationToken.None);
        var tool = Assert.Single(tools);
        Assert.Equal("mcp_test_server____fetch_page", tool.Name);
        Assert.DoesNotContain(" ", tool.Name);
    }

    [Fact]
    public async Task DuplicateToolNames_SameServer_GetUniqueSuffix()
    {
        // 同一服务器返回两个同名工具 → 第二个应分配 _2 后缀（否则 registry 按名覆盖丢一个）
        var transport = new StubTransport(
            """[{"name":"dup","description":"a"},{"name":"dup","description":"b"}]"""
        );
        var bridge = Bridge(transport);
        var tools = await bridge.ConnectAsync([Enabled("svr")], CancellationToken.None);
        Assert.Equal(2, tools.Count);
        Assert.Equal("mcp_svr_dup", tools[0].Name);
        Assert.Equal("mcp_svr_dup_2", tools[1].Name);
    }

    [Fact]
    public async Task DistinctTools_AcrossServers_AllRegistered()
    {
        // 两台服务器工具名不同 → 全部保留、无后缀误伤
        var mixed = new ExternalMcpBridge(entry =>
        {
            var toolName = entry.Name == "svr-a" ? "fetch" : "search";
            return new StubTransport(
                JsonSerializer.Serialize(new[] { new { name = toolName, description = "tool" } })
            );
        });
        var servers = new List<McpServerEntry> { Enabled("svr-a"), Enabled("svr-b") };
        var tools = await mixed.ConnectAsync(servers, CancellationToken.None);
        Assert.Equal(2, tools.Count);
        Assert.Equal("mcp_svr-a_fetch", tools[0].Name);
        Assert.Equal("mcp_svr-b_search", tools[1].Name);
    }

    /// <summary>tools/list 正常但 tools/call 抛异常的桩：验证执行失败降级为 IsError。</summary>
    private sealed class ThrowingCallTransport : IJsonRpcTransport
    {
        public bool IsConnected => true;

        public Task<string> SendAsync(string requestJson, CancellationToken ct = default)
        {
            using var doc = JsonDocument.Parse(requestJson);
            var method = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
            if (method == "tools/call")
                throw new InvalidOperationException("连接已断开");
            // initialize/tools/list 正常返回
            var id = doc.RootElement.GetProperty("id").GetInt32();
            var result =
                method == "initialize"
                    ? new { protocolVersion = "2025-06-18" }
                    : (object)new { tools = Array.Empty<object>() };
            return Task.FromResult(
                JsonSerializer.Serialize(
                    new
                    {
                        jsonrpc = "2.0",
                        id,
                        result,
                    }
                )
            );
        }

        public Task DisconnectAsync() => Task.CompletedTask;
    }
}

/// <summary>
/// Feature 核心承诺的端到端验证：外部 MCP 工具注册进 ToolRegistry 后——
/// ① DeferLoading=true → 不进注入面（每轮 LLM 请求工具列表）；
/// ② 进入 tool_search 索引，中文/关键词 query 可命中；
/// ③ 命中后 ExpandHits → 下一轮注入面包含该工具（可被 LLM 直接调用）。
/// 这 3 点才是「融合进 LLM Tornado 的 tool_search 链路」的验收语义。
/// </summary>
public sealed class ExternalMcpToolSearchIntegrationTests
{
    private sealed class SimpleStub : IJsonRpcTransport
    {
        public bool IsConnected => true;

        public Task<string> SendAsync(string requestJson, CancellationToken ct = default)
        {
            using var doc = JsonDocument.Parse(requestJson);
            var method = doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
            var id = doc.RootElement.GetProperty("id").GetInt32();
            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "stub" },
                },
                "tools/list" => new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "fetch_page",
                            description = "抓取网页内容为 Markdown",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new { url = new { type = "string" } },
                            },
                        },
                    },
                },
                "tools/call" => new { content = new[] { new { type = "text", text = "ok" } } },
                _ => new { },
            };
            return Task.FromResult(
                JsonSerializer.Serialize(
                    new
                    {
                        jsonrpc = "2.0",
                        id,
                        result,
                    }
                )
            );
        }

        public Task DisconnectAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task ExternalMcpTool_Deferred_NotInjected_DiscoverableAndExpandable()
    {
        // ── 组装：registry + 桥接注册 External MCP 工具 + Tool Search 基建（镜像 Host 接线）──
        var registry = new ToolRegistry();
        // tool_search 常驻工具
        registry.Register(new StubTool("tool_search", "搜索工具", "按需检索工具"), "Core");
        // 一个普通常驻写作工具
        registry.Register(new StubTool("write_chapter", "写章节", "写入章节内容"), "NovelTools");

        var bridge = new ExternalMcpBridge(_ => new SimpleStub());
        var servers = new List<McpServerEntry>
        {
            new()
            {
                Name = "web",
                Transport = "http",
                Url = "http://stub/mcp",
                Enabled = true,
            },
        };
        var mcpTools = await bridge.ConnectAsync(servers, CancellationToken.None);
        Assert.Single(mcpTools);
        registry.RegisterRange(mcpTools, "External MCP");

        // ── Tool Search 基建（与 HostBootstrapper 相同的 deferredPluginNames 语义）──
        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        var injector = new ToolInjector(
            registry,
            engine,
            deferredPluginNames: new HashSet<string>(
                ["Semantic Retrieval Tool", "Web Search Tools"],
                StringComparer.OrdinalIgnoreCase
            )
        );
        await engine.BuildIndexAsync(registry, CancellationToken.None);

        // ① DeferLoading=true → 不进注入面
        var injectedNames = injector.BuildRequestTools().Select(t => t.Name).ToArray();
        Assert.DoesNotContain("mcp_web_fetch_page", injectedNames);
        // tool_search / 非延迟工具仍在
        Assert.Contains("tool_search", injectedNames);
        Assert.Contains("write_chapter", injectedNames);

        // ② tool_search 索引命中（关键词 query）
        var searchTool = new ToolSearchTool(engine, injector);
        var args = JsonDocument.Parse("""{"query":"抓取网页"}""").RootElement;
        var result = await searchTool.ExecuteAsync("call-1", args, CancellationToken.None);
        var typed = Assert.IsType<AgentToolResult<ToolSearchDetails>>(result);
        Assert.NotNull(typed.TypedDetails);
        Assert.Contains("mcp_web_fetch_page", typed.TypedDetails!.ToolNames);

        // ③ 命中后展开 → 下一轮注入面包含该工具
        var nextInjected = injector.BuildRequestTools().Select(t => t.Name).ToArray();
        Assert.Contains("mcp_web_fetch_page", nextInjected);
    }

    /// <summary>测试用最小工具（非延迟，模拟常驻工具）。</summary>
    private sealed class StubTool(string name, string label, string description) : AgentTool
    {
        public override string Name => name;
        public override string Label => label;
        public override string Description => description;
        public override JsonElement? Parameters => null;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        ) => Task.FromResult(new AgentToolResult { Content = [new TextContent("ok")] });
    }
}
