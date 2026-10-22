using Core.Tools;

namespace Tui.Services;

/// <summary>
/// 斜杠命令解析与路由决策（纯逻辑，零 UI 依赖）。
///
/// TuiApp.HandleSlashCommand 委托本类完成「命令识别 → 参数解析」的决策层，
/// 自身只负责把决策映射到对话框/异步处理器。单元测试经 InternalsVisibleTo
/// （Tui.Tests）直接验证全部命令的路由、别名、参数解析与模糊匹配——
/// 与 CloudLoginDialog internal 静态工厂测试缝同模式。
///
/// 设计约束：
/// - 所有方法 internal static 且不触碰 ToastService / Dialog / TerminalApp，
///   保证无终端环境下可严格断言决策结果；
/// - 语义与重构前 TuiApp.HandleSlashCommand / HandleToolsCommand /
///   HandleUnknownCommand 完全一致（含别名、大小写不敏感、参数回退）。
/// </summary>
internal static class SlashCommandParser
{
    // ── 斜杠命令常量（TuiApp.Commands.cs 迁移，单一事实来源） ──
    internal const string Commands = "/commands"; // 命令列表
    internal const string Help = "/help"; // 帮助
    internal const string State = "/state"; // 状态
    internal const string History = "/history"; // 历史
    internal const string Clear = "/clear"; // 清屏
    internal const string Tools = "/tools"; // 工具
    internal const string Stats = "/stats"; // /stats 别名 → /state
    internal const string Resume = "/resume"; // 恢复
    internal const string Session = "/session"; // 会话
    internal const string Theme = "/theme"; // 主题
    internal const string Compact = "/compact"; // 压缩
    internal const string Decompress = "/decompress"; // 恢复压缩内容
    internal const string Tree = "/tree"; // 分支树
    internal const string Mode = "/mode"; // 增强模式
    internal const string Context = "/context"; // 上下文管理模式（压缩/接续）
    internal const string Read = "/read"; // 读章节（只读阅读器）
    internal const string Chapter = "/chapter"; // 定位章节（大纲面板高亮）
    internal const string Search = "/search"; // 全书检索（跨章节关键字）
    internal const string Notes = "/notes"; // 笔记（随手记 CRUD）
    internal const string Note = "/note"; // 笔记简写
    internal const string Cloud = "/cloud"; // 云端登录（兼容别名）
    internal const string Login = "/login"; // 云端登录（主命令）
    internal const string Logout = "/logout"; // 云端登出
    internal const string Variants = "/variants"; // 变体（隐藏命令）
    internal const string Var = "/var"; // 变体简写（隐藏命令）
    internal const string CloudColon = ":cloud"; // 云端冒号形式（login/status/logout/sync/pull）
    internal const string WeChat = "/wechat"; // 微信绑定（主命令）
    internal const string Wx = "/wx"; // 微信绑定简写

    // ── /wechat 子命令常量 ──
    internal const string WeChatBind = "bind"; // 扫码绑定
    internal const string WeChatStatus = "status"; // 查看绑定状态
    internal const string WeChatUnbind = "unbind"; // 解绑

    // ── 主题子参数常量 ──
    internal const string ThemeToggle = "toggle"; // 循环切换主题
    internal const string ThemeDark = "dark"; // 兼容别名 → desert
    internal const string ThemeLight = "light"; // 兼容别名 → desert-light

    /// <summary>斜杠命令分类（路由决策结果）。</summary>
    internal enum SlashCommandKind
    {
        Unknown,
        Tools,
        Session,
        State,
        History,
        Clear,

        /// <summary>/stats 别名 → 与 State 同一处理器。</summary>
        Stats,
        Resume,
        Theme,
        Compact,
        Decompress,
        Tree,
        Mode,

        /// <summary>/context：上下文管理模式（压缩 / 接续）。</summary>
        Context,

        /// <summary>/read：只读章节阅读器。</summary>
        Read,

        /// <summary>/chapter：在大纲面板定位章节。</summary>
        Chapter,

        /// <summary>/search：全书检索（正文/细纲/标题）。</summary>
        Search,

        /// <summary>/notes 与 /note 别名：作者随手记。</summary>
        Notes,
        Commands,
        Help,

        /// <summary>/variants 与 /var 别名。</summary>
        Variants,
        Cloud,
        Login,
        Logout,

        /// <summary>:cloud 冒号形式（子命令由 <see cref="ParseColonCloud"/> 细分）。</summary>
        CloudColon,
        CloudLogin,
        CloudStatus,
        CloudLogout,
        CloudSync,
        CloudSyncAll,
        CloudPull,

        /// <summary>/wechat 与 /wx（子命令由 <see cref="ParseWeChatSubcommand"/> 细分）。</summary>
        WeChat,

        /// <summary>/wechat bind：扫码绑定。</summary>
        WeChatBind,

        /// <summary>/wechat status（无子命令时的默认）。</summary>
        WeChatStatus,

        /// <summary>/wechat unbind：解绑。</summary>
        WeChatUnbind,
    }

    /// <summary>压缩参数：smart 表示智能压缩（RGDecimator），KeepRecent 为保留消息数。</summary>
    internal sealed record CompactArgs(bool Smart, int KeepRecent);

    /// <summary>上下文管理模式命令动作。</summary>
    internal enum ContextAction
    {
        /// <summary>无参数 → 只展示当前模式与用法。</summary>
        Show,

        /// <summary>compact / continuation → 切换。</summary>
        Switch,

        /// <summary>非法参数（提示用法，不改状态）。</summary>
        Unknown,
    }

    /// <summary>/context 解析结果；Mode 仅 Switch 时有值。</summary>
    internal sealed record ContextArgs(ContextAction Action, string? Mode, string? OriginalArg);

    /// <summary>
    /// /read、/chapter 的参数（章节序号 | 章节 ID | 标题片段；无参 → null 表示给用法提示）。
    /// </summary>
    internal static string? ParseChapterRef(string[] parts) =>
        parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : null;

    /// <summary>
    /// /context [compact|continuation] 解析（大小写不敏感；无参 → 查看当前模式）。
    /// </summary>
    internal static ContextArgs ParseContext(string[] parts)
    {
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            return new ContextArgs(ContextAction.Show, null, null);

        var arg = parts[1].Trim();
        var normalized = arg.ToLowerInvariant();
        return normalized switch
        {
            "compact" => new ContextArgs(ContextAction.Switch, "compact", arg),
            "continuation" => new ContextArgs(ContextAction.Switch, "continuation", arg),
            _ => new ContextArgs(ContextAction.Unknown, null, arg),
        };
    }

    /// <summary>主题命令动作。</summary>
    internal enum ThemeAction
    {
        /// <summary>无参数 → 弹出主题选择器。</summary>
        Picker,

        /// <summary>toggle → 循环切换。</summary>
        Toggle,

        /// <summary>dark/light/有效主题 id → 设置主题。</summary>
        Set,

        /// <summary>未知主题名。</summary>
        Unknown,
    }

    /// <summary>主题命令解析结果。OriginalArg 保留用户原始输入（提示消息用，不 lower）。</summary>
    internal sealed record ThemeArgs(ThemeAction Action, string? ThemeId, string? OriginalArg);

    /// <summary>/login 参数解析结果。UnknownArg 非 null 表示存在非法 --参数（此时应提示用法并返回）。</summary>
    internal sealed record LoginArgs(bool ForceDevice, string? BaseUrlOverride, string? UnknownArg);

    /// <summary>/tools 目标解析结果。</summary>
    internal enum ToolTargetKind
    {
        /// <summary>"插件:工具"命中 → 嵌入工具调用提示。</summary>
        Prompt,

        /// <summary>插件 ID/名称命中 → 展开该插件的工具对话框。</summary>
        PluginDialog,

        /// <summary>未找到工具或插件。</summary>
        Unknown,
    }

    /// <summary>
    /// /tools 目标解析结果。PluginId 为规范化 kebab-case ID（Prompt/PluginDialog 时非空），
    /// ToolName 仅 Prompt 时非空；FullArg 为原始参数（错误提示用）。
    /// </summary>
    internal sealed record ToolTarget(
        ToolTargetKind Kind,
        string? PluginId,
        string? ToolName,
        string FullArg
    );

    /// <summary>
    /// 路由决策：把输入的第一个 token 映射到命令分类。
    /// 大小写不敏感；空串/未知 → Unknown（由调用方走模糊匹配提示）。
    /// </summary>
    internal static SlashCommandKind MapCommand(string firstToken)
    {
        return firstToken.ToLowerInvariant() switch
        {
            Tools => SlashCommandKind.Tools,
            Session => SlashCommandKind.Session,
            State => SlashCommandKind.State,
            History => SlashCommandKind.History,
            Clear => SlashCommandKind.Clear,
            Stats => SlashCommandKind.Stats,
            Resume => SlashCommandKind.Resume,
            Theme => SlashCommandKind.Theme,
            Compact => SlashCommandKind.Compact,
            Decompress => SlashCommandKind.Decompress,
            Tree => SlashCommandKind.Tree,
            Mode => SlashCommandKind.Mode,
            Context => SlashCommandKind.Context,
            Read => SlashCommandKind.Read,
            Chapter => SlashCommandKind.Chapter,
            Search => SlashCommandKind.Search,
            Notes or Note => SlashCommandKind.Notes,
            Commands => SlashCommandKind.Commands,
            Help => SlashCommandKind.Help,
            Variants or Var => SlashCommandKind.Variants,
            Cloud => SlashCommandKind.Cloud,
            Login => SlashCommandKind.Login,
            Logout => SlashCommandKind.Logout,
            CloudColon => SlashCommandKind.CloudColon,
            WeChat or Wx => SlashCommandKind.WeChat,
            _ => SlashCommandKind.Unknown,
        };
    }

    /// <summary>
    /// /wechat 子命令细分：bind / status / unbind。
    /// 无子命令 → Status（看状态是最常见的无参意图）；未知子命令 → Unknown（调用方提示用法）。
    /// </summary>
    internal static SlashCommandKind ParseWeChatSubcommand(string[] parts)
    {
        if (parts.Length < 2)
            return SlashCommandKind.WeChatStatus;
        return parts[1].ToLowerInvariant() switch
        {
            WeChatBind => SlashCommandKind.WeChatBind,
            WeChatStatus => SlashCommandKind.WeChatStatus,
            WeChatUnbind => SlashCommandKind.WeChatUnbind,
            _ => SlashCommandKind.Unknown,
        };
    }

    /// <summary>
    /// :cloud 冒号形式的子命令细分（login/status/logout）。
    /// 无子命令或子命令未知 → Unknown（调用方回退到未知命令提示）。
    /// </summary>
    internal static SlashCommandKind ParseColonCloud(string[] parts)
    {
        if (parts.Length < 2)
            return SlashCommandKind.Unknown;
        var subcommand = parts[1].ToLowerInvariant();
        if (subcommand == "sync")
        {
            // :cloud sync [all]（大小写不敏感）
            return parts.Length >= 3 && parts[2].Equals("all", StringComparison.OrdinalIgnoreCase)
                ? SlashCommandKind.CloudSyncAll
                : SlashCommandKind.CloudSync;
        }
        if (subcommand == "pull")
        {
            return SlashCommandKind.CloudPull;
        }
        return subcommand switch
        {
            "login" => SlashCommandKind.CloudLogin,
            "status" => SlashCommandKind.CloudStatus,
            "logout" => SlashCommandKind.CloudLogout,
            _ => SlashCommandKind.Unknown,
        };
    }

    /// <summary>
    /// /compact 参数解析：
    /// - "/compact" → 普通压缩，KeepRecent = defaultKeepRecent；
    /// - "/compact N" → 普通压缩，KeepRecent = N；
    /// - "/compact smart" → 智能压缩，KeepRecent = defaultKeepRecent；
    /// - "/compact smart N" → 智能压缩，KeepRecent = N；
    /// 非数字参数回退 defaultKeepRecent（语义与重构前一致）。
    /// </summary>
    internal static CompactArgs ParseCompact(string[] parts, int defaultKeepRecent)
    {
        if (parts.Length >= 2 && parts[1].Equals("smart", StringComparison.OrdinalIgnoreCase))
        {
            var keep =
                parts.Length >= 3 && int.TryParse(parts[2], out var k2) ? k2 : defaultKeepRecent;
            return new CompactArgs(true, keep);
        }

        var keepPlain =
            parts.Length >= 2 && int.TryParse(parts[1], out var k) ? k : defaultKeepRecent;
        return new CompactArgs(false, keepPlain);
    }

    /// <summary>
    /// /theme 参数解析：
    /// - 无参数 → Picker（弹出主题选择器）；
    /// - toggle → Toggle（循环切换）；
    /// - dark/light → Set（兼容别名 → desert / desert-light）；
    /// - 已注册主题 id → Set；
    /// - 其余 → Unknown（OriginalArg 保留原始输入用于提示）。
    /// </summary>
    internal static ThemeArgs ParseTheme(string[] parts)
    {
        if (parts.Length < 2)
            return new ThemeArgs(ThemeAction.Picker, null, null);

        var themeArg = parts[1].ToLowerInvariant();
        switch (themeArg)
        {
            case ThemeToggle:
                return new ThemeArgs(ThemeAction.Toggle, null, parts[1]);
            case ThemeDark:
                return new ThemeArgs(ThemeAction.Set, ThemeRegistry.Desert.Id, parts[1]);
            case ThemeLight:
                return new ThemeArgs(ThemeAction.Set, ThemeRegistry.DesertLight.Id, parts[1]);
        }

        if (ThemeRegistry.Get(themeArg) is { } target)
            return new ThemeArgs(ThemeAction.Set, target.Id, parts[1]);

        return new ThemeArgs(ThemeAction.Unknown, null, parts[1]);
    }

    /// <summary>
    /// /login 参数解析：--device 强制设备流；--未知参数 → UnknownArg（提示用法并中止）；
    /// 其余参数为云端地址覆盖（多个时最后一个生效，与重构前循环语义一致）。
    /// </summary>
    internal static LoginArgs ParseLogin(string[] parts)
    {
        string? baseUrlOverride = null;
        var forceDevice = false;
        for (var i = 1; i < parts.Length; i++)
        {
            var arg = parts[i];
            if (arg.Equals("--device", StringComparison.OrdinalIgnoreCase))
            {
                forceDevice = true;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                return new LoginArgs(forceDevice, baseUrlOverride, arg);
            }
            else
            {
                baseUrlOverride = arg;
            }
        }
        return new LoginArgs(forceDevice, baseUrlOverride, null);
    }

    /// <summary>/resume 参数解析：缺省返回 null（调用方提示用法）。</summary>
    internal static string? ParseResumeNovelId(string[] parts) =>
        parts.Length >= 2 ? parts[1] : null;

    /// <summary>/decompress 参数解析：缺省为空串（列出所有可恢复块）；块 ID 去除首尾空白。</summary>
    internal static string ParseDecompressBlockId(string[] parts) =>
        parts.Length >= 2 ? parts[1].Trim() : "";

    /// <summary>
    /// /tools 目标解析：完整复刻重构前 HandleToolsCommand 的匹配顺序——
    /// 1. "插件ID:工具名" 冒号形式：插件命中后精确/模糊匹配工具 → Prompt；
    ///    插件命中但工具未命中 → Unknown（提示"未找到工具"）；
    ///    插件未命中 → 落到 2/3 继续按插件匹配；
    /// 2. 规范化插件 ID（kebab-case）命中 → PluginDialog；
    /// 3. 插件原名（大小写不敏感）命中 → PluginDialog；
    /// 4. 其余 → Unknown（提示"未知插件"）。
    /// </summary>
    internal static ToolTarget ResolveToolTarget(ToolRegistry registry, string fullArg)
    {
        if (fullArg.Contains(':'))
        {
            var colonIdx = fullArg.IndexOf(':');
            var pluginId = fullArg[..colonIdx];
            var toolName = fullArg[(colonIdx + 1)..];

            var pluginName =
                registry.GetPluginNameById(pluginId)
                ?? registry
                    .GetPluginNames()
                    .FirstOrDefault(p => p.Equals(pluginId, StringComparison.OrdinalIgnoreCase));

            if (pluginName is not null)
            {
                var tools = registry.GetToolsByPlugin(pluginName);
                var tool =
                    tools.FirstOrDefault(t =>
                        t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)
                    )
                    ?? tools.FirstOrDefault(t =>
                        t.Name.Contains(toolName, StringComparison.OrdinalIgnoreCase)
                    );
                if (tool is not null)
                    return new ToolTarget(
                        ToolTargetKind.Prompt,
                        ToolRegistry.NormalizePluginId(pluginName),
                        tool.Name,
                        fullArg
                    );
                return new ToolTarget(ToolTargetKind.Unknown, pluginId, toolName, fullArg);
            }
        }

        // 尝试匹配插件 ID（kebab-case）
        var normalizedId = ToolRegistry.NormalizePluginId(fullArg);
        if (registry.GetPluginNameById(normalizedId) is not null)
            return new ToolTarget(ToolTargetKind.PluginDialog, normalizedId, null, fullArg);

        // 尝试直接匹配插件名
        var exactPlugin = registry
            .GetPluginNames()
            .FirstOrDefault(p => p.Equals(fullArg, StringComparison.OrdinalIgnoreCase));
        if (exactPlugin is not null)
            return new ToolTarget(
                ToolTargetKind.PluginDialog,
                ToolRegistry.NormalizePluginId(exactPlugin),
                null,
                fullArg
            );

        return new ToolTarget(ToolTargetKind.Unknown, null, null, fullArg);
    }

    /// <summary>
    /// 未知命令模糊匹配：在命令名列表中找 Levenshtein 距离 ≤ 2 的第一个建议。
    /// 与重构前 HandleUnknownCommand 语义一致（长度 ≤ 1 的命令不参与建议）。
    /// </summary>
    internal static string? FindSimilarCommand(string cmd, IReadOnlyCollection<string> commandNames)
    {
        return commandNames.FirstOrDefault(c => c.Length > 1 && LevenshteinDistance(cmd, c) <= 2);
    }

    /// <summary>计算两个字符串的 Levenshtein 编辑距离（用于斜杠命令模糊匹配）。</summary>
    internal static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
