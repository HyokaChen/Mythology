using Core.Data;
using Core.Evolution;
using Tui.Controls;
using Tui.Services;
using XenoAtom.Terminal.UI.Controls;
using ChapterEditScope = Tui.Controls.ChapterEditScope;

namespace Tui;

public sealed partial class TuiApp
{
    /// <summary>
    /// 用户手动编辑标记（区分 AI 工具调用和用户 UI 操作）。
    /// 当为 true 时，<see cref="OnStoreEntityChanged"/> 会向 Agent 发送系统通知（Steer），
    /// 让 LLM 知道用户手动修改了实体，而不是 AI 自己做的。
    /// </summary>
    private bool _manualEdit;

    /// <summary>
    /// CRUD 事件退订器列表。Dispose 时逐一调用以防止内存泄漏。
    /// </summary>
    private readonly List<Action> _crudUnsubscribers = [];

    /// <summary>
    /// 绑定 SetUpPanel 的所有 CRUD 事件（Add/Edit/Delete）到 TuiApp 的处理逻辑。
    /// 每种实体类型（Character、Faction、Location 等）分别绑定三个事件。
    /// 编辑/删除操作需要先选中实体（id != null），否则提示用户展开节点。
    /// </summary>
    private void WireSidePanelCrud()
    {
        WireAdd(
            EntityType.Character,
            h => _setUpPanel.OnAddCharacter += h,
            h => _setUpPanel.OnAddCharacter -= h
        );
        WireEdit(
            EntityType.Character,
            "角色",
            h => _setUpPanel.OnEditCharacter += h,
            h => _setUpPanel.OnEditCharacter -= h
        );
        WireDelete(
            EntityType.Character,
            "角色",
            h => _setUpPanel.OnDeleteCharacter += h,
            h => _setUpPanel.OnDeleteCharacter -= h
        );

        WireAdd(
            EntityType.Faction,
            h => _setUpPanel.OnAddFaction += h,
            h => _setUpPanel.OnAddFaction -= h
        );
        WireEdit(
            EntityType.Faction,
            "组织",
            h => _setUpPanel.OnEditFaction += h,
            h => _setUpPanel.OnEditFaction -= h
        );
        WireDelete(
            EntityType.Faction,
            "组织",
            h => _setUpPanel.OnDeleteFaction += h,
            h => _setUpPanel.OnDeleteFaction -= h
        );

        WireAdd(
            EntityType.Location,
            h => _setUpPanel.OnAddLocation += h,
            h => _setUpPanel.OnAddLocation -= h
        );
        WireEdit(
            EntityType.Location,
            "地点",
            h => _setUpPanel.OnEditLocation += h,
            h => _setUpPanel.OnEditLocation -= h
        );
        WireDelete(
            EntityType.Location,
            "地点",
            h => _setUpPanel.OnDeleteLocation += h,
            h => _setUpPanel.OnDeleteLocation -= h
        );

        WireAdd(
            EntityType.Plotline,
            h => _setUpPanel.OnAddPlotline += h,
            h => _setUpPanel.OnAddPlotline -= h
        );
        WireEdit(
            EntityType.Plotline,
            "情节线",
            h => _setUpPanel.OnEditPlotline += h,
            h => _setUpPanel.OnEditPlotline -= h
        );
        WireDelete(
            EntityType.Plotline,
            "情节线",
            h => _setUpPanel.OnDeletePlotline += h,
            h => _setUpPanel.OnDeletePlotline -= h
        );

        WireAdd(
            EntityType.Scene,
            h => _setUpPanel.OnAddScene += h,
            h => _setUpPanel.OnAddScene -= h
        );
        WireEdit(
            EntityType.Scene,
            "场景",
            h => _setUpPanel.OnEditScene += h,
            h => _setUpPanel.OnEditScene -= h
        );
        WireDelete(
            EntityType.Scene,
            "场景",
            h => _setUpPanel.OnDeleteScene += h,
            h => _setUpPanel.OnDeleteScene -= h
        );

        WireAdd(EntityType.Item, h => _setUpPanel.OnAddItem += h, h => _setUpPanel.OnAddItem -= h);
        WireEdit(
            EntityType.Item,
            "道具",
            h => _setUpPanel.OnEditItem += h,
            h => _setUpPanel.OnEditItem -= h
        );
        WireDelete(
            EntityType.Item,
            "道具",
            h => _setUpPanel.OnDeleteItem += h,
            h => _setUpPanel.OnDeleteItem -= h
        );

        WireAdd(
            EntityType.Relationship,
            h => _setUpPanel.OnAddRelationship += h,
            h => _setUpPanel.OnAddRelationship -= h
        );
        WireEdit(
            EntityType.Relationship,
            "关系",
            h => _setUpPanel.OnEditRelationship += h,
            h => _setUpPanel.OnEditRelationship -= h
        );
        WireDeleteRelationship();

        // 章节细纲编辑（作用域化表单：标题/序号/状态/所属卷 + 细纲规划，不含正文）
        Action<string?, string?> editChapterHandler = (novelId, chapterId) =>
        {
            if (string.IsNullOrEmpty(novelId) || string.IsNullOrEmpty(chapterId))
                return;
            _manualEdit = true;
            new EntityEditDialog(
                _store,
                EntityType.Chapter,
                entityId: chapterId,
                novelId: novelId,
                editScope: ChapterEditScope.Outline
            ).Show();
        };
        _setUpPanel.OnEditChapter += editChapterHandler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnEditChapter -= editChapterHandler);

        // 章节内容编辑（作用域化表单：只留正文；标题等字段保持原值）
        Action<string?, string?> editChapterContentHandler = (novelId, chapterId) =>
        {
            if (string.IsNullOrEmpty(novelId) || string.IsNullOrEmpty(chapterId))
                return;
            _manualEdit = true;
            new EntityEditDialog(
                _store,
                EntityType.Chapter,
                entityId: chapterId,
                novelId: novelId,
                editScope: ChapterEditScope.Content
            ).Show();
        };
        _setUpPanel.OnEditChapterContent += editChapterContentHandler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnEditChapterContent -= editChapterContentHandler);

        // 分卷（大纲二层）：快速新建（只填卷名）/ 编辑 / 重命名 / 删除
        Action<string?, string?> addVolumeHandler = (nid, _) =>
        {
            var novelId = nid ?? _novelId;
            if (string.IsNullOrEmpty(novelId))
            {
                ToastService.Warning("请先选择小说项目");
                return;
            }
            TextInputDialog.Show(
                "📦 新建分卷",
                "",
                "卷名（可留空，只记卷序）",
                name =>
                    TuiSafeFire.SafeFire(
                        () => CreateVolumeAsync(novelId, name),
                        "create volume",
                        _logger,
                        "新建分卷失败"
                    )
            );
        };
        _setUpPanel.OnAddVolume += addVolumeHandler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnAddVolume -= addVolumeHandler);

        WireEdit(
            EntityType.Volume,
            "分卷",
            h => _setUpPanel.OnEditVolume += h,
            h => _setUpPanel.OnEditVolume -= h
        );
        WireDelete(
            EntityType.Volume,
            "分卷",
            h => _setUpPanel.OnDeleteVolume += h,
            h => _setUpPanel.OnDeleteVolume -= h
        );

        // 小说信息入口（大纲页「📖 小说信息」）：小说实体编辑
        Action<string?, string?> editNovelHandler = (novelId, _) =>
        {
            if (string.IsNullOrEmpty(novelId))
            {
                ToastService.Warning("请先选择小说项目");
                return;
            }
            _manualEdit = true;
            new EntityEditDialog(
                _store,
                EntityType.Novel,
                entityId: novelId,
                novelId: novelId
            ).Show();
        };
        _setUpPanel.OnEditNovel += editNovelHandler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnEditNovel -= editNovelHandler);

        // 书级删除：确认弹窗后删除整本小说（含全部卷/章/实体），再重载面板
        // 注意：第二个参数必须起真名（不能叫 _）——否则函数体内的 `_ = ...`
        // fire-and-forget 丢弃赋值会变成给它赋值，直接编译不过（CS0029）。
        // 同文件 readChapterHandler 用 novelIdIgnored，同一惯例。
        Action<string?, string?> deleteNovelHandler = (deletedNovelId, novelIdIgnored) =>
        {
            if (string.IsNullOrEmpty(deletedNovelId))
            {
                ToastService.Warning("请先选择小说项目");
                return;
            }
            _ = DeleteNovelWithConfirmationAsync(deletedNovelId);
        };
        _setUpPanel.OnDeleteNovel += deleteNovelHandler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnDeleteNovel -= deleteNovelHandler);

        // 章节结构操作（大纲 Tab 内直接管理）：新建 / 删除 / 换序 / 移动到卷
        Action<string?, string?> addChapterHandler = (nid, volumeId) =>
        {
            var novelId2 = nid ?? _novelId;
            if (string.IsNullOrEmpty(novelId2))
            {
                ToastService.Warning("请先选择小说项目");
                return;
            }
            TuiSafeFire.SafeFire(
                () => OpenChapterCreateDialogAsync(novelId2, volumeId),
                "create chapter",
                _logger,
                "新建章节失败"
            );
        };
        _setUpPanel.OnAddChapter += addChapterHandler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnAddChapter -= addChapterHandler);

        Action<string?, string?> deleteChapterHandler = (nid, id) =>
        {
            if (string.IsNullOrEmpty(nid) || string.IsNullOrEmpty(id))
                return;
            _ = DeleteWithConfirmationAsync(EntityType.Chapter, id);
        };
        _setUpPanel.OnDeleteChapter += deleteChapterHandler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnDeleteChapter -= deleteChapterHandler);

        // 章节生命周期（发布/撤回）：状态是发布事务的结果——草稿 → 已发布走 PublishAsync，
        // 已发布 → 草稿走 UnpublishAsync；两条都先弹预览/确认对话框，取消则状态不变。
        Action<string> publishChapterHandler = chapterId =>
            TuiSafeFire.SafeFire(
                () => PublishChapterAsync(chapterId),
                "publish chapter",
                _logger,
                "发布章节失败"
            );
        _setUpPanel.OnPublishChapter += publishChapterHandler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnPublishChapter -= publishChapterHandler);

        Action<string> unpublishChapterHandler = chapterId =>
            TuiSafeFire.SafeFire(
                () => UnpublishChapterAsync(chapterId),
                "unpublish chapter",
                _logger,
                "撤回章节失败"
            );
        _setUpPanel.OnUnpublishChapter += unpublishChapterHandler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnUnpublishChapter -= unpublishChapterHandler);
    }

    /// <summary>章节落盘（FileNovelStore 需手动写入作用域，跳过 AI 冲突门禁）。</summary>
    private async Task SaveChapterAsync(Chapter chapter)
    {
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        if (_store is FileNovelStore fns)
        {
            using (fns.BeginManualWrite())
                await _store.SaveChapter(chapter);
        }
        else
        {
            await _store.SaveChapter(chapter);
        }
    }

    /// <summary>分卷落盘（同 <see cref="SaveChapterAsync"/>）。</summary>
    private async Task SaveVolumeAsync(Volume volume)
    {
        using var _ = Core.Evolution.OntologyWriteContext.BeginUser();
        if (_store is FileNovelStore fns)
        {
            using (fns.BeginManualWrite())
                await _store.SaveVolume(volume);
        }
        else
        {
            await _store.SaveVolume(volume);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 章节生命周期（发布 / 撤回）— 唯一事务边界是 ChapterLifecycleService
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>当前小说 id（宿主 _novelId 优先，回退面板刷新时记录的值）。</summary>
    private string? CurrentLifecycleNovelId =>
        !string.IsNullOrEmpty(_novelId) ? _novelId : _setUpPanel.CurrentNovelId;

    /// <summary>
    /// 发布草稿章：预览（待生效变化/未登记死亡/联动）→ 用户确认 → <c>PublishAsync</c>。
    /// 取消预览不改变任何状态。
    /// </summary>
    private async Task PublishChapterAsync(string chapterId)
    {
        var novelId = CurrentLifecycleNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            ToastService.Warning("请先选择小说项目");
            return;
        }

        var service = new ChapterLifecycleService(_store);
        var preview = await service.PreviewPublishAsync(novelId, chapterId);
        var resolve = await BuildEntityNameResolverAsync(novelId);
        var options = await ChapterPublishDialog.ShowAsync(
            preview,
            resolve,
            id => resolve(EntityType.Location, id),
            id => resolve(EntityType.Plotline, id)
        );
        if (options is null)
            return; // 取消：章节仍是草稿

        var result = await service.PublishAsync(novelId, chapterId, options);
        if (!result.Success)
        {
            ToastService.Error($"发布失败: {result.Error}");
            return;
        }

        _manualEdit = true;
        await RefreshOutline();
        ToastService.Info(
            $"已发布第{preview.ChapterOrder}章「{preview.ChapterTitle}」"
                + (
                    result.AppliedSuggestionCount > 0
                        ? $"（应用联动 {result.AppliedSuggestionCount} 条）"
                        : ""
                )
        );
        foreach (var warning in result.Warnings)
            ToastService.Warning(warning);
    }

    /// <summary>
    /// 撤回已发布章：预览（后续章/实体冲突）→ 用户确认 → <c>UnpublishAsync</c>（变更集精确回滚）。
    /// 被阻断或取消时不改动任何状态。
    /// </summary>
    private async Task UnpublishChapterAsync(string chapterId)
    {
        var novelId = CurrentLifecycleNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            ToastService.Warning("请先选择小说项目");
            return;
        }

        var service = new ChapterLifecycleService(_store);
        var preview = await service.PreviewUnpublishAsync(novelId, chapterId);
        var chapter = await _store.GetChapter(novelId, chapterId);
        var title = chapter is null
            ? $"章节 {chapterId}"
            : $"第{chapter.Order}章「{chapter.Title}」";

        if (!await ChapterUnpublishDialog.ShowAsync(title, preview))
            return; // 取消 / 被阻断

        var result = await service.UnpublishAsync(novelId, chapterId);
        if (!result.Success)
        {
            ToastService.Error($"撤回失败: {result.Error}");
            return;
        }

        _manualEdit = true;
        await RefreshOutline();
        ToastService.Info($"已撤回 {title}（回滚 {result.RevertedEntries} 条）");
        foreach (var warning in result.Warnings)
            ToastService.Warning(warning);
    }

    /// <summary>
    /// 实体 id → 显示名解析器（发布预览用；跨类型一张表，缺项回退 id，不隐藏信息）。
    /// </summary>
    private async Task<Func<EntityType, string, string>> BuildEntityNameResolverAsync(
        string novelId
    )
    {
        var names = new Dictionary<(EntityType, string), string>();
        foreach (var character in await _store.GetCharacters(novelId))
            names[(EntityType.Character, character.Id)] = character.Name;
        foreach (var faction in await _store.GetFactions(novelId))
            names[(EntityType.Faction, faction.Id)] = faction.Name;
        foreach (var location in await _store.GetLocations(novelId))
            names[(EntityType.Location, location.Id)] = location.Name;
        foreach (var item in await _store.GetItems(novelId))
            names[(EntityType.Item, item.Id)] = item.Name;
        foreach (var plotline in await _store.GetPlotlines(novelId))
            names[(EntityType.Plotline, plotline.Id)] = plotline.Name;
        foreach (var scene in await _store.GetScenesByNovel(novelId))
            names[(EntityType.Scene, scene.Id)] = scene.Title;
        foreach (var chapter in await _store.GetChapters(novelId))
            names[(EntityType.Chapter, chapter.Id)] = chapter.Title;
        foreach (var volume in await _store.GetVolumes(novelId))
            names[(EntityType.Volume, volume.Id)] = string.IsNullOrWhiteSpace(volume.Name)
                ? $"第{volume.Order}卷"
                : volume.Name;
        foreach (var rule in await _store.GetWorldRules(novelId))
            names[(EntityType.WorldRule, rule.Id)] = string.IsNullOrWhiteSpace(rule.Description)
                ? rule.Id
                : rule.Description;
        if (await _store.GetNovel(novelId) is { } novel)
            names[(EntityType.Novel, novel.Id)] = novel.Title;

        return (type, id) =>
            names.TryGetValue((type, id), out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : id;
    }

    /// <summary>新建分卷（快速创建：只填卷名；卷纲/目标等重字段走完整编辑器）。</summary>
    private async Task CreateVolumeAsync(string novelId, string name)
    {
        var volume = new Volume
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            NovelId = novelId,
            Name = name,
            Order = ChapterOrdering.NextVolumeOrder(await _store.GetVolumes(novelId)),
        };
        _manualEdit = true;
        await SaveVolumeAsync(volume);
        await RefreshOutline();
        _setUpPanel.RevealVolume(volume.Id);
        ToastService.Info(
            string.IsNullOrEmpty(volume.Name)
                ? $"已新建第 {volume.Order} 卷"
                : $"已新建第 {volume.Order} 卷：{volume.Name}"
        );
    }

    /// <summary>弹「新建章节」快速对话框（标题 + 所属卷，预选调用行所在卷）。</summary>
    private async Task OpenChapterCreateDialogAsync(
        string novelId,
        string? presetVolumeId,
        string? afterChapterId = null
    )
    {
        var volumes = ChapterOrdering.SortVolumes(await _store.GetVolumes(novelId));
        ChapterCreateDialog.Show(
            volumes,
            presetVolumeId,
            (title, volumeId) =>
                TuiSafeFire.SafeFire(
                    () => CreateChapterAsync(novelId, title, volumeId, afterChapterId),
                    "create chapter",
                    _logger,
                    "新建章节失败"
                )
        );
    }

    /// <summary>新建章节（快速创建：标题 + 所属卷；插入到 <paramref name="afterChapterId"/> 之后或该卷末尾）。</summary>
    private async Task CreateChapterAsync(
        string novelId,
        string? title,
        string? volumeId,
        string? afterChapterId = null
    )
    {
        var chapters = await _store.GetChapters(novelId);
        var plan = ChapterOrdering.PlanInsert(chapters, afterChapterId, volumeId);
        var order = plan.Order;

        // ③ 该处无可用空号 → 先全章重排腾出位置（新章号由哨兵行预留）。
        if (plan.RequiresReorder)
        {
            var assignments = ChapterOrdering.PlanFullReorder(
                chapters,
                afterChapterId,
                ChapterOrdering.GroupKey(volumeId)
            );
            order = assignments.First(a => a.ChapterId == ChapterOrdering.InsertSlotId).Order;
            await _store.ReorderAllChapters(novelId, assignments);
        }

        var chapter = new Chapter
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            NovelId = novelId,
            Title = string.IsNullOrWhiteSpace(title) ? "新章节" : title.Trim(),
            Order = order,
            Status = ChapterStatus.Draft,
            VolumeId = string.IsNullOrEmpty(volumeId) ? null : volumeId,
        };
        _manualEdit = true;
        await SaveChapterAsync(chapter);
        await RefreshOutline();
        _setUpPanel.RevealChapter(chapter.Id);
        ToastService.Info($"已新建「{chapter.Title}」");
    }

    /// <summary>
    /// 绑定“添加实体”事件：打开 EntityEditDialog 新建模式。
    /// </summary>
    private void WireAdd(
        EntityType entityType,
        Action<Action<string?, string?>> subscribe,
        Action<Action<string?, string?>> unsubscribe
    )
    {
        Action<string?, string?> handler = (novelId, _) =>
        {
            _manualEdit = true;
            new EntityEditDialog(_store, entityType, novelId: novelId).Show();
        };
        subscribe(handler);
        _crudUnsubscribers.Add(() => unsubscribe(handler));
    }

    /// <summary>
    /// 绑定“编辑实体”事件：打开 EntityEditDialog 编辑模式。id 为 null 时提示用户先展开节点。
    /// </summary>
    private void WireEdit(
        EntityType entityType,
        string label,
        Action<Action<string?, string?>> subscribe,
        Action<Action<string?, string?>> unsubscribe
    )
    {
        Action<string?, string?> handler = (novelId, id) =>
        {
            if (id is null)
            {
                ToastService.Warning($"请先展开一个{label}");
                return;
            }
            _manualEdit = true;
            new EntityEditDialog(_store, entityType, entityId: id, novelId: novelId).Show();
        };
        subscribe(handler);
        _crudUnsubscribers.Add(() => unsubscribe(handler));
    }

    /// <summary>
    /// 删除整本小说：ConfirmDeleteDialog 确认（名字走存储里的书名）→ DeleteNovel → 刷新面板。
    /// 面板空态由 _setUpPanel.RefreshAsync 处理（novelId provider 返回被删 id 时结构为空）。
    /// </summary>
    private async Task DeleteNovelWithConfirmationAsync(string novelId)
    {
        var name = (await _store.GetNovel(novelId))?.Title ?? novelId;
        ConfirmDeleteDialog.Show(
            EntityType.Novel,
            name,
            async () =>
            {
                _manualEdit = true;
                await _store.DeleteNovel(novelId);
                // 与 InjectNovelContextAsync 的空态同一口径：提示无已加载小说，不直接建
                _agent.SetNovelContext(
                    "⚡ 当前没有加载任何小说项目。如果用户想创建新小说，先用 ask_user 确认，不要直接调用 create_novel。"
                );
                await RefreshOutline();
                ToastService.Info($"已删除小说「{name}」");
            }
        );
    }

    /// <summary>
    /// 绑定“删除实体”事件：弹出 ConfirmDeleteDialog 确认后删除。id 为 null 时提示用户先展开节点。
    /// </summary>
    private void WireDelete(
        EntityType entityType,
        string label,
        Action<Action<string?, string?>> subscribe,
        Action<Action<string?, string?>> unsubscribe
    )
    {
        Action<string?, string?> handler = (a, id) =>
        {
            if (id is null)
            {
                ToastService.Warning($"请先展开一个{label}");
                return;
            }
            _ = DeleteWithConfirmationAsync(entityType, id);
        };
        subscribe(handler);
        _crudUnsubscribers.Add(() => unsubscribe(handler));
    }

    /// <summary>
    /// 绑定“删除关系”事件：关系显示名由关联角色名组成（如“张三 ↔ 李四”）。
    /// </summary>
    private void WireDeleteRelationship()
    {
        Action<string?, string?> handler = async (_, id) =>
        {
            if (id is null)
            {
                ToastService.Warning("请先展开一个关系");
                return;
            }
            var rel = await _store.GetRelationship(id);
            string displayName;
            if (rel is not null)
            {
                var chars = await _store.GetCharacters(rel.NovelId);
                var charMap = chars.ToDictionary(c => c.Id, c => c.Name);
                var nameA = charMap.GetValueOrDefault(rel.CharacterAId, rel.CharacterAId);
                var nameB = charMap.GetValueOrDefault(rel.CharacterBId, rel.CharacterBId);
                displayName = $"{nameA} ↔ {nameB}";
            }
            else
            {
                displayName = id;
            }
            ConfirmDeleteDialog.Show(
                EntityType.Relationship,
                displayName,
                async () =>
                {
                    _manualEdit = true;
                    await _store.DeleteRelationship(id);
                    await RefreshOutline();
                }
            );
        };
        _setUpPanel.OnDeleteRelationship += handler;
        _crudUnsubscribers.Add(() => _setUpPanel.OnDeleteRelationship -= handler);
    }

    /// <summary>
    /// 弹出确认删除对话框，确认后执行删除并刷新大纲。
    /// 章节删除走 <see cref="ChapterLifecycleService.DeleteChapterAsync"/>（已发布章先撤回，
    /// 变更集精确回滚后才删文件），失败只提示、不留半删状态。
    /// </summary>
    private async Task DeleteWithConfirmationAsync(EntityType entityType, string id)
    {
        var name = await GetEntityDisplayNameAsync(entityType, id);
        ConfirmDeleteDialog.Show(
            entityType,
            name ?? id,
            () =>
                TuiSafeFire.SafeFire(
                    async () =>
                    {
                        _manualEdit = true;
                        await DeleteEntityAsync(entityType, id);
                        await RefreshOutline();
                    },
                    "delete entity",
                    _logger,
                    $"{EntityDisplayHelper.DisplayName(entityType)}删除失败"
                )
        );
    }

    /// <summary>
    /// 获取实体的显示名称（用于确认对话框标题）。
    /// 优先使用 novelId 限定范围的重载以避免 O(N×M) 全局扫描。
    /// </summary>
    private async Task<string?> GetEntityDisplayNameAsync(EntityType entityType, string id)
    {
        // Use novelId-scoped overloads when available to avoid O(N×M) global scans
        var novelId = _novelId;
        return entityType switch
        {
            EntityType.Character when novelId is not null => (
                await _store.GetCharacter(novelId, id)
            )?.Name,
            EntityType.Character => (await _store.GetCharacter(id))?.Name,
            EntityType.Faction when novelId is not null => (
                await _store.GetFaction(novelId, id)
            )?.Name,
            EntityType.Faction => (await _store.GetFaction(id))?.Name,
            EntityType.Location when novelId is not null => (
                await _store.GetLocation(novelId, id)
            )?.Name,
            EntityType.Location => (await _store.GetLocation(id))?.Name,
            EntityType.Plotline when novelId is not null => (
                await _store.GetPlotline(novelId, id)
            )?.Name,
            EntityType.Plotline => (await _store.GetPlotline(id))?.Name,
            EntityType.Scene when novelId is not null => (
                await _store.GetScene(novelId, id)
            )?.Title,
            EntityType.Scene => (await _store.GetScene(id))?.Title,
            EntityType.Item when novelId is not null => (await _store.GetItem(novelId, id))?.Name,
            EntityType.Item => (await _store.GetItem(id))?.Name,
            EntityType.Volume when novelId is not null => (
                await _store.GetVolume(novelId, id)
            )?.Name,
            EntityType.Volume => (await _store.GetVolume(id))?.Name,
            EntityType.Chapter when novelId is not null => (await _store.GetChapter(novelId, id))
                is { } chapter
                ? $"{chapter.Order}. {chapter.Title}"
                : null,
            EntityType.Chapter => id,
            _ => id,
        };
    }

    /// <summary>
    /// 根据实体类型调用对应的存储删除方法。
    /// 章节是唯一例外：状态与变更集由发布事务管理，删除必须经
    /// <see cref="ChapterLifecycleService.DeleteChapterAsync"/>（已发布章先撤回再删）。
    /// </summary>
    private async Task DeleteEntityAsync(EntityType entityType, string id)
    {
        if (entityType == EntityType.Chapter)
        {
            var novelId = CurrentLifecycleNovelId;
            if (string.IsNullOrEmpty(novelId))
            {
                ToastService.Warning("请先选择小说项目");
                return;
            }
            var result = await new ChapterLifecycleService(_store).DeleteChapterAsync(novelId, id);
            if (!result.Success)
                throw new InvalidOperationException(result.Error ?? "删除章节失败");
            return;
        }

        await (
            entityType switch
            {
                EntityType.Character => _store.DeleteCharacter(id),
                EntityType.Faction => _store.DeleteFaction(id),
                EntityType.Location => _store.DeleteLocation(id),
                EntityType.Plotline => _store.DeletePlotline(id),
                EntityType.Scene => _store.DeleteScene(id),
                EntityType.Item => _store.DeleteItem(id),
                EntityType.Relationship => _store.DeleteRelationship(id),
                // 分卷删除走 novelId 重载：级联清空章节 VolumeId（避免悬空引用）
                EntityType.Volume when _novelId is not null => _store.DeleteVolume(_novelId, id),
                EntityType.Volume => _store.DeleteVolume(id),
                _ => Task.CompletedTask,
            }
        );
    }
}
