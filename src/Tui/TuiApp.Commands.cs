using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Channels.WeChat;
using ClientPlatform;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Novels;
using Core.Plugins;
using Core.Tools;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using Tui.Controls;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;

namespace Tui;

public sealed partial class TuiApp
{
    // 斜杠命令常量与解析/路由决策统一收口在 SlashCommandParser（internal，可单测）；
    // 本 partial 只负责把决策映射到对话框/异步处理器。

    /// <summary>
    /// 获取斜杠命令列表。
    /// </summary>
    /// <returns>斜杠命令列表。</returns>
    private ReadOnlyCollection<string> GetSlashCommands()
    {
        var cmds = new List<string>
        {
            SlashCommandParser.Commands,
            SlashCommandParser.Help,
            SlashCommandParser.State,
            SlashCommandParser.History,
            SlashCommandParser.Clear,
            $"{SlashCommandParser.Theme} {SlashCommandParser.ThemeToggle}",
            SlashCommandParser.Tools,
            // /stats 已合并入 /state，不再单独列出
            SlashCommandParser.Resume,
            SlashCommandParser.Session,
            SlashCommandParser.Compact,
            SlashCommandParser.Decompress,
            SlashCommandParser.Tree,
            SlashCommandParser.Mode,
            SlashCommandParser.Context,
            $"{SlashCommandParser.Read} <序号|ID|标题>",
            $"{SlashCommandParser.Chapter} <序号|ID|标题>",
            $"{SlashCommandParser.Search} <关键字>",
            SlashCommandParser.Notes,
            SlashCommandParser.Login,
            SlashCommandParser.Logout,
            SlashCommandParser.Cloud,
            SlashCommandParser.WeChat,
            $"{SlashCommandParser.CloudColon} sync",
            $"{SlashCommandParser.CloudColon} sync all",
            $"{SlashCommandParser.CloudColon} pull",
        };
        cmds.AddRange(_registry.GetPluginIds().Select(id => $"{SlashCommandParser.Tools} {id}"));
        // 动态追加 /theme <主题 id>（主题列表来自注册表）
        cmds.AddRange(ThemeRegistry.All.Select(t => $"{SlashCommandParser.Theme} {t.Id}"));
        // 动态追加 /tools <插件名>
        return cmds.AsReadOnly();
    }

    /// <summary>
    /// 注册全局快捷键命令：Ctrl+P 设置、Ctrl+H 帮助、Ctrl+O 项目、Ctrl+E 会话、
    /// Ctrl+L 清屏、Ctrl+S 中断、Ctrl+T 主题，以及状态/历史/分支树面板命令。
    /// </summary>
    private void RegisterCommands()
    {
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.Settings",
                Name = "settings",
                LabelMarkup = "[primary]🔧 系统设置[/]",
                Gesture = new KeyGesture(TerminalChar.CtrlP, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandPalette,
                Execute = _ =>
                    new SettingsDialog(
                        _settingsService,
                        _registry,
                        ReloadProvider,
                        ReloadEmbedding,
                        ReloadSystemOne,
                        SyncPluginState,
                        RemovePlugin,
                        _contextModeSwitcher
                    ).Show(),
            }
        );
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.Help",
                Name = "help",
                LabelMarkup = "[primary]❓ 帮助[/]",
                Gesture = new KeyGesture(TerminalChar.CtrlH, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandPalette,
                Execute = _ => AboutDialog.Show(),
            }
        );
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.NovelProject",
                Name = "novel-project",
                LabelMarkup = "[primary]📂 小说项目[/]",
                Gesture = new KeyGesture(TerminalChar.CtrlO, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandPalette,
                Execute = _ => ShowNovelProjectDialog(),
            }
        );
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.SessionPicker",
                Name = "session-picker",
                LabelMarkup = "[primary]🔄 会话切换[/]",
                Gesture = new KeyGesture(TerminalChar.CtrlE, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandPalette,
                Execute = _ => ShowSessionPickerDialog(),
            }
        );
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.Clear",
                Name = "clear",
                LabelMarkup = "[primary]🧹 清屏[/]",
                Gesture = new KeyGesture(TerminalChar.CtrlL, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandPalette,
                Execute = _ => ClearConversation(),
            }
        );
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.Abort",
                Name = "abort",
                LabelMarkup = "[error]⛔ 中断 Agent[/]",
                Gesture = new KeyGesture(TerminalChar.CtrlS, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandPalette,
                Execute = _ => AbortAgent(),
            }
        );
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.ToggleTheme",
                Name = "theme.toggle",
                LabelMarkup = "[primary]🌓 切换主题[/]",
                Gesture = new KeyGesture(TerminalChar.CtrlT, TerminalModifiers.Ctrl),
                Presentation = CommandPresentation.CommandPalette,
                Execute = _ => ToggleTheme(),
            }
        );
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.State",
                Name = "state",
                LabelMarkup = "[primary]🤖 Agent 状态[/]",
                Presentation = CommandPresentation.CommandPalette,
                Execute = InvokeShowState,
            }
        );
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.History",
                Name = "history",
                LabelMarkup = "[primary]📜 对话历史[/]",
                Presentation = CommandPresentation.CommandPalette,
                Execute = _ => ShowHistoryDialog(),
            }
        );
        _rootVisual.AddCommand(
            new Command
            {
                Id = "App.Tree",
                Name = "tree",
                LabelMarkup = "[primary]🌳 对话分支树[/]",
                Presentation = CommandPresentation.CommandPalette,
                Execute = _ => ShowTreeSelectorDialog(),
            }
        );
    }

    // ── /tools 斜杠命令处理 ──

    /// <summary>
    /// 处理 /tools 斜杠命令：无参数显示所有插件对话框，带参数按"插件ID:工具名"或插件名匹配。
    /// 匹配决策（含精确/模糊匹配、冒号形式、规范化 ID）委托 SlashCommandParser.ResolveToolTarget
    /// （纯逻辑，可单测），此处只做 UI 映射。
    /// </summary>
    private void HandleToolsCommand(string[] parts)
    {
        // /tools — 无参数回车：Dialog 展示所有插件 + 展开工具
        if (parts.Length < 2)
        {
            var dlg = new ToolsDialog(_registry, OnToolSelectedFromDialog);
            dlg.ShowAll();
            return;
        }

        var fullArg = parts.Length > 2 ? string.Join(' ', parts[1..]) : parts[1];
        var target = SlashCommandParser.ResolveToolTarget(_registry, fullArg);

        switch (target.Kind)
        {
            case SlashCommandParser.ToolTargetKind.Prompt:
                EmbedToolPrompt($"{target.PluginId}:{target.ToolName}");
                return;
            case SlashCommandParser.ToolTargetKind.PluginDialog:
                var dlg = new ToolsDialog(_registry, OnToolSelectedFromDialog);
                dlg.ShowPlugin(target.PluginId!);
                return;
            default:
                // 冒号形式且插件命中但工具未命中 → 提示未找到工具；其余 → 未知插件
                if (target.PluginId is not null && target.ToolName is not null)
                    ToastService.Error($"未找到工具: {target.PluginId}:{target.ToolName}");
                else
                    ToastService.Error($"未知插件: {target.FullArg}");
                return;
        }
    }

    /// <summary>
    /// ToolsDialog 回调：用户选择工具后触发。
    /// 将工具调用提示嵌入对话，供 LLM 执行。
    /// </summary>
    private void OnToolSelectedFromDialog(string pluginToolPath)
    {
        EmbedToolPrompt(pluginToolPath);
    }

    /// <summary>
    /// 将工具调用提示作为用户消息嵌入对话。
    /// LLM 将决定是否实际调用该工具。
    /// </summary>
    private void EmbedToolPrompt(string pluginToolPath)
    {
        var ct = _appCts?.Token ?? CancellationToken.None;
        _ = RunPromptAsync($"🔧 调用 {pluginToolPath} 工具！", ct);
    }

    // ── /commands 斜杠命令 ──

    private static void ShowCommandsDialog()
    {
        const string md = """
            # 可用命令

            | 命令 | 参数 | 说明 |
            |------|------|------|
            | `/commands` | — | 显示此帮助 |
            | `/help` | — | 关于 Mythology |
            | `/state` | — | Agent 状态 + 工具统计 + 写作统计 |
            | `/clear` | — | 清屏 |
            | `/theme` | `[dark/light/toggle]` | 切换主题 |
            | `/tools` | `[插件名]` | 列出工具分组或展开插件 |

            | `/resume` | `<novelId>` | 恢复会话 |
            | `/session` | — | 切换会话 |
            | `/compact` | `[keepRecent]` | 压缩历史（保留最近 N 条，默认 20） |
            | `/decompress` | `[blockId]` | 恢复被压缩的对话原文 |
            | `/context` | `[compact/continuation]` | 上下文管理模式（立即生效，无需重启） |
            | `/read` | `<序号\|ID\|标题片段>` | 只读章节阅读器（细纲 + 正文） |
            | `/chapter` | `<序号\|ID\|标题片段>` | 在大纲面板定位章节 |
            | `/search` | `<关键字>` | 全书检索（正文/细纲/标题，Enter 打开该章） |

            ---
            **工具执行**: `/tools <插件名>:<工具名> <JSON参数>`
            """;
        MarkdownDialog.Show("📋 命令列表", md);
    }

    // ── 未知命令处理（模糊匹配） ──

    /// <summary>
    /// 未知命令处理：Levenshtein 距离模糊匹配委托 SlashCommandParser.FindSimilarCommand（可单测）。
    /// </summary>
    private void HandleUnknownCommand(string cmd)
    {
        var allCommands = GetSlashCommands().Select(c => c.Split(' ')[0]).Distinct().ToList();
        var similar = SlashCommandParser.FindSimilarCommand(cmd, allCommands);

        ToastService.Warning(
            similar is not null
                ? $"未知命令 `{cmd}`，你是不是想用 `{similar}`？"
                : $"未知命令 `{cmd}`，输入 /commands 查看所有命令"
        );
    }

    /// <summary>
    /// 斜杠命令路由：命令识别与参数解析委托 SlashCommandParser（纯逻辑，可单测），
    /// 此处只做 UI/异步处理映射。支持 /tools、/session、/state、/history、/clear、
    /// /stats、/resume、/theme、/compact、/decompress、/context、/read、/chapter、/search、/tree、
    /// /mode、/variants(/var)、/cloud、/login、/logout 及 :cloud login|status|logout。
    /// </summary>
    private void HandleSlashCommand(string input)
    {
        var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return; // 空输入不路由（防御：正常路径不会到达）

        switch (SlashCommandParser.MapCommand(parts[0]))
        {
            case SlashCommandParser.SlashCommandKind.Tools:
                HandleToolsCommand(parts);
                break;
            case SlashCommandParser.SlashCommandKind.Session:
                ShowSessionPickerDialog();
                break;
            case SlashCommandParser.SlashCommandKind.State:
            case SlashCommandParser.SlashCommandKind.Stats: // /stats 别名 → 合并到 /state
                _ = ShowStateDialogAsync();
                break;
            case SlashCommandParser.SlashCommandKind.History:
                ShowHistoryDialog();
                break;
            case SlashCommandParser.SlashCommandKind.Clear:
                ClearConversation();
                break;
            case SlashCommandParser.SlashCommandKind.Resume:
            {
                var novelId = SlashCommandParser.ParseResumeNovelId(parts);
                if (novelId is null)
                {
                    ToastService.Info($"用法: {SlashCommandParser.Resume} <novel-id>");
                    break;
                }
                _ = ResumeSessionAsync(novelId);
                break;
            }
            case SlashCommandParser.SlashCommandKind.Theme:
                HandleThemeCommand(parts);
                break;
            case SlashCommandParser.SlashCommandKind.Compact:
            {
                // ★ 模式互斥：接续模式不用压缩（有损摘要会与换页语义互相干扰）
                if (_contextManagement is { Mode: ContextMode.Continuation })
                {
                    ToastService.Info("接续模式不使用压缩：上下文接近上限时会自动开启新页。");
                    break;
                }
                var keepRecent = _agent.ModelProfile?.ScaledKeepRecent ?? 20; // 与 SlashCommandParser.ParseCompact 默认值一致
                var compact = SlashCommandParser.ParseCompact(parts, keepRecent);
                _ = compact.Smart
                    ? CompactSessionSmartAsync(compact.KeepRecent)
                    : CompactSessionAsync(compact.KeepRecent);
                break;
            }
            case SlashCommandParser.SlashCommandKind.Decompress:
                _ = DecompressAsync(SlashCommandParser.ParseDecompressBlockId(parts));
                break;
            case SlashCommandParser.SlashCommandKind.Tree:
                ShowTreeSelectorDialog();
                break;
            case SlashCommandParser.SlashCommandKind.Mode:
                _ = HandleModeCommandAsync();
                break;
            case SlashCommandParser.SlashCommandKind.Context:
                _ = HandleContextCommandAsync(SlashCommandParser.ParseContext(parts));
                break;
            case SlashCommandParser.SlashCommandKind.Read:
                _ = OpenChapterReaderAsync(SlashCommandParser.ParseChapterRef(parts));
                break;
            case SlashCommandParser.SlashCommandKind.Chapter:
                _ = JumpToChapterAsync(SlashCommandParser.ParseChapterRef(parts));
                break;
            case SlashCommandParser.SlashCommandKind.Search:
                _ = SearchNovelAsync(SlashCommandParser.ParseChapterRef(parts));
                break;
            case SlashCommandParser.SlashCommandKind.Notes:
                ShowNotesDialog();
                break;
            case SlashCommandParser.SlashCommandKind.Commands:
                ShowCommandsDialog();
                break;
            case SlashCommandParser.SlashCommandKind.Help:
                AboutDialog.Show();
                ToastService.Info($"输入 {SlashCommandParser.Commands} 查看所有可用命令");
                break;
            case SlashCommandParser.SlashCommandKind.Variants:
                _ = ReopenLastVariantSetAsync();
                break;
            case SlashCommandParser.SlashCommandKind.Cloud:
                ShowCloudLoginDialog();
                break;
            case SlashCommandParser.SlashCommandKind.WeChat:
                HandleWeChatCommand(parts);
                break;
            case SlashCommandParser.SlashCommandKind.Login:
                HandleLoginCommand(parts);
                break;
            case SlashCommandParser.SlashCommandKind.Logout:
                CloudLogout();
                break;
            case SlashCommandParser.SlashCommandKind.CloudColon:
            {
                // :cloud login|status|logout（原先的 case ":cloud login" 等是死代码：
                // 路由只比较第一个 token，带空格的 case 永远匹配不上，全部落进未知命令）
                switch (SlashCommandParser.ParseColonCloud(parts))
                {
                    case SlashCommandParser.SlashCommandKind.CloudLogin:
                        ShowCloudLoginDialog();
                        break;
                    case SlashCommandParser.SlashCommandKind.CloudStatus:
                        _ = ShowCloudStatusAsync();
                        break;
                    case SlashCommandParser.SlashCommandKind.CloudLogout:
                        CloudLogout();
                        break;
                    case SlashCommandParser.SlashCommandKind.CloudSync:
                        _ = RunNovelSyncAsync(pullOnly: false);
                        break;
                    case SlashCommandParser.SlashCommandKind.CloudSyncAll:
                        _ = RunNovelSyncAllAsync(pullOnly: false);
                        break;
                    case SlashCommandParser.SlashCommandKind.CloudPull:
                        _ = RunNovelPullAsync();
                        break;
                    default:
                        HandleUnknownCommand(parts[0]);
                        break;
                }
                break;
            }
            default:
                HandleUnknownCommand(parts[0]);
                break;
        }
    }

    /// <summary>
    /// /context [compact|continuation] 命令处理：查看/切换上下文管理模式（热切换，无需重启）。
    /// 解析委托 <see cref="SlashCommandParser.ParseContext"/>（可单测），此处只做 UI 与落盘。
    /// </summary>
    private async Task HandleContextCommandAsync(SlashCommandParser.ContextArgs args)
    {
        var current =
            _contextModeSwitcher?.CurrentMode
            ?? (
                _agent.ContextManagement is { Mode: ContextMode.Continuation }
                    ? ContextMode.Continuation
                    : ContextMode.Compact
            );
        var currentLabel = current == ContextMode.Continuation ? "接续" : "压缩";

        if (args.Action == SlashCommandParser.ContextAction.Unknown)
        {
            ToastService.Warning(
                $"未知模式「{args.OriginalArg}」：用法 /context [compact|continuation]"
            );
            return;
        }

        if (args.Action == SlashCommandParser.ContextAction.Show || args.Mode is null)
        {
            ToastService.Info(
                $"当前上下文管理模式：{currentLabel}（/context compact 或 /context continuation 切换，立即生效）"
            );
            return;
        }

        if (_contextModeSwitcher is null)
        {
            ToastService.Warning("上下文管理模式不可切换：Host 未接线（请重启应用）。");
            return;
        }

        var (ok, message) = await _contextModeSwitcher.SwitchModeAsync(args.Mode);

        if (ok)
        {
            // 内存副本同步（设置页下次打开时显示新模式；磁盘已由切换器写入）
            var previous = _settingsService.ContextManagement;
            _settingsService.ContextManagement = new ContextManagementConfig
            {
                Mode = args.Mode,
                ReminderPercent = previous.ReminderPercent,
                VectorRetrieval = previous.VectorRetrieval,
            };
            ToastService.Success(message);
        }
        else
        {
            ToastService.Warning(message);
        }
    }

    /// <summary>
    /// /read [序号|ID|标题片段]：打开只读章节阅读器（细纲带 + 正文）。
    /// 长篇列表只给骨架行，正文与细纲在这里按需展开——不必去翻 chapters/*.md 文件。
    /// </summary>
    private async Task OpenChapterReaderAsync(string? query)
    {
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            ToastService.Warning("当前没有加载小说");
            return;
        }

        var structure = await _store.GetStructure(novelId);
        var chapters = structure?.Chapters ?? [];
        var (chapter, message) = ChapterLocator.Resolve(chapters, query);
        if (chapter is null)
        {
            ToastService.Warning(message ?? ChapterLocator.UsageHint);
            return;
        }

        try
        {
            await OpenReaderAsync(novelId, chapters, chapter);
        }
        catch (Exception ex)
        {
            ToastService.Error($"打开章节失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 打开阅读器（/read 与 /search 结果共用）：只预取名录（卷/情节线/角色，都是小集合），
    /// **正文与按章演化记录按需单章读取**（ch{Order:D3}.md / ch{Order:D3}.yaml），翻章不重读全书。
    /// </summary>
    private async Task OpenReaderAsync(
        string novelId,
        IReadOnlyList<Chapter> chapters,
        Chapter chapter
    )
    {
        var volumes = await _store.GetVolumes(novelId);
        var plotlineNames = (await _store.GetPlotlines(novelId)).ToDictionary(
            p => p.Id,
            p => p.Name
        );
        var characterNames = (await _store.GetCharacters(novelId)).ToDictionary(
            c => c.Id,
            c => c.Name
        );
        var locationNames = (await _store.GetLocations(novelId)).ToDictionary(
            l => l.Id,
            l => l.Name
        );
        var orderById = chapters.ToDictionary(c => c.Id, c => c.Order, StringComparer.Ordinal);

        await ChapterReaderDialog.ShowAsync(
            chapters,
            chapter,
            async (chapterId, ct) =>
            {
                var loaded = orderById.TryGetValue(chapterId, out var order)
                    ? await _store.GetChapterByOrder(novelId, order, ct)
                    : null;
                loaded ??= await _store.GetChapter(novelId, chapterId, ct);
                if (loaded is null)
                    return null;

                return new ChapterReaderData(
                    loaded,
                    await _store.GetAppearancesByChapter(novelId, chapterId, ct),
                    await _store.GetEntityStateChangesByChapter(novelId, chapterId, ct)
                );
            },
            c =>
                c.VolumeId is { Length: > 0 } volumeId
                    ? volumes.FirstOrDefault(v => v.Id == volumeId)
                    : null,
            id => plotlineNames.GetValueOrDefault(id, id),
            id => characterNames.GetValueOrDefault(id, id),
            id => locationNames.GetValueOrDefault(id, id)
        );
    }

    /// <summary>
    /// /search &lt;关键字&gt;：全书只读检索（正文/细纲/标题），命中列表 Enter 直接打开该章。
    /// 离线可用、结果确定；语义检索（RetrievalKernel）是另一条通道，不在这里混。
    /// </summary>
    private async Task SearchNovelAsync(string? query)
    {
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            ToastService.Warning("当前没有加载小说");
            return;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            ToastService.Warning("用法: /search <关键字>");
            return;
        }

        var chapters = (await _store.GetChapters(novelId)).OrderBy(c => c.Order).ToList();
        var hits = NovelTextSearch.Search(chapters, query);
        var navigation = (await _store.GetStructure(novelId))?.Chapters ?? chapters;
        ChapterSearchDialog.Show(
            query.Trim(),
            hits,
            chapters,
            chapter =>
            {
                _ = OpenReaderFromSearchAsync(novelId, navigation, chapter);
            }
        );
    }

    /// <summary>/search 命中打开阅读器（异步包装：OptionList 回调是同步的）。</summary>
    private async Task OpenReaderFromSearchAsync(
        string novelId,
        IReadOnlyList<Chapter> navigation,
        Chapter chapter
    )
    {
        try
        {
            await OpenReaderAsync(novelId, navigation, chapter);
        }
        catch (Exception ex)
        {
            ToastService.Error($"打开章节失败: {ex.Message}");
        }
    }

    /// <summary>
    /// /chapter [序号|ID|标题片段]：在大纲面板定位章节（切到「大纲」页签 + 展开所属卷 + 高亮）。
    /// 与 /read 分工：这个只回答「这章在哪」，不打开正文。
    /// </summary>
    private async Task JumpToChapterAsync(string? query)
    {
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            ToastService.Warning("当前没有加载小说");
            return;
        }

        var chapters = (await _store.GetStructure(novelId))?.Chapters ?? [];
        var (chapter, message) = ChapterLocator.Resolve(chapters, query);
        if (chapter is null)
        {
            ToastService.Warning(message ?? ChapterLocator.UsageHint);
            return;
        }

        _setUpPanel.NavigateToEntity(EntityType.Chapter, chapter.Id);
        await RefreshOutline();
        ToastService.Info($"已定位到第 {chapter.Order} 章「{chapter.Title}」");
    }

    /// <summary>
    /// /theme 命令处理：参数解析委托 SlashCommandParser.ParseTheme（可单测），此处只做 UI 映射。
    /// </summary>
    private void HandleThemeCommand(string[] parts)
    {
        var theme = SlashCommandParser.ParseTheme(parts);
        switch (theme.Action)
        {
            case SlashCommandParser.ThemeAction.Picker:
                // /theme 无参 → 弹出主题选择器（↑/↓ 预览，Enter 保存）
                ThemePickerDialog.Show(_themeService, _settingsService);
                break;
            case SlashCommandParser.ThemeAction.Toggle:
                _themeService.NextTheme();
                ToastService.Info($"主题: {_themeService.Current.Name}");
                break;
            case SlashCommandParser.ThemeAction.Set:
                _themeService.SetTheme(theme.ThemeId!);
                _themeService.PersistTheme();
                _ = _settingsService.SaveThemeAsync();
                ToastService.Info($"主题: {ThemeRegistry.Get(theme.ThemeId!)?.Name}");
                break;
            default:
                ToastService.Warning(
                    $"未知主题: {theme.OriginalArg}，可用: {string.Join(", ", ThemeRegistry.All.Select(t => t.Id))} 或 {SlashCommandParser.ThemeToggle}"
                );
                break;
        }
    }

    /// <summary>
    /// Command.Execute 回调适配器：将 async ShowStateDialogAsync 转为 Action&lt;Visual&gt;。
    /// </summary>
    private void InvokeShowState(Visual v)
    {
        _ = ShowStateDialogAsync();
    }

    /// <summary>
    /// 显示 Agent 状态对话框：运行信息 + 工具调用统计 + 写作统计 + System Prompt 摘要。
    /// 原 /stats 已合并入此对话框。
    /// </summary>
    private async Task ShowStateDialogAsync()
    {
        var s = _agent.State;
        var sb = new StringBuilder();

        // ── 运行信息 ──
        sb.AppendLine("# Agent 状态");
        sb.AppendLine();
        sb.AppendLine("| 属性 | 值 |");
        sb.AppendLine("|------|-----|");
        sb.AppendLine($"| **Model** | {s.ModelName} |");
        sb.AppendLine($"| **ThinkingLevel** | {s.ThinkingLevel} |");
        sb.AppendLine($"| **Tools** | {s.Tools.Count} |");
        sb.AppendLine($"| **Messages** | {s.Messages.Count} |");
        sb.AppendLine($"| **IsStreaming** | {s.IsStreaming} |");
        sb.AppendLine($"| **IsRunning** | {_agent.IsRunning} |");
        sb.AppendLine($"| **PendingToolCalls** | {s.PendingToolCalls.Count} |");
        sb.AppendLine($"| **Error** | {s.Error ?? "(无)"} |");
        sb.AppendLine();

        // ── 工具调用统计 ──
        var stats = _agent.UsageStats;
        if (stats is not null && stats.TotalCalls > 0)
        {
            sb.AppendLine("## 工具调用统计");
            sb.AppendLine();
            sb.AppendLine(stats.GetSummary());
            sb.AppendLine();
        }

        // ── 写作统计 ──
        var novelId = _agent.CurrentNovelId;
        if (!string.IsNullOrEmpty(novelId))
        {
            try
            {
                var totalWords = await _store.GetTotalWordCount(novelId);
                var chapterCount = await _store.GetChapterCount(novelId);
                var characterCount = await _store.GetCharacterCount(novelId);
                sb.AppendLine("## 写作统计");
                sb.AppendLine($"- 总字数: {totalWords}");
                sb.AppendLine($"- 章节: {chapterCount}");
                sb.AppendLine($"- 角色: {characterCount}");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                Services.TuiLogger.OperationFailed(_logger, ex, "query writing statistics");
            }
        }

        // ── System Prompt 预览 ──
        sb.AppendLine("## System Prompt");
        sb.AppendLine();
        sb.AppendLine(Truncate(s.SystemPrompt, 500));

        MarkdownDialog.Show("🤖 Agent 状态", sb.ToString());
    }

    /// <summary>显示对话历史对话框：按角色列出所有消息的截断内容。</summary>
    private void ShowHistoryDialog()
    {
        var msgs = _agent.State.Messages;
        var sb = new StringBuilder();
        sb.AppendLine("# 对话历史");
        sb.AppendLine();
        sb.AppendLine($"共 **{msgs.Count}** 条消息");
        sb.AppendLine();

        foreach (var m in msgs)
        {
            // 上下文接续机制对用户隐藏：隐藏工具的调用与结果不列出
            if (HiddenTools.IsHidden(m.Name))
                continue;

            var txt = m.Content ?? "";
            if (m.Parts is not null && m.Parts.Count > 0)
            {
                txt = string.Join(
                    "",
                    m.Parts.Where(p => p.Type == ChatMessageTypes.Text).Select(p => p.Text)
                );
            }
            var role = m.Role switch
            {
                ChatMessageRoles.User => "👤 你",
                ChatMessageRoles.Assistant => "🤖 Mythology",
                ChatMessageRoles.System => "🔧 系统",
                _ => m.Role?.ToString() ?? "unknown",
            };
            sb.AppendLine($"### {role}");
            sb.AppendLine();
            sb.AppendLine(Truncate(txt, 300));
            sb.AppendLine();
        }

        MarkdownDialog.Show("📜 对话历史", sb.ToString());
    }

    /// <summary>显示小说项目选择对话框，选择后打开对应小说和会话。</summary>
    private void ShowNovelProjectDialog()
    {
        new NovelProjectDialog(
            _store,
            _sessionStore,
            (novelId, sessionId) => _ = OpenNovelAsync(novelId, sessionId)
        ).Show();
    }

    /// <summary>
    /// 启动引导（docs/DESKTOP-LOGIN-FLOW.md §6 的 TUI 形态，与 Desktop onboarding 对齐）：
    /// 1. 云端：新用户（无凭据 + 无小说）→ 默认弹登录框（Esc/取消可跳过）；
    /// 2. 登录成功 + 无小说 → 直达新建项目表单（onboarding 衔接）；
    /// 3. 否则 → 常规项目选择/新建。
    /// 凭据失效（401/403）由 InitializeCloudAsync/ProbeCloudAsync 清除并 Toast，此处无需重复处理。
    /// </summary>
    private async Task RunStartupOnboardingAsync()
    {
        try
        {
            var novels = await _store.GetAllNovels();
            var isNewUser = _cloudSession.Current is null && novels.Count == 0;

            // 1. 新用户默认弹登录（仅配置了云端地址时；可跳过，不绑架本地创作）。
            //    弹框前用短预算（StartupLoginPrecheckTimeout）预检连通性：云端不可达时
            //    直接跳过自动登录 + Toast，避免登录握手把启动拖住「等了半天才进去」。
            if (isNewUser)
            {
                var cloudUrl = Environment.GetEnvironmentVariable("MYTHOLOGY_CLOUD_URL");
                if (!string.IsNullOrEmpty(cloudUrl))
                {
                    using var precheckCts = new CancellationTokenSource();
                    precheckCts.CancelAfter(CloudTimings.StartupLoginPrecheckTimeout);
                    var precheckState = await _cloudSession.ProbeAsync(precheckCts.Token);
                    if (
                        precheckState
                        is CloudConnectionState.Connected
                            or CloudConnectionState.RateLimited
                            or CloudConnectionState.QuotaExceeded
                    )
                    {
                        ToastService.Info("未登录云端 — 打开登录…（Esc 可跳过）");
                        await ShowCloudLoginDialogAsync();
                    }
                    else
                    {
                        ToastService.Warning(
                            "云端暂不可达，本次跳过自动登录 — 可稍后用 :cloud login 登录。"
                        );
                    }
                }
            }

            // 2. 登录成功 + 无小说 → onboarding 衔接：直达新建项目（无小说时自动进新建表单）
            if (_cloudSession.IsConfigured && novels.Count == 0)
            {
                ToastService.Success("🌐 云端已就绪 — 创建你的第一部小说吧！");
                if (_agent.TornadoApi is null)
                    ToastService.Warning("❗ 未配置 LLM API Key — 创建后请用 Ctrl+P 设置提供商");
                ShowNovelProjectDialog();
                return;
            }

            // 3. 常规：项目选择/新建
            ShowNovelProjectDialog();
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "run startup onboarding");
            ShowNovelProjectDialog(); // 兜底：保证项目选择器必出
        }
    }

    /// <summary>显示会话切换对话框，在当前小说的会话列表中选择。</summary>
    private void ShowSessionPickerDialog()
    {
        var novelId = _agent.CurrentNovelId;
        new SessionPickerDialog(
            _store,
            _sessionStore,
            (nid, sessionId) => _ = OpenNovelAsync(nid, sessionId),
            novelId
        ).Show();
    }

    /// <summary>/notes：作者随手记（确认候选 / 查看 / 编辑）。</summary>
    private void ShowNotesDialog()
    {
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            ToastService.Info("请先打开小说项目");
            return;
        }
        _ = new NotesDialog(_store, _retrievalKernel, novelId).ShowAsync();
    }

    // ── /mode 斜杠命令处理 ──

    /// <summary>云端登录对话框是否已打开（并发闸门：吊销总线 / 探活 401 / 启动引导 / 用户命令重叠时只弹一个模态框）。</summary>
    private bool _cloudLoginDialogOpen;

    /// <summary>
    /// /mode 斜杠命令处理。切换增强模式（手动→半自动→全自动）并持久化到小说配置。
    /// </summary>
    private async Task HandleModeCommandAsync()
    {
        var newMode = _statusBar.CycleEnhanceMode();
        var modeLabel = newMode switch
        {
            "none" => "手动",
            "semi" => "半自动",
            "full" => "全自动",
            _ => newMode,
        };

        // 更新 Novel 配置
        var novelId = _agent.CurrentNovelId;
        if (!string.IsNullOrEmpty(novelId))
        {
            try
            {
                var novel = await _store.GetNovel(novelId);
                if (novel is not null)
                {
                    novel.AutoEnhance = newMode;
                    using (Core.Evolution.OntologyWriteContext.BeginUser())
                    {
                        await _store.SaveNovel(novel);
                    }

                    // 重新注入 WritingProfile
                    var profile = WritingProfile.FromNovel(novel);
                    _agent.SetNovelContext(profile.ToContextString());
                }
            }
            catch (Exception ex)
            {
                Services.TuiLogger.OperationFailed(_logger, ex, "persist enhancement mode");
                // 模式切换已生效（UI 侧），仅持久化失败——不阻断用户
            }
        }

        ToastService.Info($"增强模式: {modeLabel}");
    }

    /// <summary>显示对话分支树选择器，导航到历史分支点。</summary>
    private void ShowTreeSelectorDialog()
    {
        if (string.IsNullOrEmpty(_sessionId))
        {
            ToastService.Warning("请先打开一个会话");
            return;
        }

        new TreeSelectorPanel(_sessionStore, result => _ = HandleTreeNavigationAsync(result)).Show(
            _sessionId
        );
    }

    /// <summary>
    /// 云端状态栏/Toast 更新统一编组到终端 Dispatcher 线程（冒烟测试发现：心跳/探活的
    /// await continuation 在 thread-pool 线程，直接触控状态栏抛 InvalidOperationException
    /// "Invalid thread access"，导致心跳首次探活后死亡、状态栏永不刷新——修复见 §8 心跳）。
    /// 启动探活早于 Terminal.RunAsync（Dispatcher 未绑定 TerminalApp）时 Post 抛
    /// "Dispatcher is not attached to a running TerminalApp"——此时入队，由首帧统一重放，
    /// 避免 fire-and-forget 吞掉异常导致状态栏永远停在「登录中」。
    /// 状态（ConnectionState/store/heartbeat CTS）更新线程安全，不编组。
    /// </summary>
    private static void PostToUi(Action action)
    {
        var dispatcher = XenoAtom.Terminal.UI.Threading.Dispatcher.Current;
        try
        {
            dispatcher.Post(action);
        }
        catch (InvalidOperationException)
        {
            // TerminalApp 尚未绑定 Dispatcher（启动窗口）→ 入队，首帧 DrainPendingUiActions 重放
            PendingUiActions.Enqueue(action);
        }
    }

    /// <summary>启动窗口（Dispatcher 未绑定）积压的 UI 更新；首帧（app 就绪后）重放。</summary>
    private static readonly ConcurrentQueue<Action> PendingUiActions = new();

    /// <summary>首帧（TerminalApp 已绑定 Dispatcher）重放启动期间积压的 UI 更新。</summary>
    private void DrainPendingUiActions()
    {
        while (PendingUiActions.TryDequeue(out var action))
            action();
    }

    /// <summary>
    /// CHANGE_07 §12.3.1: 启动时初始化云端状态 — 加载凭据、更新状态栏、启动心跳。
    /// 无凭据时不做打扰（新用户引导由 RunStartupOnboardingAsync 负责）；
    /// 凭据失效（401/403）由 ProbeCloudAsync 清除并提示。
    /// </summary>
    private async Task InitializeCloudAsync()
    {
        try
        {
            _cloudSession.Load();
            SetCloudState(_cloudSession.ConnectionState);

            if (_cloudSession.IsConfigured)
            {
                // 已有凭据 → 验证连通性后启动心跳
                _ = ProbeCloudAsync();
                StartCloudHeartbeat();
                // 已登录 → 启动时自动同步云端最新（静默，失败只转 Offline 标识不打扰创作；
                // 与手动 :cloud sync 经 Service 串行闸门互斥）
                _ = RunStartupSyncAsync();
            }
        }
        catch (Exception ex)
        {
            TuiLogger.OperationFailed(_logger, ex, "initialize cloud connection");
        }
    }

    /// <summary>
    /// §13.6 事件驱动失效检测编排：CloudConnectionService 已清除凭据并降级状态，
    /// 此处只做 TUI 侧编排——停心跳 + Toast + **自动激活登录**（默认本地回调 OAuth，
    /// 浏览器自动打开授权页；SSH 环境自动走设备流）。
    /// 可能来自后台 HTTP 线程；ToastService/对话框经 PostToUi 编排。
    /// 自动弹窗由 ShowCloudLoginDialogAsync 的并发闸门兜底（吊销总线与探活同时触发只弹一次）。
    /// </summary>
    private void OnCloudCredentialsInvalidated()
    {
        PostToUi(() => SetCloudState(CloudConnectionState.Disabled));
        _heartbeatCts?.Cancel();
        // 与心跳 401 / 探活 401 共用同一节流键（Authenticating），避免吊销事件连续触发时重复轰炸
        if (
            !_cloudToastThrottle.ShouldSuppress(
                CloudConnectionState.Authenticating,
                DateTimeOffset.UtcNow
            )
        )
            PostToUi(() =>
            {
                ToastService.Warning("云端登录已失效（API 调用被拒绝），正在打开登录向导…");
                _ = ShowCloudLoginDialogAsync();
            });
    }

    /// <summary>
    /// CHANGE_07 §8: 云端心跳 — 每 5 分钟轻量 GET /v1/capabilities 刷新连接状态。
    /// 不打扰创作，失败时静默降级状态栏。
    /// </summary>
    private void StartCloudHeartbeat()
    {
        if (!_cloudSession.IsConfigured)
            return; // 无有效凭据 → 不启动心跳
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _heartbeatCts = new CancellationTokenSource();
        _ = HeartbeatLoopAsync(_heartbeatCts.Token);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CloudTimings.HeartbeatInterval, ct); // 心跳间隔共享收口（Desktop / TUI 共用）
                await ProbeCloudAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Services.TuiLogger.OperationFailed(_logger, ex, "probe cloud heartbeat");
                _cloudSession.SetConnectionState(CloudConnectionState.Unavailable);
                PostToUi(() => SetCloudState(CloudConnectionState.Unavailable));
            }
        }
    }

    /// <summary>
    /// 探测云端连通性 — GET /v1/capabilities（HTTP 收口在 CloudConnectionService.ProbeAsync，
    /// §13.7 与 Desktop CloudAuthService.ProbeAsync 对称），此处只做 TUI 编排：更新状态栏、
    /// 401/403 时清除凭据 + 停心跳 + Toast。
    /// </summary>
    private async Task ProbeCloudAsync()
    {
        if (string.IsNullOrEmpty(_cloudSession.ApiKey))
        {
            _cloudSession.SetConnectionState(CloudConnectionState.Authenticating);
            PostToUi(() => SetCloudState(CloudConnectionState.Authenticating));
            return;
        }

        var state = await _cloudSession.ProbeAsync();

        if (state == CloudConnectionState.Authenticating)
        {
            // 凭据被服务端拒绝（401/403）→ 清除本地凭据（对齐 Desktop Invalid 语义），
            // 避免"幽灵登录"：状态栏显示未登录，并停止心跳（凭据已无，无需再探测）。
            // 原始 401 详情写入日志（含本地最后记录的到期时间），TUI 只做 Toast 提醒。
            var expiresAt = _cloudSession.ExpiresAt;
            _logger.LogWarning(
                "云端凭据被服务端拒绝（探活 401/403），已清除本地凭据。本地最后记录的过期时间: {ExpiresAt:o}",
                expiresAt
            );
            _cloudSession.Clear();
            PostToUi(() => SetCloudState(CloudConnectionState.Disabled));
            if (_heartbeatCts is not null)
                await _heartbeatCts.CancelAsync();
            // 与吊销事件 / 探活 401 共用同一节流键（Authenticating），避免重启/心跳连续命中 401 时重复轰炸
            if (
                !_cloudToastThrottle.ShouldSuppress(
                    CloudConnectionState.Authenticating,
                    DateTimeOffset.UtcNow
                )
            )
            {
                // 已过期 → 直接说明原因（过期窗口，滑动续期后一般不再触发），否则笼统提示吊销
                var hint =
                    expiresAt is { } exp && exp <= DateTimeOffset.UtcNow
                        ? $"云端登录已过期（{exp:yyyy-MM-dd}），正在打开登录向导…"
                        : "云端登录已失效（API Key 被拒绝），正在打开登录向导…";
                PostToUi(() =>
                {
                    ToastService.Warning(hint);
                    // 自动激活登录（本地回调 OAuth → 浏览器自动打开授权页；SSH 走设备流）
                    _ = ShowCloudLoginDialogAsync();
                });
            }
            return;
        }

        PostToUi(() => SetCloudState(state));
    }

    /// <summary>
    /// 云端登录对话框（CHANGE_09 形态 B）— 默认本地回调 OAuth，失败回退设备流。
    /// 配置：共享收口 CloudEndpoints（环境变量 MYTHOLOGY_CLOUD_URL，默认 http://localhost:5080），可用 /login &lt;url&gt; 临时覆盖。
    /// </summary>
    private async Task ShowCloudLoginDialogAsync(
        string? baseUrlOverride = null,
        bool forceDeviceFlow = false
    )
    {
        // 并发闸门：吊销总线、探活 401、启动引导、用户 :cloud login 可能在同一窗口重叠，
        // 登录对话框是模态的——同时只允许一个（模态对话框如果重复 Open 会叠层/状态混乱）。
        if (_cloudLoginDialogOpen)
            return;
        _cloudLoginDialogOpen = true;
        try
        {
            await ShowCloudLoginDialogCoreAsync(baseUrlOverride, forceDeviceFlow);
        }
        finally
        {
            _cloudLoginDialogOpen = false;
        }
    }

    private async Task ShowCloudLoginDialogCoreAsync(
        string? baseUrlOverride = null,
        bool forceDeviceFlow = false
    )
    {
        var baseUrl = baseUrlOverride?.Trim().TrimEnd('/') ?? CloudEndpoints.ResolveCloudBaseUrl();

        // 已登录预检：提示当前凭据，仍继续打开（用户可能想重新登录）
        if (_cloudSession.IsConfigured)
        {
            var label = _cloudSession.DisplayName is { Length: > 0 } dn
                ? dn
                : _cloudSession.PublicId;
            ToastService.Info($"已登录云端（{label}），重新登录将覆盖现有凭据。");
        }

        var dialog = new CloudLoginDialog(
            _cloudSession,
            baseUrl,
            forceDeviceFlow,
            _cloudToastThrottle
        );
        var success = await dialog.ShowAsync(CancellationToken.None);

        if (success)
        {
            PostToUi(() => SetCloudState(_cloudSession.ConnectionState));
            PostToUi(() => ToastService.Success("已登录 Mythology Cloud，数据面 API Key 已保存。"));
            StartCloudHeartbeat();
            _ = ProbeAfterLoginAsync(); // §13.7：登录后立即端到端探活，失败降级状态栏 + Toast
        }
    }

    /// <summary>
    /// §13.7 登录后端到端连通性探活（与 Desktop CloudAuthService.ProbeAfterLoginAsync 对齐）：
    /// 登录协议（OAuth/设备流）的 token 端点已确认 Key 有效，但客户端/心跳实际消费的是
    /// GET /v1/capabilities——登录成功后立即用真实调用验证整条链路。
    /// 失败不否定登录（凭据已保存、心跳已启动自动重试），仅降级状态栏 + Toast 提示；
    /// 降级 Toast 经 ProbeToastThrottle 节流——同一降级原因在冷却窗口内只提示一次，
    /// 避免登录窗口连续重试/多次登录时重复轰炸同一提示。
    ///
    /// 瞬时不可达（Unavailable）重试一次：登录协议刚刚成功，后端一定是可达的——
    /// 首个 capabilities 请求超时/连接抖动更可能是冷启动或瞬时网络问题，
    /// 直接降级 ❌ 会让用户看到「登录成功但云端不可用」的矛盾体验。重试一次仍失败才降级。
    ///
    /// 线程模型：全流程 ConfigureAwait(false) 停在线程池（不捕获 UI 上下文），UI 更新统一经 PostToUi 编组——
    /// 应用退出窗口内续体不回投已脱离的 Dispatcher（防进程中止；与 CloudLoginDialog.RunAfterDelay 同类）。
    /// </summary>
    private async Task ProbeAfterLoginAsync()
    {
        var state = await _cloudSession.ProbeAsync().ConfigureAwait(false);

        // 瞬时不可达 → 短暂等待后重试一次（凭据未清除，重试成功则保持 Connected）
        if (state == CloudConnectionState.Unavailable)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            state = await _cloudSession.ProbeAsync().ConfigureAwait(false);
        }

        var suppressToast = _cloudToastThrottle.ShouldSuppress(state, DateTimeOffset.UtcNow);

        // Connected → null（静默成功，闸门已重置）；其余 → 降级提示文案
        var hint = state switch
        {
            CloudConnectionState.Connected => null,
            CloudConnectionState.Authenticating =>
                "✅ 登录成功，但凭据校验异常 — 请重新 :cloud login。",
            CloudConnectionState.RateLimited => "✅ 登录成功，云端限流 — 稍后自动重试。",
            CloudConnectionState.QuotaExceeded => "✅ 登录成功，云端配额已用尽。",
            _ => "✅ 登录成功，云端暂不可达 — 稍后自动重试。",
        };
        if (hint is null)
            return; // 端到端验证通过，状态栏保持 Connected

        if (state == CloudConnectionState.Authenticating)
        {
            // 签发后立即被拒（服务端不一致）→ 清除凭据 + 提示重登（对齐 TUI 既有 401 语义）
            _cloudSession.Clear();
            PostToUi(() => SetCloudState(CloudConnectionState.Disabled));
            if (_heartbeatCts is not null)
                await _heartbeatCts.CancelAsync().ConfigureAwait(false);
        }
        else
        {
            PostToUi(() => SetCloudState(state));
        }

        if (!suppressToast)
            PostToUi(() => ToastService.Warning(hint));
    }

    /// <summary>
    /// /login 命令处理：参数解析委托 SlashCommandParser.ParseLogin（可单测），此处只做 UI 映射。
    /// 默认本地回调 OAuth，--device 强制设备流，&lt;url&gt; 云端地址覆盖。
    /// SSH 环境自动跳过本地回调（决策 D-C）。
    /// 三态统一入口：本地回调（默认）→ 失败时一键「改用设备码」→ 手动粘贴 mk_live_ Key 兜底。
    /// </summary>
    private void HandleLoginCommand(string[] parts)
    {
        var login = SlashCommandParser.ParseLogin(parts);

        if (login.UnknownArg is not null)
        {
            ToastService.Warning(
                $"未知参数: {login.UnknownArg}，用法: {SlashCommandParser.Login} [--device] [<url>]"
            );
            return;
        }

        if (login.ForceDevice)
            ToastService.Info("设备流模式：二维码 + 用户码登录。");
        else if (OAuthLoopbackClient.IsSshSession())
            ToastService.Info("检测到 SSH 环境，自动使用设备流登录。");

        _ = ShowCloudLoginDialogAsync(login.BaseUrlOverride, login.ForceDevice);
    }

    /// <summary>
    /// 云端登录 — 同步入口（从 :cloud login 命令调用，默认本地回调 OAuth）。
    /// </summary>
    private void ShowCloudLoginDialog() => _ = ShowCloudLoginDialogAsync();

    // ═══════════════════════════════════════════════════════════════
    // 微信通道（/wechat bind|status|unbind）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// /wechat 命令族。通道未接线（Host 未注入服务）时只提示，不当错误——测试环境与
    /// 未启用通道的入口都会走到这里。
    /// </summary>
    private void HandleWeChatCommand(string[] parts)
    {
        if (_weChat is null)
        {
            ToastService.Info("微信通道未接线（当前入口未启用）");
            return;
        }

        switch (SlashCommandParser.ParseWeChatSubcommand(parts))
        {
            case SlashCommandParser.SlashCommandKind.WeChatBind:
                _ = ShowWeChatBindAsync();
                break;
            case SlashCommandParser.SlashCommandKind.WeChatStatus:
                ShowWeChatStatus();
                break;
            case SlashCommandParser.SlashCommandKind.WeChatUnbind:
                UnbindWeChat();
                break;
            default:
                ToastService.Info(
                    $"用法：{SlashCommandParser.WeChat} {SlashCommandParser.WeChatBind}|{SlashCommandParser.WeChatStatus}|{SlashCommandParser.WeChatUnbind}"
                );
                break;
        }
    }

    /// <summary>扫码绑定（模态框：同时只允许一个，与 :cloud login 同闸门语义）。</summary>
    private async Task ShowWeChatBindAsync()
    {
        if (_weChat is null || _weChatDialogOpen)
            return;

        _weChatDialogOpen = true;
        try
        {
            var before = _weChat.Status.Snapshot;
            if (before.IsBound)
                ToastService.Info("已绑定微信，重新扫码将覆盖现有绑定。");

            var bound = await new WeChatBindDialog(_weChat).ShowAsync(CancellationToken.None);
            var after = _weChat.Status.Snapshot;

            if (bound)
                ToastService.Success("已绑定微信，之后可以直接在微信里指挥本机助手。");
            else if (after.Phase == WeChatPhase.Failed)
                ToastService.Error($"微信绑定失败：{after.Error}");
        }
        finally
        {
            _weChatDialogOpen = false;
        }
    }

    private void ShowWeChatStatus()
    {
        if (_weChat is null)
            return;

        var snapshot = _weChat.Status.Snapshot;
        ToastService.Info(
            snapshot.Phase switch
            {
                WeChatPhase.Polling when snapshot.PeerUserId is { Length: > 0 } peer =>
                    $"微信：已绑定（{peer}）",
                WeChatPhase.Polling => "微信：已绑定",
                WeChatPhase.Restoring => "微信：正在恢复绑定…",
                WeChatPhase.WaitingScan or WeChatPhase.Scanned or WeChatPhase.Connecting =>
                    "微信：绑定进行中…",
                WeChatPhase.Failed => $"微信：异常（{snapshot.Error}）",
                _ =>
                    $"微信：未绑定（{SlashCommandParser.WeChat} {SlashCommandParser.WeChatBind} 扫码绑定）",
            }
        );
    }

    /// <summary>
    /// 解绑。没拿到独占权时拒绝：另一个入口可能正拿内存里的凭证轮询，
    /// 单方面删掉凭证文件会让它处于"没有凭证但还在轮询"的悬挂态。
    /// </summary>
    private void UnbindWeChat()
    {
        if (_weChat is null)
            return;

        if (!_weChat.State.IsBound)
        {
            ToastService.Info("当前未绑定微信。");
            return;
        }

        if (!_weChat.Unbind())
        {
            ToastService.Error("解绑失败：微信通道正被另一个入口使用，请先关闭它。");
            return;
        }

        ToastService.Success("已解绑微信，绑定凭证已清除。");
    }

    /// <summary>
    /// 显示云端连接状态。
    /// </summary>
    /// <summary>
    /// 启动/登录后自动同步：已打开小说 → 双向同步；未打开 → 仅拉取（bootstrap 也写回远端空时零动作）。
    /// 静默运行：结果不弹 Toast（不打扰创作），仅状态栏 ⇅ 标识变化；异常已由 Service 转 Offline。
    /// </summary>
    private async Task RunStartupSyncAsync()
    {
        if (_novelSync is null)
        {
            return;
        }
        var novelId = _agent.CurrentNovelId;
        try
        {
            if (!string.IsNullOrEmpty(novelId))
            {
                await _novelSync.SyncAsync(novelId, pullOnly: false);
            }
            else
            {
                // 未打开小说：拉取云端全部（bootstrap）；云端空/无网时零动作
                await _novelSync.PullAllNovelsAsync();
            }
        }
        catch (CloudApiException exception)
        {
            // 启动同步失败不弹 Toast（登录后凭据问题已有吊销总线兜底），仅日志
            _logger?.LogWarning(exception, "Startup cloud sync skipped: {Code}", exception.Code);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Startup cloud sync failed");
        }
    }

    /// <summary>:cloud sync / :cloud pull — 当前打开小说的双向同步 / 仅拉取。</summary>
    private async Task RunNovelSyncAsync(bool pullOnly)
    {
        if (_novelSync is null)
        {
            ToastService.Warning("同步服务未启用");
            return;
        }
        if (_cloudSession.ConnectionState != CloudConnectionState.Connected)
        {
            ToastService.Warning("请先 :cloud login 登录云端");
            return;
        }
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            ToastService.Warning("未打开小说，使用 :cloud pull 拉取全部");
            return;
        }
        try
        {
            var report = await _novelSync.SyncAsync(
                novelId,
                pullOnly,
                new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token
            );
            await ShowSyncReportAsync(report);
        }
        catch (CloudApiException exception)
        {
            ToastService.Error($"同步失败：{exception.Message}");
        }
        catch (OperationCanceledException)
        {
            // 用户取消/超时：静默
        }
        catch (Exception exception)
        {
            ToastService.Error($"同步失败：{exception.Message}");
        }
    }

    /// <summary>:cloud pull 无当前小说 → bootstrap 拉取云端全部小说。</summary>
    private async Task RunNovelPullAsync()
    {
        if (_novelSync is null)
        {
            ToastService.Warning("同步服务未启用");
            return;
        }
        if (_cloudSession.ConnectionState != CloudConnectionState.Connected)
        {
            ToastService.Warning("请先 :cloud login 登录云端");
            return;
        }
        if (!string.IsNullOrEmpty(_agent.CurrentNovelId))
        {
            await RunNovelSyncAsync(pullOnly: true);
            return;
        }
        try
        {
            var novels = await _novelSync.PullAllNovelsAsync(
                new CancellationTokenSource(TimeSpan.FromMinutes(10)).Token
            );
            ToastService.Info($"拉取完成：{novels.Count} 本小说已同步");
        }
        catch (CloudApiException exception)
        {
            ToastService.Error($"拉取失败：{exception.Message}");
        }
        catch (OperationCanceledException)
        {
            // 静默
        }
        catch (Exception exception)
        {
            ToastService.Error($"拉取失败：{exception.Message}");
        }
    }

    /// <summary>:cloud sync all — 枚举 novelsRoot 下全部本地小说逐一同步。</summary>
    private async Task RunNovelSyncAllAsync(bool pullOnly)
    {
        if (_novelSync is null)
        {
            ToastService.Warning("同步服务未启用");
            return;
        }
        if (_cloudSession.ConnectionState != CloudConnectionState.Connected)
        {
            ToastService.Warning("请先 :cloud login 登录云端");
            return;
        }
        try
        {
            var (novels, pushed, pulled, conflicts) = await _novelSync.SyncAllAsync(
                pullOnly,
                new CancellationTokenSource(TimeSpan.FromMinutes(10)).Token
            );
            if (novels == 0)
            {
                ToastService.Info("未发现本地小说");
                return;
            }
            ToastService.Info(
                $"全部同步完成：{novels} 本 · 上传 {pushed} · 下载 {pulled} · 冲突 {conflicts}"
            );
        }
        catch (CloudApiException exception)
        {
            ToastService.Error($"同步失败：{exception.Message}");
        }
        catch (OperationCanceledException)
        {
            // 静默
        }
        catch (Exception exception)
        {
            ToastService.Error($"同步失败：{exception.Message}");
        }
    }

    /// <summary>同步报告 Toast + 当前打开小说的内存态刷新（面板从磁盘重读）。</summary>
    private async Task ShowSyncReportAsync(SyncReport report)
    {
        ToastService.Info(
            $"同步完成：上传 {report.Pushed} · 下载 {report.Pulled} · 删除 {report.Deleted} · 冲突 {report.Conflicts}"
                + (report.Skipped > 0 ? $" · 跳过 {report.Skipped}" : "")
        );
        if (report.ConflictPaths.Count > 0)
        {
            foreach (var conflictPath in report.ConflictPaths.Take(3))
            {
                ToastService.Warning($"冲突副本：{conflictPath}");
            }
            if (report.ConflictPaths.Count > 3)
            {
                _logger?.LogInformation(
                    "Cloud sync conflicts ({Count}): {Paths}",
                    report.ConflictPaths.Count,
                    string.Join("; ", report.ConflictPaths)
                );
            }
        }
        // post-sync 刷新：设定面板 / 大纲从磁盘重读，保证内存态与文件系统一致
        var novelId = _agent.CurrentNovelId;
        if (!string.IsNullOrEmpty(novelId))
        {
            await _setUpPanel.RefreshAsync(novelId);
            await RefreshOutline();
        }
    }

    private async Task ShowCloudStatusAsync()
    {
        var state = _cloudSession.ConnectionState;
        var stateLabel = state switch
        {
            CloudConnectionState.Connected => "🌐 Connected",
            CloudConnectionState.Authenticating => "🔒 Not logged in",
            CloudConnectionState.RateLimited => "❗ Rate limited",
            CloudConnectionState.QuotaExceeded => "❗ Quota exceeded",
            CloudConnectionState.Unavailable => "❌ Offline",
            _ => "Disabled",
        };

        var info = _cloudSession.IsConfigured
            ? $"{stateLabel} | Key: {_cloudSession.PublicId} | Expires: {_cloudSession.ExpiresAt:yyyy-MM-dd}"
            : stateLabel;

        ToastService.Info($"Cloud: {info}");

        // 积分余量（零价查询）：未接线或未登录时静默跳过——连接态 Toast 已给出原因。
        if (_cloudUsage is null || !_cloudSession.IsConfigured)
            return;
        try
        {
            var usage = await _cloudUsage.GetUsageAsync(ct: CancellationToken.None);
            PostToUi(() =>
                ToastService.Info(
                    $"Cloud: 积分 {usage.Period} 已用 {usage.UsedUnits} / {usage.IncludedUnits}（剩余 {usage.RemainingUnits}）"
                )
            );
        }
        catch (Exception ex)
        {
            // 查询失败不打扰：连接态 Toast 已显示状态；此处仅留日志。
            _logger?.LogDebug(ex, "云端用量查询失败（:cloud status）");
        }
    }

    /// <summary>
    /// 云端登出 — 清除本地凭据，更新状态栏，停止心跳。
    /// </summary>
    private async Task CloudLogoutAsync()
    {
        _cloudSession.Clear();
        PostToUi(() => SetCloudState(CloudConnectionState.Disabled));
        if (_heartbeatCts is not null)
            await _heartbeatCts.CancelAsync();
        PostToUi(() => ToastService.Success("已登出 Mythology Cloud，API Key 已清除。"));
    }

    private void CloudLogout() => _ = CloudLogoutAsync();
}
