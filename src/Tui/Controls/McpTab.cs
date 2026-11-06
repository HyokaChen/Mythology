using Core.Data;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// MCP 服务器设置标签页。
/// </summary>
internal sealed class McpTab
{
    private static readonly string[] AuthModes = ["none", "bearer", "header"];
    private static readonly string[] AuthLabels = ["无鉴权", "Bearer 令牌", "自定义头"];

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
                server.Enabled,
                server.Auth,
                server.BearerToken,
                server.AuthHeader,
                server.AuthValue
            );
        }

        // 占满整行（配合 CreateScrollableTabContent 的 Stretch 链，卡片右缘统一）
        var content = new VStack(
            new Markup("[bold]MCP 服务器[/]"),
            _cards,
            new HStack(
                new Button("➕ 本地服务器")
                    .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
                    .Click(() => AddCard("", "stdio", "", "npx", "", true, "none", null, null, null)),
                new Button("🌐 远程 MCP")
                    .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
                    .Click(() => AddCard("", "http", "", "", "", true, "none", null, null, null))
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
                Auth = AuthModes[Math.Clamp(s.AuthIndex.Value, 0, AuthModes.Length - 1)],
                BearerToken = NullIfWhiteSpace(s.BearerToken.Value),
                AuthHeader = NullIfWhiteSpace(s.AuthHeader.Value),
                AuthValue = NullIfWhiteSpace(s.AuthValue.Value),
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
    /// <param name="auth">鉴权方式（none/bearer/header）。</param>
    /// <param name="bearerToken">Bearer 令牌。</param>
    /// <param name="authHeader">自定义鉴权头名。</param>
    /// <param name="authValue">自定义鉴权头的值。</param>
    private void AddCard(
        string name,
        string transport,
        string url,
        string command,
        string args,
        bool enabled,
        string? auth,
        string? bearerToken,
        string? authHeader,
        string? authValue
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
            AuthIndex = new State<int>(AuthIndexFor(auth)),
            BearerToken = new State<string?>(bearerToken ?? ""),
            AuthHeader = new State<string?>(authHeader ?? ""),
            AuthValue = new State<string?>(authValue ?? ""),
        };
        _entries.Add(state);

        var transportInput = new TextBox()
            .Text(state.Transport)
            .Placeholder("stdio / http")
            .HorizontalAlignment(Align.Stretch);

        var authSelect = new MythSelect(AuthLabels, state.AuthIndex.Value)
            .SelectedIndex(state.AuthIndex)
            .Style(SelectStyle.NoBorder);
        authSelect.HorizontalAlignment = Align.Stretch;

        var bearerRow = new HStack(
            new Markup("[dim]令牌[/]"),
            new SecretTextBox()
                .Text(state.BearerToken)
                .Placeholder("Bearer 令牌")
                .HorizontalAlignment(Align.Stretch)
        ).Spacing(1);
        bearerRow.IsVisible = state.AuthIndex.Value == 1;

        var headerNameRow = new HStack(
            new Markup("[dim]头名[/]"),
            new TextBox()
                .Text(state.AuthHeader)
                .Placeholder("X-API-Key")
                .HorizontalAlignment(Align.Stretch)
        ).Spacing(1);
        headerNameRow.IsVisible = state.AuthIndex.Value == 2;

        var headerValueRow = new HStack(
            new Markup("[dim]头值[/]"),
            new SecretTextBox()
                .Text(state.AuthValue)
                .Placeholder("值")
                .HorizontalAlignment(Align.Stretch)
        ).Spacing(1);
        headerValueRow.IsVisible = state.AuthIndex.Value == 2;

        authSelect.SelectionChanged(
            (_, e) =>
            {
                bearerRow.IsVisible = e.NewIndex == 1;
                headerNameRow.IsVisible = e.NewIndex == 2;
                headerValueRow.IsVisible = e.NewIndex == 2;
            }
        );

        // 鉴权仅对 HTTP 传输有意义；Transport 仍是自由文本，跟随输入动态显示。
        var authSection = new VStack(
            new HStack(new Markup("[dim]鉴权[/]"), authSelect).Spacing(1),
            bearerRow,
            headerNameRow,
            headerValueRow
        ).Spacing(1);
        authSection.IsVisible = !IsStdio(state.Transport.Value);
        transportInput.Update(_ => authSection.IsVisible = !IsStdio(transportInput.Text));

        var grid = new Grid()
            .Columns(new ColumnDefinition { Width = GridLength.Star() })
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
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
                new HStack(new Markup("[dim]传输[/]"), transportInput).Spacing(1),
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
            .Cell(authSection, 3, 0)
            .Cell(
                new HStack(
                    new Markup("[dim]命令[/]"),
                    new TextBox()
                        .Text(state.Command)
                        .Placeholder("npx")
                        .HorizontalAlignment(Align.Stretch)
                ).Spacing(1),
                4,
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
                5,
                0
            )
            .HorizontalAlignment(Align.Stretch);

        _cards.Children.Add(new Border(grid) { Padding = new Thickness(1, 0, 1, 0) });
    }

    private static int AuthIndexFor(string? auth) =>
        auth?.Trim().ToLowerInvariant() switch
        {
            "bearer" => 1,
            "header" => 2,
            _ => 0,
        };

    private static bool IsStdio(string? transport) =>
        string.Equals(transport?.Trim(), "stdio", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
    public State<int> AuthIndex = new(0); // 鉴权方式：0=none / 1=bearer / 2=header
    public State<string?> BearerToken = new("", null); // Bearer 令牌
    public State<string?> AuthHeader = new("", null); // 自定义鉴权头名
    public State<string?> AuthValue = new("", null); // 自定义鉴权头的值
}
