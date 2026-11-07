using Tui.Controls;
using Tui.Services;
using XenoAtom.Terminal.UI.Controls;

namespace Tui;

public sealed partial class TuiApp
{
    /// <summary>
    /// 处理补全请求的占位方法，当前返回默认值（未实现异步补全）。
    /// </summary>
    /// <param name="request">补全请求，包含光标位置和输入文本</param>
    /// <returns>补全结果，当前始终为 default</returns>
    private static PromptEditorCompletion HandleCompletion(
        in PromptEditorCompletionRequest request
    ) => default;

    /// <summary>
    /// 根据输入内容触发补全弹窗。支持三种补全模式：
    /// #实体引用（二级：类型→名称）、@文件路径、/tools 工具名。
    /// 实体提及一律走 #（@ 实体回退已删除，两端对齐）。
    /// 无匹配时关闭弹窗。
    /// </summary>
    /// <param name="input">当前输入框文本</param>
    private void TriggerCompletion(string input)
    {
        // # 实体引用补全——二级：先选类型再选名称
        var hashIndex = input.LastIndexOf('#');
        if (hashIndex >= 0)
        {
            var afterHash = input[(hashIndex + 1)..];
            // # 后无空格且长度 <80 才触发实体补全
            if (!afterHash.Contains(' ') && afterHash.Length < 80)
            {
                var colonIndex = afterHash.IndexOf(':');
                if (colonIndex >= 0)
                {
                    // 二级补全：#类型:前缀 → 按前缀过滤该类型实体名
                    var typeDisplay = afterHash[..colonIndex];
                    var entityPrefix = afterHash[(colonIndex + 1)..];
                    var typeKey = EntityDisplayHelper.ToEntityType(typeDisplay);

                    if (typeKey is not null)
                    {
                        var entities = _setUpPanel.GetEntityNamesByType(typeKey.Value);
                        var matches =
                            entityPrefix.Length > 0
                                ? entities
                                    .Where(n =>
                                        n.Contains(entityPrefix, StringComparison.OrdinalIgnoreCase)
                                    )
                                    .ToArray()
                                : entities;

                        if (matches.Length > 0)
                        {
                            _promptEditor.ShowCompletionPopup(
                                matches,
                                hashIndex + 1 + colonIndex + 1,
                                entityPrefix.Length
                            );
                            return;
                        }
                    }
                }
                else
                {
                    // 一级补全：#前缀 → 按前缀过滤实体类型显示名，补全后追加冒号
                    var matches =
                        afterHash.Length > 0
                            ? EntityDisplayHelper
                                .HashDisplayTypes.Where(t =>
                                    t.Contains(afterHash, StringComparison.OrdinalIgnoreCase)
                                )
                                .ToArray()
                            : EntityDisplayHelper.HashDisplayTypes;

                    if (matches.Length > 0)
                    {
                        _promptEditor.ShowCompletionPopup(
                            matches,
                            hashIndex + 1,
                            afterHash.Length,
                            ":"
                        );
                        return;
                    }
                }
            }
            _promptEditor.CloseMyPopup();
            return;
        }

        // @ 文件路径补全——目录导航（含空格路径不断词；B2 守卫修复）
        var atIndex = input.LastIndexOf('@');
        if (atIndex >= 0)
        {
            var afterAt = input[(atIndex + 1)..];
            // 长度上限防长文本误触发；空格合法（含空格目录名）
            if (afterAt.Length < 80)
            {
                var pathCandidates = Core.Shared.FilePathCompletion.GetEntries(
                    afterAt,
                    Directory.GetCurrentDirectory()
                );
                if (pathCandidates.Length > 0)
                {
                    _promptEditor.ShowCompletionPopup(
                        pathCandidates,
                        atIndex + 1,
                        afterAt.Length
                    );
                    return;
                }
            }
        }

        // /tools 二级补全：空格后补全插件 ID，冒号后补全工具名
        const string toolsPrefix = SlashCommandParser.Tools;
        if (input.StartsWith($"{toolsPrefix} "))
        {
            var afterPrefix = input[(toolsPrefix.Length + 1)..];
            var colonIdx = afterPrefix.IndexOf(':');

            if (colonIdx < 0)
            {
                // 已经选完插件（尾部空格）→ 等用户继续输入冒号/回车，不再弹同一份插件列表
                if (afterPrefix.Contains(' '))
                {
                    _promptEditor.CloseMyPopup();
                    return;
                }

                // /tools <部分插件ID> — 补全插件 kebab-case ID；选中后自动补冒号进入工具级
                var pluginIds = _registry.GetPluginIds();
                var idMatches = pluginIds
                    .Where(id => id.StartsWith(afterPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var candidates = idMatches.Length > 0 ? idMatches : pluginIds.ToArray();
                if (candidates.Length > 0)
                {
                    _promptEditor.ShowCompletionPopup(
                        candidates,
                        toolsPrefix.Length + 1,
                        afterPrefix.Length,
                        ":"
                    );
                    return;
                }
            }
            else
            {
                // /tools <插件ID>:<部分工具名> — 冒号后补全该插件的工具名
                var pluginId = afterPrefix[..colonIdx];
                var toolPrefix = afterPrefix[(colonIdx + 1)..];
                if (toolPrefix.Contains(' '))
                {
                    _promptEditor.CloseMyPopup();
                    return;
                }

                var actualPluginName = _registry.GetPluginNameById(pluginId);
                if (actualPluginName is not null)
                {
                    var tools = _registry.GetToolsByPlugin(actualPluginName);
                    var toolNames = tools.Select(t => t.Name).OrderBy(n => n).ToArray();
                    var matches = toolNames
                        .Where(n => n.StartsWith(toolPrefix, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var candidates = matches.Length > 0 ? matches : toolNames;
                    if (candidates.Length > 0)
                    {
                        _promptEditor.ShowCompletionPopup(
                            candidates,
                            toolsPrefix.Length + 1 + colonIdx + 1,
                            toolPrefix.Length
                        );
                        return;
                    }
                }
            }
            _promptEditor.CloseMyPopup();
            return;
        }

        // /theme 二级补全：toggle 或主题 id（选中后尾部空格关闭 popup）
        const string themePrefix = SlashCommandParser.Theme;
        if (input.StartsWith($"{themePrefix} "))
        {
            var afterPrefix = input[(themePrefix.Length + 1)..];
            if (!afterPrefix.Contains(' '))
            {
                var candidates = new[] { SlashCommandParser.ThemeToggle }
                    .Concat(ThemeRegistry.All.Select(t => t.Id))
                    .Where(c => c.StartsWith(afterPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (candidates.Length > 0)
                {
                    _promptEditor.ShowCompletionPopup(
                        candidates,
                        themePrefix.Length + 1,
                        afterPrefix.Length
                    );
                    return;
                }
            }
            _promptEditor.CloseMyPopup();
            return;
        }

        // 斜杠命令一级补全：只列命令本身（不把 <参数> 用法带进输入框）。
        // 选中命令后，下一次 update loop 会按新文本继续展开 /tools、/theme 等二级 popup。
        var matching = GetSlashCommands()
            .Select(c => c.Split(' ')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => c.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matching.Length > 0)
            _promptEditor.ShowCompletionPopup(matching, 0, input.Length);
        else
            _promptEditor.CloseMyPopup();
    }

}
