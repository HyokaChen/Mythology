using System.Text.RegularExpressions;
using Core.Data;
using Tui.Controls;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Rendering;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// TUI MCP 设置契约：鉴权字段可编辑、保存时不丢值，密钥字段默认遮蔽。
/// </summary>
public sealed class McpTabTests
{
    [Fact]
    public void BuildServerEntries_PreservesAllAuthFields()
    {
        var tab = new McpTab();
        tab.Build(
            new McpConfig
            {
                Servers =
                [
                    new McpServerEntry
                    {
                        Name = "remote",
                        Transport = "http",
                        Url = "https://example.com/mcp",
                        Auth = "header",
                        BearerToken = "bearer-secret-1234",
                        AuthHeader = "X-API-Key",
                        AuthValue = "header-secret-5678",
                    },
                ],
            }
        );

        var entry = Assert.Single(tab.BuildServerEntries());

        Assert.Equal("header", entry.Auth);
        Assert.Equal("bearer-secret-1234", entry.BearerToken);
        Assert.Equal("X-API-Key", entry.AuthHeader);
        Assert.Equal("header-secret-5678", entry.AuthValue);
    }

    [Fact]
    public void BearerAuth_RendersMaskedToken_WhenUnfocused()
    {
        var tab = new McpTab();
        var root = tab.Build(
            new McpConfig
            {
                Servers =
                [
                    new McpServerEntry
                    {
                        Name = "remote",
                        Transport = "http",
                        Url = "https://example.com/mcp",
                        Auth = "bearer",
                        BearerToken = "sk-1234567890abcd",
                    },
                ],
            }
        );

        var buffer = VisualSnapshotRenderer.Render(root, 100, 32);
        var rendered = StripMarkup(string.Join('\n', buffer.ToMarkupLines()));

        Assert.Contains("••••••••abcd", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-1234567890abcd", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderAuth_RendersMaskedValue_WhenUnfocused()
    {
        var tab = new McpTab();
        var root = tab.Build(
            new McpConfig
            {
                Servers =
                [
                    new McpServerEntry
                    {
                        Name = "remote",
                        Transport = "http",
                        Url = "https://example.com/mcp",
                        Auth = "header",
                        AuthHeader = "X-API-Key",
                        AuthValue = "header-secret-5678",
                    },
                ],
            }
        );

        var buffer = VisualSnapshotRenderer.Render(root, 100, 32);
        var rendered = StripMarkup(string.Join('\n', buffer.ToMarkupLines()));

        Assert.Contains("X-API-Key", rendered, StringComparison.Ordinal);
        Assert.Contains("••••••••5678", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret-5678", rendered, StringComparison.Ordinal);
    }

    private static string StripMarkup(string text) => Regex.Replace(text, @"\[[^\[\]]*\]", "");
}
