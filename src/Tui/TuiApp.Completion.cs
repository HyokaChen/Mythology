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
    /// #实体引用（二级：类型→名称）、@文件路径/@实体提及、/tools 工具名。
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

        // @ 文件路径补全——目录导航
        // 检测：@ 后跟路径特征文本（含 / 或 .\ 或 . 或裸 @）
        var atIndex = input.LastIndexOf('@');
        if (atIndex >= 0)
        {
            var afterAt = input[(atIndex + 1)..];
            if (!afterAt.Contains(' ') && afterAt.Length < 80)
            {
                // 判断 @ 后是否为文件路径（空、/、./、~、含路径分隔符）
                var isFilePath =
                    afterAt.Length == 0
                    || afterAt.StartsWith('/')
                    || afterAt.StartsWith("./")
                    || afterAt.StartsWith('~')
                    || afterAt.Contains('/')
                    || afterAt.Contains('\\');

                if (isFilePath)
                {
                    var pathCandidates = GetFileCompletions(afterAt);
                    if (pathCandidates.Length > 0)
                    {
                        _promptEditor.ShowCompletionPopup(
                            pathCandidates,
                            atIndex + 1,
                            afterAt.Length
                        );
                        return;
                    }
                    // 文件无匹配时，回退到实体补全
                }

                // @ 实体提及补全：按前缀过滤所有实体名
                var entities = _promptEditor.EntityCompletions;
                if (entities.Count > 0)
                {
                    var matches =
                        afterAt.Length > 0
                            ? entities
                                .Where(n => n.Contains(afterAt, StringComparison.OrdinalIgnoreCase))
                                .ToArray()
                            : entities.ToArray();
                    if (matches.Length > 0)
                    {
                        _promptEditor.ShowCompletionPopup(matches, atIndex + 1, afterAt.Length);
                        return;
                    }
                }
            }
        }

        // /tools 二级补全：空格后补全插件 ID，冒号后补全工具名
        const string toolsPrefix = SlashCommandParser.Tools;
        if (input.StartsWith($"{toolsPrefix} ") && input.Length > toolsPrefix.Length + 1)
        {
            var afterPrefix = input[(toolsPrefix.Length + 1)..];
            var colonIdx = afterPrefix.IndexOf(':');

            if (colonIdx < 0)
            {
                // /tools <部分插件ID> — 空格后补全插件 kebab-case ID
                var pluginIds = _registry.GetPluginIds();
                var prefix = afterPrefix;

                var idMatches = pluginIds
                    .Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                var candidates = idMatches.Length > 0 ? idMatches : pluginIds.ToArray();
                _promptEditor.ShowCompletionPopup(
                    candidates,
                    toolsPrefix.Length + 1,
                    afterPrefix.Length
                );
            }
            else
            {
                // /tools <插件ID>:<部分工具名> — 冒号后补全该插件的工具名
                var pluginId = afterPrefix[..colonIdx];
                var toolPrefix = afterPrefix[(colonIdx + 1)..];

                var actualPluginName = _registry.GetPluginNameById(pluginId);
                if (actualPluginName is not null)
                {
                    var tools = _registry.GetToolsByPlugin(actualPluginName);
                    var toolNames = tools.Select(t => t.Name).OrderBy(n => n).ToArray();
                    var matches = toolNames
                        .Where(n => n.StartsWith(toolPrefix, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var candidates = matches.Length > 0 ? matches : toolNames;
                    _promptEditor.ShowCompletionPopup(
                        candidates,
                        7 + colonIdx + 1,
                        toolPrefix.Length
                    );
                }
            }
        }

        // 斜杠命令补全：按前缀过滤已注册命令
        var matching = GetSlashCommands()
            .Where(c => c.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matching.Length > 0)
            _promptEditor.ShowCompletionPopup(matching, 0, input.Length);
        else
            _promptEditor.CloseMyPopup();
    }

    /// <summary>
    /// 从 SetUpPanel 收集所有实体名称，更新输入框的 @ 实体补全列表。
    /// </summary>
    private void UpdateEntityCompletions()
    {
        try
        {
            var names = _setUpPanel.GetAllEntityNames();
            _promptEditor.SetEntityCompletions(names);
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "update entity completions");
        }
    }

    /// <summary>
    /// 根据 @ 后的部分文件路径，返回目录条目用于补全。
    /// 层级规则：空路径 → 当前目录列表；/path/ → 该目录下条目；./rel → CWD/rel 下条目。
    /// 目录名后加 /；文件名保持原样。
    /// </summary>
    private string[] GetFileCompletions(string partialPath)
    {
        try
        {
            string dir;
            string prefix;

            if (string.IsNullOrEmpty(partialPath))
            {
                // 裸 @ → 显示当前工作目录条目
                dir = Directory.GetCurrentDirectory();
                prefix = "";
            }
            else if (partialPath.StartsWith("~/"))
            {
                // ~/ 路径：展开用户主目录
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var rest = partialPath[2..];
                var slashIdx = rest.LastIndexOf('/');
                if (slashIdx < 0)
                {
                    dir = home;
                    prefix = rest;
                }
                else
                {
                    dir = Path.Combine(home, rest[..slashIdx]);
                    prefix = rest[(slashIdx + 1)..];
                }
            }
            else if (partialPath.StartsWith('/'))
            {
                // 绝对路径：最后一个 / 之前为目录，之后为前缀
                var slashIdx = partialPath.LastIndexOf('/');
                dir = partialPath[..(slashIdx == 0 ? 1 : slashIdx)];
                prefix = partialPath[(slashIdx + 1)..];
            }
            else if (partialPath.StartsWith("./"))
            {
                // ./ 相对路径：基于当前工作目录
                var rest = partialPath[2..];
                var slashIdx = rest.LastIndexOf('/');
                if (slashIdx < 0)
                {
                    dir = Directory.GetCurrentDirectory();
                    prefix = rest;
                }
                else
                {
                    dir = Path.Combine(Directory.GetCurrentDirectory(), rest[..slashIdx]);
                    prefix = rest[(slashIdx + 1)..];
                }
            }
            else
            {
                // 无 ./ 前缀的相对路径
                var slashIdx = partialPath.LastIndexOf('/');
                if (slashIdx < 0)
                {
                    dir = Directory.GetCurrentDirectory();
                    prefix = partialPath;
                }
                else
                {
                    dir = Path.Combine(Directory.GetCurrentDirectory(), partialPath[..slashIdx]);
                    prefix = partialPath[(slashIdx + 1)..];
                }
            }

            if (!Directory.Exists(dir))
                return [];

            var entries = Directory
                .GetFileSystemEntries(dir)
                .Select(p =>
                {
                    var isDir = Directory.Exists(p);
                    var name = Path.GetFileName(p);
                    return isDir ? name + "/" : name; // 目录名后加 / 标识
                })
                .Where(n =>
                    string.IsNullOrEmpty(prefix)
                    || n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || n[..^1].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ) // 目录名含尾 /，需去掉再匹配前缀
                .OrderBy(n => n)
                .Take(20) // 限制最多 20 条，避免弹窗过长
                .ToArray();

            return entries;
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "get file completions");
            return [];
        }
    }
}
