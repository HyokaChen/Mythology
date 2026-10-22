using System.Reflection;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 关于窗口的键位/命令表必须与 Agent 实际派发的按键、命令实现保持一致——
/// 帮助窗是用户发现快捷键的唯一入口，条目漂移不会让任何测试变红。
/// </summary>
public sealed class AboutDialogEntriesTests
{
    [Fact]
    public void Shortcuts_Keys_AreUnique()
    {
        var keys = AboutDialog.Shortcuts.Select(e => e.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Commands_Keys_AreUnique()
    {
        var keys = AboutDialog.Commands.Select(e => e.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Entries_HaveNoBlankKeyOrDescription()
    {
        var all = AboutDialog.Shortcuts.Concat(AboutDialog.Commands).ToList();

        Assert.All(
            all,
            e =>
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Key), "条目缺键");
                Assert.False(string.IsNullOrWhiteSpace(e.Description), $"{e.Key} 缺说明");
            }
        );
    }

    /// <summary>
    /// 帮助窗列出的每个斜杠命令都必须真的能被派发——
    /// 对照 <c>ConversationViewModel.TryExecuteCommand</c> 的 case 标签
    /// （权威定义；AvailableCommands 只是补全候选，允许更短）。
    /// </summary>
    [Fact]
    public void ListedCommands_AreActuallyDispatched()
    {
        var dispatched = DispatchedCommandNames();
        var listed = AboutDialog
            .Commands.Select(e => e.Key)
            .Select(k => "/" + k.TrimStart('/', ':').Split(' ')[0])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // :cloud 的子命令由 RequestCloudColon 转发，switch 里只有 ":cloud" 本身
        var missing = listed
            .Where(c => !dispatched.Contains(c, StringComparer.Ordinal))
            .Where(c => c != ":cloud")
            .ToList();

        Assert.True(missing.Count == 0, $"帮助窗列了无法派发的命令：{string.Join(", ", missing)}");
    }

    /// <summary>
    /// 输入框的补全候选表里不得出现「敲了却提示未知命令」的条目——
    /// <c>AvailableCommands</c> 是用户唯一能看到的命令清单，写错就是产品缺陷。
    /// </summary>
    [Fact]
    public void AvailableCommands_AreAllDispatched()
    {
        var dispatched = DispatchedCommandNames();
        var table = typeof(Desktop.ViewModels.ConversationViewModel).GetField(
            "AvailableCommands",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(table);
        var entries = (string[])table.GetValue(null)!;

        var missing = entries
            .Where(e => e.StartsWith('/'))
            .Select(e => "/" + e[1..].Split(' ')[0])
            .Distinct(StringComparer.Ordinal)
            .Where(c => !dispatched.Contains(c, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"补全候选里有无法派发的命令（敲了会提示「未知命令」）：{string.Join(", ", missing)}"
        );
    }

    /// <summary>
    /// 反向：TryExecuteCommand 里的每个 case 都必须出现在补全候选里——
    /// 这条正是本轮修掉的缺口（/context /read /chapter /search 能派发却不给补全）。
    /// </summary>
    [Fact]
    public void EveryDispatchedCommand_IsSuggested()
    {
        var dispatched = DispatchedCommandNames();
        var table = typeof(Desktop.ViewModels.ConversationViewModel).GetField(
            "AvailableCommands",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(table);
        // 统一去掉参数与前缀：":cloud sync" → ":cloud"，"/theme dark" → "/theme"
        var suggested = ((string[])table.GetValue(null)!)
            .Select(e => (e.StartsWith(':') ? ":" : "/") + e.TrimStart('/', ':').Split(' ')[0])
            .ToHashSet(StringComparer.Ordinal);

        // 别名（/stats→/state）与转发入口（/cloud、:cloud 根命令）不必单独进补全
        var aliases = new HashSet<string>(["/stats", "/cloud", ":cloud"], StringComparer.Ordinal);
        var missing = dispatched
            .Where(c => !suggested.Contains(c) && !aliases.Contains(c))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"这些命令能派发但不出现在输入框补全里（用户无从发现）：{string.Join(", ", missing)}"
        );
    }

    /// <summary>从 TryExecuteCommand 的 IL 字符串字面量中提取所有 case 标签。</summary>
    private static HashSet<string> DispatchedCommandNames()
    {
        var method = typeof(Desktop.ViewModels.ConversationViewModel).GetMethod(
            "TryExecuteCommand",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return names;

        // 扫 ldstr 操作码(0x72) + 4 字节元数据 token
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x72)
                continue;
            var token = BitConverter.ToInt32(il, i + 1);
            try
            {
                if (
                    method.Module.ResolveString(token) is { } s
                    && (s.StartsWith('/') || s.StartsWith(':'))
                )
                    names.Add(s);
            }
            catch (ArgumentException)
            {
                // 非字符串元数据 token，跳过
            }
        }
        return names;
    }
}
