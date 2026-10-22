using Core.Data;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// MCP 服务器设置标签页。
/// </summary>
internal sealed class McpTab
{
    private readonly VStack _cards = new() { Spacing = 1 }; // 卡片容器
    private readonly List<McpCardState> _entries = []; // MCP 卡片状态列表

    /// <summary>
    /// 构建 MCP 设置标签页的 UI。
    /// </summary>
    /// <param name="mcpConfig">MCP 配置。</param>
    /// <returns>视觉元素。</returns>
    public Visual Build(McpConfig mcpConfig)
    {
        foreach (var server in mcpConfig.Servers)
        {
            AddCard(
                server.Name,
                server.Transport,
                server.Url ?? "",
                server.Command,
                string.Join(" ", server.Args),
                server.Enabled
            );
        }

        // 占满整行（配合 CreateScrollableTabContent 的 Stretch 链，卡片右缘统一）
        var content = new VStack(
            new Markup("[bold]MCP 服务器[/]"),
            _cards,
            new HStack(
                new Button("➕ 本地服务器")
                    .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
                    .Click(() => AddCard("", "stdio", "", "npx", "", true)),
                new Button("🌐 远程 MCP")
                    .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
                    .Click(() => AddCard("", "http", "", "", "", true))
            ).Spacing(1)
        ).Spacing(1);
        content.HorizontalAlignment = Align.Stretch;

        return PanelThemes.CreateScrollableTabContent(content);
    }

    /// <summary>
    /// 构建服务器条目列表。
    /// </summary>
    /// <returns>服务器条目列表。</returns>
    public List<McpServerEntry> BuildServerEntries() =>
        _entries
            .Select(s => new McpServerEntry
            {
                Name = s.Name.Value ?? "",
                Transport = s.Transport.Value ?? "stdio",
                Url = string.IsNullOrWhiteSpace(s.Url.Value) ? null : s.Url.Value,
                Command = s.Command.Value ?? "npx",
                Args = ParseShellArgs(s.Args.Value ?? ""),
                Enabled = s.Enabled.Value,
            })
            .Where(s => !string.IsNullOrEmpty(s.Name))
            .ToList();

    /// <summary>
    /// 添加 MCP 服务器卡片。
    /// </summary>
    /// <param name="name">服务器名称。</param>
    /// <param name="transport">传输类型（stdio/http）。</param>
    /// <param name="url">远程端点 URL（http 时）。</param>
    /// <param name="command">命令（stdio 时）。</param>
    /// <param name="args">参数（stdio 时）。</param>
    /// <param name="enabled">是否启用。</param>
    private void AddCard(
        string name,
        string transport,
        string url,
        string command,
        string args,
        bool enabled
    )
    {
        var state = new McpCardState
        {
            Name = new State<string?>(name),
            Transport = new State<string?>(transport),
            Url = new State<string?>(url),
            Command = new State<string?>(command),
            Args = new State<string?>(args),
            Enabled = new State<bool>(enabled),
        };
        _entries.Add(state);

        var grid = new Grid()
            .Columns(new ColumnDefinition { Width = GridLength.Star() })
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            )
            .RowGap(1)
            .Cell(
                new HStack(
                    new Switch("启用")
                        .Style(ThemeStyles.BuildSwitch(PanelThemes.Current))
                        .IsOn(state.Enabled),
                    new TextBox()
                        .Text(state.Name)
                        .Placeholder("名称")
                        .HorizontalAlignment(Align.Stretch)
                ).Spacing(1),
                0,
                0
            )
            .Cell(
                new HStack(
                    new Markup("[dim]传输[/]"),
                    new TextBox()
                        .Text(state.Transport)
                        .Placeholder("stdio / http")
                        .HorizontalAlignment(Align.Stretch)
                ).Spacing(1),
                1,
                0
            )
            .Cell(
                new HStack(
                    new Markup("[dim]URL[/]"),
                    new TextBox()
                        .Text(state.Url)
                        .Placeholder("https://example.com/mcp")
                        .HorizontalAlignment(Align.Stretch)
                ).Spacing(1),
                2,
                0
            )
            .Cell(
                new HStack(
                    new Markup("[dim]命令[/]"),
                    new TextBox()
                        .Text(state.Command)
                        .Placeholder("npx")
                        .HorizontalAlignment(Align.Stretch)
                ).Spacing(1),
                3,
                0
            )
            .Cell(
                new HStack(
                    new Markup("[dim]参数[/]"),
                    new TextBox()
                        .Text(state.Args)
                        .Placeholder("-y @example/mcp-server")
                        .HorizontalAlignment(Align.Stretch)
                ).Spacing(1),
                4,
                0
            )
            .HorizontalAlignment(Align.Stretch);

        _cards.Children.Add(new Border(grid) { Padding = new Thickness(1, 0, 1, 0) });
    }

    /// <summary>
    /// 解析 Shell 参数字符串。
    /// </summary>
    /// <param name="args">参数字符串。</param>
    /// <returns>参数列表。</returns>
    private static List<string> ParseShellArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return [];
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        foreach (var c in args)
        {
            switch (c)
            {
                case '"':
                    inQuote = !inQuote;
                    break;
                case ' ' when !inQuote:
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }

                    break;
                }
                default:
                    current.Append(c);
                    break;
            }
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }
}

/// <summary>
/// MCP 服务器卡片状态。
/// </summary>
internal sealed class McpCardState
{
    public State<string?> Name = new("", null); // 服务器名称
    public State<string?> Transport = new("stdio", null); // 传输类型（stdio/http）
    public State<string?> Url = new("", null); // 远程端点 URL
    public State<string?> Command = new("", null); // 启动命令
    public State<string?> Args = new("", null); // 命令参数
    public State<bool> Enabled = new(false); // 是否启用
}
