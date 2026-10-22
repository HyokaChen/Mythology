using System.Text.Json;
using Core.Shared;
using Core.Tools;

namespace Core.Mcp;

/// <summary>
/// 外部 MCP 工具的 AgentTool 包装 — 继承非泛型 <see cref="AgentTool"/> 基类。
/// 参数绕过泛型反序列化，以 <see cref="JsonElement"/> 原样透传给 MCP 服务器。
/// 所有 MCP 工具默认 <see cref="AgentTool.DeferLoading"/> = true，仅经 tool_search 按需发现。
/// </summary>
public sealed class ExternalMcpTool : AgentTool
{
    private readonly ExternalMcpConnection _connection;
    private readonly string _remoteName;

    /// <summary>工具名：mcp_{server}_{remoteName}，字符白名单 [a-zA-Z0-9_-]。</summary>
    public override string Name { get; }

    public override string Label { get; }

    public override string Description { get; }

    public override JsonElement? Parameters { get; }

    /// <summary>外部工具一律延迟加载，不预注入 LLM 上下文。</summary>
    public override bool DeferLoading => true;

    /// <summary>外部工具归为通用类别。</summary>
    public override ToolCategory Category => ToolCategory.Generic;

    /// <summary>外部工具可并行执行。</summary>
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Parallel;

    public ExternalMcpTool(
        ExternalMcpConnection connection,
        string serverName,
        McpToolDescriptor descriptor,
        string? nameOverride = null
    )
    {
        _connection = connection;
        _remoteName = descriptor.Name;

        var sanitizedServer = SanitizeName(serverName);
        var sanitizedTool = SanitizeName(descriptor.Name);
        // nameOverride 用于桥接层全局去重后的唯一名（两个服务器同名工具时分配 _2/_3 后缀）。
        Name = nameOverride ?? $"mcp_{sanitizedServer}_{sanitizedTool}";

        Label = $"{serverName}/{descriptor.Name}";
        Description = !string.IsNullOrWhiteSpace(descriptor.Description)
            ? descriptor.Description + $"（由外部 MCP 服务器 {serverName} 提供）"
            : $"外部 MCP 工具：{serverName}/{descriptor.Name}（由外部 MCP 服务器 {serverName} 提供）";

        Parameters = descriptor.InputSchema;
    }

    public override async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        JsonElement args,
        CancellationToken ct,
        IProgress<AgentToolResult>? onUpdate = null
    )
    {
        try
        {
            var result = await _connection.CallToolAsync(_remoteName, args, ct);

            if (result.IsError)
            {
                return new AgentToolResult
                {
                    Content = [new TextContent(result.Text)],
                    IsError = true,
                };
            }

            return new AgentToolResult { Content = [new TextContent(result.Text)] };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult
            {
                Content = [new TextContent($"外部 MCP 工具调用失败（{Name}）：{ex.Message}")],
                IsError = true,
            };
        }
    }

    /// <summary>清洗名称：只保留 [a-zA-Z0-9_-]，其余替换为 _。</summary>
    private static string SanitizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "unknown";
        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            chars[i] =
                (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '_'
                || c == '-'
                    ? c
                    : '_';
        }
        return new string(chars);
    }
}
