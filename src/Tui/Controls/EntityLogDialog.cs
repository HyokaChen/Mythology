using Core.Data;
using Core.Evolution;
using Tui.Services;
using XenoAtom.Terminal.UI.Controls;

namespace Tui.Controls;

/// <summary>
/// 实体时间线查看器：读 <see cref="EntityLogView"/>（状态变化 + 本体动作日志 + 旧字段审计只读合并），
/// 经 <see cref="EntityLogFormatter"/> 渲染成 Markdown 表格后走 <see cref="MarkdownDialog"/>。
///
/// 定位：所有「实体历史」入口（实体行 ⋯ → 历史、大纲行 → 时间线、世界规则卡片 → 历史）共用，
/// 唯一真源是本体日志；旧 <c>entity_audit.jsonl</c> 只在时间线里只读合并，不再新增写入。
/// </summary>
internal static class EntityLogDialog
{
    /// <summary>打开实体的时间线对话框（数据异步读取，UI 同步返回）。</summary>
    public static void Show(
        INovelStore store,
        string? novelId,
        EntityType entityType,
        string entityId
    )
    {
        if (string.IsNullOrEmpty(novelId) || string.IsNullOrEmpty(entityId))
        {
            ToastService.Warning("请先选择小说项目");
            return;
        }

        TuiSafeFire.SafeFire(
            async () =>
            {
                var entries = await EntityLogView.BuildForEntityAsync(
                    store,
                    novelId,
                    entityType,
                    entityId
                );
                var name = ResolveName(entityType, entityId, entries);
                var markdown = EntityLogFormatter.ToMarkdown(name, entries);
                MarkdownDialog.Show(
                    $"🧾 {EntityDisplayHelper.DisplayName(entityType)}时间线",
                    markdown,
                    width: 96,
                    minHeight: 20,
                    resizable: true
                );
            },
            "view entity log",
            errorMessage: "加载时间线失败"
        );
    }

    /// <summary>
    /// 标题用实体名：优先时间线里记录的实体名；没有记录（或记录名就是 id）时回退 id。
    /// 这样已删除实体的历史仍可打开（不因查不到实体而丢历史）。
    /// </summary>
    private static string ResolveName(
        EntityType entityType,
        string entityId,
        IReadOnlyList<EntityLogEntry> entries
    )
    {
        var recorded = entries
            .Select(e => e.EntityName)
            .FirstOrDefault(n =>
                !string.IsNullOrWhiteSpace(n)
                && !string.Equals(n, entityId, StringComparison.Ordinal)
            );
        return string.IsNullOrWhiteSpace(recorded) ? entityId : recorded;
    }
}
