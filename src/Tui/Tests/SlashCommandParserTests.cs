using Core.Tools;
using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// SlashCommandParser 严格单元测试：覆盖全部斜杠命令的路由、别名、参数解析与模糊匹配。
///
/// 背景：TuiApp.HandleSlashCommand 及其处理器是 UI 绑定（Dialog.Show 在无 TerminalApp 时抛
/// InvalidOperationException，ToastService 无宿主时静默 no-op），无法在不启动终端的单测中直接
/// 断言。路由/解析决策因此收口到 SlashCommandParser（internal 纯静态类，经 InternalsVisibleTo
/// 访问，与 CloudLoginDialog internal 测试缝同模式），此处对每个命令的每条分支做严格断言。
/// </summary>
public sealed class SlashCommandParserTests
{
    // ── MapCommand：全部命令 + 别名 + 大小写 ──

    [Theory]
    [InlineData("/commands", "Commands")]
    [InlineData("/help", "Help")]
    [InlineData("/state", "State")]
    [InlineData("/history", "History")]
    [InlineData("/clear", "Clear")]
    [InlineData("/tools", "Tools")]
    [InlineData("/stats", "Stats")] // 别名 → /state
    [InlineData("/resume", "Resume")]
    [InlineData("/session", "Session")]
    [InlineData("/theme", "Theme")]
    [InlineData("/compact", "Compact")]
    [InlineData("/decompress", "Decompress")]
    [InlineData("/tree", "Tree")]
    [InlineData("/mode", "Mode")]
    [InlineData("/context", "Context")]
    [InlineData("/read", "Read")]
    [InlineData("/chapter", "Chapter")]
    [InlineData("/notes", "Notes")]
    [InlineData("/note", "Notes")] // 简写别名
    [InlineData("/cloud", "Cloud")]
    [InlineData("/login", "Login")]
    [InlineData("/logout", "Logout")]
    [InlineData("/variants", "Variants")]
    [InlineData("/var", "Variants")] // 简写别名
    [InlineData(":cloud", "CloudColon")]
    [InlineData("/wechat", "WeChat")]
    [InlineData("/wx", "WeChat")] // 简写别名
    public void MapCommand_KnownCommands_MapsToKind(string input, string expectedKindName)
    {
        Assert.Equal(expectedKindName, SlashCommandParser.MapCommand(input).ToString());
    }

    [Theory]
    [InlineData("/CLEAR")]
    [InlineData("/State")]
    [InlineData("/TOOLS")]
    [InlineData("/Compact")]
    [InlineData("/LOGIN")]
    [InlineData(":CLOUD")]
    public void MapCommand_IsCaseInsensitive(string input)
    {
        Assert.NotEqual(
            SlashCommandParser.SlashCommandKind.Unknown,
            SlashCommandParser.MapCommand(input)
        );
    }

    [Theory]
    [InlineData("/bogus")]
    [InlineData("/notacommand")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("state")] // 缺斜杠前缀不识别
    public void MapCommand_UnknownInput_ReturnsUnknown(string input)
    {
        Assert.Equal(
            SlashCommandParser.SlashCommandKind.Unknown,
            SlashCommandParser.MapCommand(input)
        );
    }

    // ── ParseColonCloud：:cloud 子命令（修复前的死代码路径） ──

    [Theory]
    [InlineData("login", "CloudLogin")]
    [InlineData("status", "CloudStatus")]
    [InlineData("logout", "CloudLogout")]
    [InlineData("LOGIN", "CloudLogin")]
    [InlineData("sync", "CloudSync")]
    [InlineData("SYNC", "CloudSync")]
    [InlineData("pull", "CloudPull")]
    [InlineData("PULL", "CloudPull")]
    public void ParseColonCloud_KnownSubcommand_MapsToKind(string sub, string expectedKindName)
    {
        Assert.Equal(
            expectedKindName,
            SlashCommandParser.ParseColonCloud([":cloud", sub]).ToString()
        );
    }

    [Theory]
    [InlineData(new[] { "sync" }, "CloudSync")]
    [InlineData(new[] { "sync", "all" }, "CloudSyncAll")]
    [InlineData(new[] { "sync", "ALL" }, "CloudSyncAll")]
    public void ParseColonCloud_SyncVariants_MapToKind(string[] parts, string expectedKindName)
    {
        Assert.Equal(
            expectedKindName,
            SlashCommandParser.ParseColonCloud([":cloud", .. parts]).ToString()
        );
    }

    [Theory]
    [InlineData(":cloud")]
    [InlineData(":cloud", "bogus")]
    [InlineData(":cloud", "")]
    public void ParseColonCloud_UnknownSubcommand_ReturnsUnknown(params string[] parts)
    {
        Assert.Equal(
            SlashCommandParser.SlashCommandKind.Unknown,
            SlashCommandParser.ParseColonCloud(parts)
        );
    }

    // ── ParseCompact：/compact 参数 ──

    [Fact]
    public void ParseCompact_NoArgs_PlainWithDefaultKeepRecent()
    {
        var result = SlashCommandParser.ParseCompact(["/compact"], defaultKeepRecent: 20);

        Assert.False(result.Smart);
        Assert.Equal(20, result.KeepRecent);
    }

    [Fact]
    public void ParseCompact_WithCount_PlainWithParsedCount()
    {
        var result = SlashCommandParser.ParseCompact(["/compact", "5"], defaultKeepRecent: 20);

        Assert.False(result.Smart);
        Assert.Equal(5, result.KeepRecent);
    }

    [Fact]
    public void ParseCompact_SmartNoCount_SmartWithDefaultKeepRecent()
    {
        var result = SlashCommandParser.ParseCompact(["/compact", "smart"], defaultKeepRecent: 20);

        Assert.True(result.Smart);
        Assert.Equal(20, result.KeepRecent);
    }

    [Fact]
    public void ParseCompact_SmartWithCount_SmartWithParsedCount()
    {
        var result = SlashCommandParser.ParseCompact(
            ["/compact", "smart", "8"],
            defaultKeepRecent: 20
        );

        Assert.True(result.Smart);
        Assert.Equal(8, result.KeepRecent);
    }

    [Fact]
    public void ParseCompact_SmartKeywordCaseInsensitive()
    {
        var result = SlashCommandParser.ParseCompact(
            ["/compact", "SMART", "3"],
            defaultKeepRecent: 20
        );

        Assert.True(result.Smart);
        Assert.Equal(3, result.KeepRecent);
    }

    [Fact]
    public void ParseCompact_NonNumericCount_FallsBackToDefault()
    {
        var plain = SlashCommandParser.ParseCompact(["/compact", "abc"], defaultKeepRecent: 20);
        var smart = SlashCommandParser.ParseCompact(
            ["/compact", "smart", "abc"],
            defaultKeepRecent: 20
        );

        Assert.False(plain.Smart);
        Assert.Equal(20, plain.KeepRecent);
        Assert.True(smart.Smart);
        Assert.Equal(20, smart.KeepRecent);
    }

    [Fact]
    public void ParseCompact_ZeroCount_ParsedAsZero()
    {
        // int.TryParse("0") 成功 → 0 原样传入（与重构前语义一致，非法值由压缩器防御）
        var result = SlashCommandParser.ParseCompact(["/compact", "0"], defaultKeepRecent: 20);

        Assert.Equal(0, result.KeepRecent);
    }

    [Fact]
    public void ParseCompact_NegativeCount_ParsedAsNegative()
    {
        // 重构前语义：负数同样透传（压缩路径的 cutIndex 防御处理）
        var result = SlashCommandParser.ParseCompact(["/compact", "-3"], defaultKeepRecent: 20);

        Assert.Equal(-3, result.KeepRecent);
    }

    // ── ParseTheme：/theme 参数 ──

    [Fact]
    public void ParseTheme_NoArgs_ReturnsPicker()
    {
        var result = SlashCommandParser.ParseTheme(["/theme"]);

        Assert.Equal(SlashCommandParser.ThemeAction.Picker, result.Action);
        Assert.Null(result.ThemeId);
    }

    [Fact]
    public void ParseTheme_Toggle_ReturnsToggle()
    {
        var result = SlashCommandParser.ParseTheme(["/theme", "toggle"]);

        Assert.Equal(SlashCommandParser.ThemeAction.Toggle, result.Action);
    }

    [Fact]
    public void ParseTheme_ToggleCaseInsensitive()
    {
        var result = SlashCommandParser.ParseTheme(["/theme", "TOGGLE"]);

        Assert.Equal(SlashCommandParser.ThemeAction.Toggle, result.Action);
    }

    [Fact]
    public void ParseTheme_DarkAlias_ResolvesToDesert()
    {
        var result = SlashCommandParser.ParseTheme(["/theme", "dark"]);

        Assert.Equal(SlashCommandParser.ThemeAction.Set, result.Action);
        Assert.Equal(ThemeRegistry.Desert.Id, result.ThemeId);
    }

    [Fact]
    public void ParseTheme_LightAlias_ResolvesToDesertLight()
    {
        var result = SlashCommandParser.ParseTheme(["/theme", "light"]);

        Assert.Equal(SlashCommandParser.ThemeAction.Set, result.Action);
        Assert.Equal(ThemeRegistry.DesertLight.Id, result.ThemeId);
    }

    [Fact]
    public void ParseTheme_RegisteredThemeId_ResolvesToId()
    {
        var result = SlashCommandParser.ParseTheme(["/theme", ThemeRegistry.Desert.Id]);

        Assert.Equal(SlashCommandParser.ThemeAction.Set, result.Action);
        Assert.Equal(ThemeRegistry.Desert.Id, result.ThemeId);
    }

    [Fact]
    public void ParseTheme_UnknownTheme_ReturnsUnknownWithOriginalArg()
    {
        var result = SlashCommandParser.ParseTheme(["/theme", "neon"]);

        Assert.Equal(SlashCommandParser.ThemeAction.Unknown, result.Action);
        Assert.Null(result.ThemeId);
        Assert.Equal("neon", result.OriginalArg); // 提示消息用原始输入（未 lower）
    }

    [Fact]
    public void ParseTheme_UnknownKeepsOriginalCaseForMessage()
    {
        var result = SlashCommandParser.ParseTheme(["/theme", "NeOn"]);

        Assert.Equal(SlashCommandParser.ThemeAction.Unknown, result.Action);
        Assert.Equal("NeOn", result.OriginalArg);
    }

    // ── ParseLogin：/login 参数 ──

    [Fact]
    public void ParseLogin_NoArgs_Defaults()
    {
        var result = SlashCommandParser.ParseLogin(["/login"]);

        Assert.False(result.ForceDevice);
        Assert.Null(result.BaseUrlOverride);
        Assert.Null(result.UnknownArg);
    }

    [Fact]
    public void ParseLogin_DeviceFlag_ForcesDeviceFlow()
    {
        var result = SlashCommandParser.ParseLogin(["/login", "--device"]);

        Assert.True(result.ForceDevice);
        Assert.Null(result.BaseUrlOverride);
        Assert.Null(result.UnknownArg);
    }

    [Fact]
    public void ParseLogin_DeviceFlagCaseInsensitive()
    {
        var result = SlashCommandParser.ParseLogin(["/login", "--DEVICE"]);

        Assert.True(result.ForceDevice);
    }

    [Fact]
    public void ParseLogin_UrlOverride_ResolvesUrl()
    {
        var result = SlashCommandParser.ParseLogin(["/login", "http://cloud.example:5080"]);

        Assert.False(result.ForceDevice);
        Assert.Equal("http://cloud.example:5080", result.BaseUrlOverride);
        Assert.Null(result.UnknownArg);
    }

    [Fact]
    public void ParseLogin_DeviceAndUrl_BothApplied()
    {
        var result = SlashCommandParser.ParseLogin(["/login", "--device", "http://x"]);

        Assert.True(result.ForceDevice);
        Assert.Equal("http://x", result.BaseUrlOverride);
    }

    [Fact]
    public void ParseLogin_UnknownFlag_ReturnsUnknownArgAndStops()
    {
        var result = SlashCommandParser.ParseLogin(["/login", "--bogus"]);

        Assert.Equal("--bogus", result.UnknownArg);
        Assert.False(result.ForceDevice);
        Assert.Null(result.BaseUrlOverride);
    }

    [Fact]
    public void ParseLogin_MultipleUrls_LastWins()
    {
        var result = SlashCommandParser.ParseLogin(["/login", "http://a", "http://b"]);

        Assert.Equal("http://b", result.BaseUrlOverride);
    }

    [Fact]
    public void ParseLogin_UnknownFlagAfterDevice_ReturnsUnknownArg()
    {
        var result = SlashCommandParser.ParseLogin(["/login", "--device", "--nope"]);

        Assert.Equal("--nope", result.UnknownArg);
        Assert.True(result.ForceDevice); // 已解析的部分保留，但调用方以 UnknownArg 为准提示
    }

    [Fact]
    public void ParseLogin_DoubleDashNonDevice_TreatedAsUnknownFlag()
    {
        var result = SlashCommandParser.ParseLogin(["/login", "--notdevice"]);

        Assert.Equal("--notdevice", result.UnknownArg);
    }

    // ── ParseResumeNovelId / ParseDecompressBlockId ──

    [Fact]
    public void ParseResumeNovelId_NoArgs_ReturnsNull()
    {
        Assert.Null(SlashCommandParser.ParseResumeNovelId(["/resume"]));
    }

    [Fact]
    public void ParseResumeNovelId_WithNovelId_ReturnsId()
    {
        Assert.Equal("novel-1", SlashCommandParser.ParseResumeNovelId(["/resume", "novel-1"]));
    }

    [Fact]
    public void ParseDecompressBlockId_NoArgs_ReturnsEmpty()
    {
        Assert.Equal("", SlashCommandParser.ParseDecompressBlockId(["/decompress"]));
    }

    [Fact]
    public void ParseDecompressBlockId_WithBlockId_ReturnsTrimmed()
    {
        Assert.Equal("b3", SlashCommandParser.ParseDecompressBlockId(["/decompress", "  b3  "]));
    }

    // ── ResolveToolTarget：/tools 目标解析 ──

    private static ToolRegistry CreateRegistry()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeAgentTool("read_file", "读取文件"), "Test Plugin");
        registry.Register(new FakeAgentTool("write_file", "写入文件"), "Test Plugin");
        registry.Register(new FakeAgentTool("search", "搜索"), "Web Search");
        return registry;
    }

    [Fact]
    public void ResolveToolTarget_ColonExactMatch_ReturnsPrompt()
    {
        var target = SlashCommandParser.ResolveToolTarget(
            CreateRegistry(),
            "Test Plugin:read_file"
        );

        Assert.Equal(SlashCommandParser.ToolTargetKind.Prompt, target.Kind);
        Assert.Equal("test-plugin", target.PluginId); // 规范化 kebab-case
        Assert.Equal("read_file", target.ToolName);
    }

    [Fact]
    public void ResolveToolTarget_ColonCaseInsensitive_ReturnsPrompt()
    {
        var target = SlashCommandParser.ResolveToolTarget(
            CreateRegistry(),
            "test plugin:READ_FILE"
        );

        Assert.Equal(SlashCommandParser.ToolTargetKind.Prompt, target.Kind);
        Assert.Equal("test-plugin", target.PluginId);
        Assert.Equal("read_file", target.ToolName); // 匹配到真实工具名
    }

    [Fact]
    public void ResolveToolTarget_ColonFuzzyToolName_ReturnsPrompt()
    {
        var target = SlashCommandParser.ResolveToolTarget(CreateRegistry(), "test-plugin:read");

        Assert.Equal(SlashCommandParser.ToolTargetKind.Prompt, target.Kind);
        Assert.Equal("test-plugin", target.PluginId);
        Assert.Equal("read_file", target.ToolName);
    }

    [Fact]
    public void ResolveToolTarget_ColonValidPluginMissingTool_ReturnsUnknownWithParts()
    {
        var target = SlashCommandParser.ResolveToolTarget(CreateRegistry(), "test-plugin:missing");

        Assert.Equal(SlashCommandParser.ToolTargetKind.Unknown, target.Kind);
        Assert.Equal("test-plugin", target.PluginId); // 提示"未找到工具: test-plugin:missing"
        Assert.Equal("missing", target.ToolName);
    }

    [Fact]
    public void ResolveToolTarget_ColonUnknownPlugin_FallsThroughToUnknown()
    {
        var target = SlashCommandParser.ResolveToolTarget(CreateRegistry(), "nosuch:tool");

        Assert.Equal(SlashCommandParser.ToolTargetKind.Unknown, target.Kind);
        Assert.Null(target.PluginId); // 提示"未知插件"
        Assert.Null(target.ToolName);
        Assert.Equal("nosuch:tool", target.FullArg);
    }

    [Fact]
    public void ResolveToolTarget_PluginId_ReturnsPluginDialog()
    {
        var target = SlashCommandParser.ResolveToolTarget(CreateRegistry(), "test-plugin");

        Assert.Equal(SlashCommandParser.ToolTargetKind.PluginDialog, target.Kind);
        Assert.Equal("test-plugin", target.PluginId);
    }

    [Fact]
    public void ResolveToolTarget_PluginName_ReturnsPluginDialog()
    {
        var target = SlashCommandParser.ResolveToolTarget(CreateRegistry(), "TEST PLUGIN");

        Assert.Equal(SlashCommandParser.ToolTargetKind.PluginDialog, target.Kind);
        Assert.Equal("test-plugin", target.PluginId);
    }

    [Fact]
    public void ResolveToolTarget_UnknownPlugin_ReturnsUnknown()
    {
        var target = SlashCommandParser.ResolveToolTarget(CreateRegistry(), "nosuchplugin");

        Assert.Equal(SlashCommandParser.ToolTargetKind.Unknown, target.Kind);
        Assert.Equal("nosuchplugin", target.FullArg);
    }

    // ── FindSimilarCommand / LevenshteinDistance ──

    private static readonly string[] CommandNames =
    [
        "/commands",
        "/help",
        "/state",
        "/history",
        "/clear",
        "/tools",
        "/resume",
        "/session",
        "/theme",
        "/compact",
        "/decompress",
        "/tree",
        "/mode",
        "/cloud",
        "/login",
        "/logout",
    ];

    [Fact]
    public void FindSimilarCommand_TypoDistance1_SuggestsCommand()
    {
        Assert.Equal("/state", SlashCommandParser.FindSimilarCommand("/stat", CommandNames));
        Assert.Equal("/clear", SlashCommandParser.FindSimilarCommand("/clea", CommandNames));
    }

    [Fact]
    public void FindSimilarCommand_TypoDistance2_SuggestsCommand()
    {
        // /stte 与 /state：替换 t→a + 插入 t = 2 处编辑
        Assert.Equal("/state", SlashCommandParser.FindSimilarCommand("/stte", CommandNames));
    }

    [Fact]
    public void FindSimilarCommand_FarAwayTypo_ReturnsNull()
    {
        Assert.Null(SlashCommandParser.FindSimilarCommand("/zzzzzz", CommandNames));
    }

    [Fact]
    public void FindSimilarCommand_EmptyCommand_ReturnsNull()
    {
        Assert.Null(SlashCommandParser.FindSimilarCommand("", CommandNames));
    }

    [Fact]
    public void LevenshteinDistance_IdenticalStrings_Zero()
    {
        Assert.Equal(0, SlashCommandParser.LevenshteinDistance("/state", "/state"));
    }

    [Fact]
    public void LevenshteinDistance_EmptyString_ReturnsOtherLength()
    {
        Assert.Equal(5, SlashCommandParser.LevenshteinDistance("", "abcde"));
        Assert.Equal(5, SlashCommandParser.LevenshteinDistance("abcde", ""));
        Assert.Equal(0, SlashCommandParser.LevenshteinDistance("", ""));
    }

    [Fact]
    public void LevenshteinDistance_Insertion_OneEdit()
    {
        Assert.Equal(1, SlashCommandParser.LevenshteinDistance("/stat", "/state"));
    }

    [Fact]
    public void LevenshteinDistance_Substitution_OneEdit()
    {
        Assert.Equal(1, SlashCommandParser.LevenshteinDistance("/state", "/statu"));
    }

    [Fact]
    public void LevenshteinDistance_ClassicKittenSitting_Three()
    {
        Assert.Equal(3, SlashCommandParser.LevenshteinDistance("kitten", "sitting"));
    }

    [Fact]
    public void LevenshteinDistance_Transposition_Two()
    {
        Assert.Equal(2, SlashCommandParser.LevenshteinDistance("ab", "ba"));
    }

    [Fact]
    public void LevenshteinDistance_CjkCharacter_One()
    {
        Assert.Equal(1, SlashCommandParser.LevenshteinDistance("你好", "您好"));
    }

    // ── ParseContext：无参=查看、两种模式、非法参数不改状态 ──

    [Theory]
    [InlineData("/context", "Show", null)]
    [InlineData("/context ", "Show", null)]
    [InlineData("/context compact", "Switch", "compact")]
    [InlineData("/context continuation", "Switch", "continuation")]
    [InlineData("/context CONTINUATION", "Switch", "continuation")] // 大小写不敏感
    [InlineData("/context compression", "Unknown", null)]
    public void ParseContext_MapsArgs(string input, string expectedAction, string? expectedMode)
    {
        var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        var args = SlashCommandParser.ParseContext(parts);

        Assert.Equal(expectedAction, args.Action.ToString());
        Assert.Equal(expectedMode, args.Mode);
    }

    // ── ParseWeChatSubcommand：/wechat 与 /wx 共用的子命令 ──

    [Theory]
    [InlineData("/wechat bind", "WeChatBind")]
    [InlineData("/wechat status", "WeChatStatus")]
    [InlineData("/wechat unbind", "WeChatUnbind")]
    [InlineData("/wx bind", "WeChatBind")]
    [InlineData("/wx unbind", "WeChatUnbind")]
    [InlineData("/wechat BIND", "WeChatBind")] // 子命令大小写不敏感
    public void ParseWeChatSubcommand_KnownSubcommand_MapsToKind(
        string input,
        string expectedKindName
    )
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(expectedKindName, SlashCommandParser.ParseWeChatSubcommand(parts).ToString());
    }

    [Fact]
    public void ParseWeChatSubcommand_NoSubcommand_DefaultsToStatus()
    {
        // 无参意图 = 看状态（最常见的用法）
        Assert.Equal(
            SlashCommandParser.SlashCommandKind.WeChatStatus,
            SlashCommandParser.ParseWeChatSubcommand(["/wechat"])
        );
        Assert.Equal(
            SlashCommandParser.SlashCommandKind.WeChatStatus,
            SlashCommandParser.ParseWeChatSubcommand(["/wx"])
        );
    }

    [Fact]
    public void ParseWeChatSubcommand_UnknownSubcommand_ReturnsUnknown()
    {
        Assert.Equal(
            SlashCommandParser.SlashCommandKind.Unknown,
            SlashCommandParser.ParseWeChatSubcommand(["/wechat", "bogus"])
        );
        Assert.Equal(
            SlashCommandParser.SlashCommandKind.Unknown,
            SlashCommandParser.ParseWeChatSubcommand(["/wx", "nosuch"])
        );
    }

    // ── ParseChapterRef：/read 与 /chapter 共用的章节参数（序号 | ID | 标题片段） ──

    [Theory]
    [InlineData("/read 12", "12")]
    [InlineData("/read ch_abc", "ch_abc")]
    [InlineData("/read 雨夜寄北", "雨夜寄北")]
    [InlineData("/chapter 3", "3")]
    [InlineData("/read", null)]
    [InlineData("/read   ", null)]
    public void ParseChapterRef_ReturnsQueryOrNull(string input, string? expected)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(expected, SlashCommandParser.ParseChapterRef(parts));
    }
}
