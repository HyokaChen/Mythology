using Core.Data;
using Core.Evolution;
using ReactiveUI;

namespace Desktop.ViewModels;

/// <summary>
/// 发布预览里的一条可勾选项（正文检测到的死亡 / 联动建议）。
/// 自动项（<see cref="IsAutomatic"/>）恒被应用，UI 不给取消（只有可选建议与检测死亡可勾选）。
/// </summary>
public sealed class PublishOptionItem : ViewModelBase
{
    public PublishOptionItem(
        string id,
        string label,
        string detail,
        bool isAutomatic,
        bool isSelected
    )
    {
        Id = id;
        Label = label;
        Detail = detail;
        IsAutomatic = isAutomatic;
        IsSelected = isSelected;
    }

    /// <summary>检测死亡候选 ID / 联动建议 ID（回传 <see cref="PublishOptions"/>）。</summary>
    public string Id { get; }

    /// <summary>主标题（角色名 / 目标实体名 + 字段）。</summary>
    public string Label { get; }

    /// <summary>说明（证据摘要 / 联动摘要）。</summary>
    public string Detail { get; }

    /// <summary>自动联动项：不可取消勾选。</summary>
    public bool IsAutomatic { get; }

    /// <summary>是否可勾选（自动项不可）。</summary>
    public bool CanToggle => !IsAutomatic;

    /// <summary>勾选状态（自动项恒为 true）。</summary>
    public bool IsSelected
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

/// <summary>
/// 章节发布预览窗口的视图模型：待生效变化 + 检测到的死亡（默认全选）+ 联动建议
/// （默认按 <see cref="CascadeSuggestion.DefaultSelected"/>，自动项不可取消）+ 预检提示。
/// 取消返回 null（保持草稿），确认返回 <see cref="PublishOptions"/>（只带勾选项）。
/// </summary>
public sealed class ChapterPublishViewModel : ViewModelBase
{
    /// <summary>名称解析（实体 ID → 显示名；缺省用 ID 原样）。</summary>
    private readonly Func<EntityType, string, string>? _nameOf;

    public ChapterPublishViewModel(
        PublishPreview preview,
        Func<EntityType, string, string>? nameOf = null
    )
    {
        _nameOf = nameOf;
        Preview = preview;

        Title = $"发布「{preview.ChapterTitle}」";
        Subtitle =
            preview.ChapterOrder > 0
                ? $"第 {preview.ChapterOrder} 章 · 草稿 → 发布"
                : "草稿 → 发布";
        PendingChangeLines = preview.PendingChanges.Select(DescribeChange).ToList();
        DetectedDeaths = preview
            .DetectedDeaths.Select(d => new PublishOptionItem(
                d.Id,
                d.CharacterName,
                string.IsNullOrWhiteSpace(d.Evidence) ? d.Summary : $"{d.Summary}（{d.Evidence}）",
                isAutomatic: false,
                isSelected: true
            ))
            .ToList();
        Suggestions = preview
            .Suggestions.Select(s => new PublishOptionItem(
                s.Id,
                $"{s.TargetName} · {s.Field}",
                s.Summary,
                s.IsAutomatic,
                s.IsAutomatic || s.DefaultSelected
            ))
            .ToList();
        Issues = preview
            .ValidationIssues.Select(i => (i.BlocksPublish ? "❌ " : "⚠️ ") + i.Message)
            .ToList();
    }

    /// <summary>原始预览（窗口用于显示章序 / 阻断判定）。</summary>
    public PublishPreview Preview { get; }

    /// <summary>窗口标题文案。</summary>
    public string Title { get; }

    /// <summary>副标题（章序 + 状态迁移）。</summary>
    public string Subtitle { get; }

    /// <summary>待生效变化的一行一条摘要。</summary>
    public IReadOnlyList<string> PendingChangeLines { get; }

    /// <summary>正文检测到的未登记死亡（默认全选）。</summary>
    public IReadOnlyList<PublishOptionItem> DetectedDeaths { get; }

    /// <summary>联动建议（自动项 + 默认选中项预勾选）。</summary>
    public IReadOnlyList<PublishOptionItem> Suggestions { get; }

    /// <summary>预检提示（❌ 阻断 / ⚠️ 警告）。</summary>
    public IReadOnlyList<string> Issues { get; }

    /// <summary>无待生效变化时的占位文案。</summary>
    public string PendingEmptyHint => "本章没有待生效变化（发布只切换章节状态）。";

    /// <summary>是否有待生效变化。</summary>
    public bool HasPendingChanges => PendingChangeLines.Count > 0;

    /// <summary>是否有检测到的死亡。</summary>
    public bool HasDetectedDeaths => DetectedDeaths.Count > 0;

    /// <summary>是否有联动建议。</summary>
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>是否有预检提示。</summary>
    public bool HasIssues => Issues.Count > 0;

    /// <summary>是否存在阻断发布的问题（有则「发布」按钮禁用）。</summary>
    public bool CanPublish => !Preview.HasBlockingIssues;

    /// <summary>选中项 → <see cref="PublishOptions"/>（空集合 = 用户全部取消，null 语义保留给「全部默认」）。</summary>
    public PublishOptions BuildOptions() =>
        new(
            DetectedDeaths.Where(o => o.IsSelected).Select(o => o.Id).ToList(),
            Suggestions.Where(o => o.IsSelected).Select(o => o.Id).ToList()
        );

    /// <summary>待生效变化摘要行。</summary>
    private string DescribeChange(EntityStateChange change)
    {
        var name = _nameOf?.Invoke(change.TargetEntityType, change.TargetId) ?? change.TargetId;
        var summary = string.IsNullOrWhiteSpace(change.Summary)
            ? $"{change.FromSummary ?? "?"} → {change.ToSummary ?? "?"}"
            : change.Summary;
        var effects = change.Effects.Count > 0 ? $"（{change.Effects.Count} 项结构化效果）" : "";
        return $"{change.Sequence}. {name} · {summary}{effects}";
    }
}
