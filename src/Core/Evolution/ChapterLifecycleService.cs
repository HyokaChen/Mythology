using Core.Data;

namespace Core.Evolution;

/// <summary>
/// 章节生命周期服务：发布/撤回的唯一事务边界。
/// 草稿章的状态变化为 Pending；发布时统一应用结构化效果与联动并生成变更集；
/// 撤回时按变更集精确回滚；已发布章的补记走 <see cref="ApplyAmendmentAsync"/>。
/// </summary>
public sealed class ChapterLifecycleService(INovelStore store)
{
    // ═══════════════════════════════════════════════════════════
    //  发布预览
    // ═══════════════════════════════════════════════════════════

    public async Task<PublishPreview> PreviewPublishAsync(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        var novel = await store.GetNovel(novelId, ct);
        var chapter = await store.GetChapter(novelId, chapterId, ct);
        if (novel is null || chapter is null)
        {
            return new PublishPreview(
                novelId,
                chapterId,
                0,
                "",
                [],
                [],
                [],
                [new ValidationIssue("missing", "小说或章节不存在", true)]
            );
        }

        var issues = new List<ValidationIssue>();
        if (chapter.Status != ChapterStatus.Draft)
        {
            issues.Add(new ValidationIssue("not-draft", "只有草稿章节可以发布", true));
        }

        var all = await store.GetEntityStateChangesByChapter(novelId, chapterId, ct);
        var pending = all.Where(c => c.Lifecycle == ChangeLifecycle.Pending).ToList();

        // 章内顺序：必须为正且不重复
        var duplicates = pending
            .GroupBy(c => c.Sequence)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            issues.Add(
                new ValidationIssue(
                    "sequence-duplicate",
                    $"章内顺序重复：{string.Join("、", duplicates)}；请修正 sequence 后再发布",
                    true
                )
            );
        }
        if (pending.Any(c => c.Sequence <= 0))
        {
            issues.Add(
                new ValidationIssue("sequence-invalid", "存在 sequence 未填写的待发布变化", true)
            );
        }

        // 效果白名单 + 目标存在 + ID 引用存在
        var chapters = await store.GetChapters(novelId, ct);
        var scenes = await store.GetScenesByNovel(novelId, ct);
        foreach (var change in pending)
        {
            if (!string.Equals(change.ChapterId, chapterId, StringComparison.Ordinal))
            {
                issues.Add(
                    new ValidationIssue(
                        "chapter-mismatch",
                        $"变化 {change.Id} 不属于本章（chapter-id={change.ChapterId}）",
                        true
                    )
                );
            }
            foreach (var effect in change.Effects)
            {
                var error = StateEffectValidator.Validate(effect, novel);
                if (error is not null)
                {
                    issues.Add(
                        new ValidationIssue("effect-invalid", $"{change.Summary}：{error}", true)
                    );
                    continue;
                }

                var target = await LoadEntityAsync(novelId, effect.TargetType, effect.TargetId, ct);
                if (target is null)
                {
                    issues.Add(
                        new ValidationIssue(
                            "target-missing",
                            $"{change.Summary}：目标 {effect.TargetType.ToKebabCaseString()} {effect.TargetId} 不存在",
                            true
                        )
                    );
                    continue;
                }

                var referenceError = await ValidateReferenceAsync(
                    novelId,
                    novel,
                    chapters,
                    effect,
                    ct
                );
                if (referenceError is not null)
                {
                    issues.Add(
                        new ValidationIssue(
                            "reference-missing",
                            $"{change.Summary}：{referenceError}",
                            true
                        )
                    );
                }

                // 人工已改过目标字段 → 警告（不阻止）
                var normalized = StateEffectValidator.Normalize(effect);
                if (
                    !string.IsNullOrEmpty(normalized.From)
                    && normalized.Operation == StateEffectOperation.Set
                )
                {
                    var current = ReadFieldValue(target, normalized.Field);
                    if (
                        current is not null
                        && !string.Equals(
                            current,
                            normalized.From,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        issues.Add(
                            new ValidationIssue(
                                "manual-modified",
                                $"「{DisplayName(target)}」的 {normalized.Field} 已被人工改为 {current}（变化记录期望 {normalized.From}）；发布将覆盖该人工修改",
                                false
                            )
                        );
                    }
                }
            }

            if (
                change.SceneId is not null
                && !scenes.Any(s => string.Equals(s.Id, change.SceneId, StringComparison.Ordinal))
            )
            {
                issues.Add(
                    new ValidationIssue(
                        "scene-missing",
                        $"{change.Summary}：场景 {change.SceneId} 不存在",
                        true
                    )
                );
            }
            if (
                change.LocationId is not null
                && !novel.Locations.Any(l =>
                    string.Equals(l.Id, change.LocationId, StringComparison.Ordinal)
                )
            )
            {
                issues.Add(
                    new ValidationIssue(
                        "location-missing",
                        $"{change.Summary}：地点 {change.LocationId} 不存在",
                        true
                    )
                );
            }
            foreach (var plotlineId in change.PlotlineIds)
            {
                if (
                    !novel.Plotlines.Any(p =>
                        string.Equals(p.Id, plotlineId, StringComparison.Ordinal)
                    )
                )
                {
                    issues.Add(
                        new ValidationIssue(
                            "plotline-missing",
                            $"{change.Summary}：情节线 {plotlineId} 不存在",
                            true
                        )
                    );
                }
            }
        }

        var detected = DeathCandidateDetector.Detect(novel, chapter, all);
        var sequenceBase = pending.Count == 0 ? 0 : pending.Max(c => c.Sequence);
        var planChanges = pending
            .Concat(
                detected.Select((d, index) => DetectedToChange(chapter, d, sequenceBase + index))
            )
            .ToList();
        var suggestions = CascadePlanner.Plan(novel, chapter, planChanges, scenes, chapters);

        return new PublishPreview(
            novelId,
            chapter.Id,
            chapter.Order,
            chapter.Title,
            pending,
            detected,
            suggestions,
            issues
        );
    }

    private async Task<string?> ValidateReferenceAsync(
        string novelId,
        Novel novel,
        IReadOnlyList<Chapter> chapters,
        StateEffect effect,
        CancellationToken ct
    )
    {
        var normalized = StateEffectValidator.Normalize(effect);
        if (
            normalized.Operation is StateEffectOperation.Clear or StateEffectOperation.Remove
            || string.IsNullOrWhiteSpace(normalized.Value)
        )
            return null;

        bool Exists(Func<string, bool> predicate) => predicate(normalized.Value!);

        return normalized.Field switch
        {
            "faction-id" => Exists(id => novel.Factions.Any(f => f.Id == id))
                ? null
                : $"组织 {normalized.Value} 不存在",
            "current-location-id" or "location-id" or "parent-id" or "headquarters-id" => Exists(
                id => novel.Locations.Any(l => l.Id == id)
            )
                ? null
                : $"地点 {normalized.Value} 不存在",
            "owner-id" => Exists(id =>
                novel.Characters.Any(c => c.Id == id) || novel.Factions.Any(f => f.Id == id)
            )
                ? null
                : $"持有者 {normalized.Value} 不存在",
            "planned-reveal-chapter-id"
            or "resolved-at-chapter-id"
            or "since-chapter-id"
            or "until-chapter-id" => Exists(id => chapters.Any(c => c.Id == id))
                ? null
                : $"章节 {normalized.Value} 不存在",
            _ => null,
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  发布
    // ═══════════════════════════════════════════════════════════

    public async Task<PublishResult> PublishAsync(
        string novelId,
        string chapterId,
        PublishOptions options,
        CancellationToken ct = default
    )
    {
        var preview = await PreviewPublishAsync(novelId, chapterId, ct);
        if (preview.HasBlockingIssues)
        {
            return Failure(
                string.Join(
                    "；",
                    preview
                        .ValidationIssues.Where(i => i.BlocksPublish)
                        .Take(5)
                        .Select(i => i.Message)
                )
            );
        }

        var novel = await store.GetNovel(novelId, ct);
        var chapter = await store.GetChapter(novelId, chapterId, ct);
        if (novel is null || chapter is null)
            return Failure("小说或章节不存在");

        var warnings = preview
            .ValidationIssues.Where(i => !i.BlocksPublish)
            .Select(i => i.Message)
            .ToList();

        var selectedDetected = options.SelectedDetectedDeathIds;
        var detected = preview
            .DetectedDeaths.Where(d => selectedDetected is null || selectedDetected.Contains(d.Id))
            .ToList();

        var selectedSuggestionIds = options.SelectedSuggestionIds;
        var suggestions = preview
            .Suggestions.Where(s =>
                s.IsAutomatic
                || (
                    selectedSuggestionIds is null
                        ? s.DefaultSelected
                        : selectedSuggestionIds.Contains(s.Id)
                )
            )
            .ToList();

        var pendingSequenceBase =
            preview.PendingChanges.Count == 0 ? 0 : preview.PendingChanges.Max(c => c.Sequence);
        var detectedChanges = detected
            .Select((d, index) => DetectedToChange(chapter, d, pendingSequenceBase + index))
            .ToList();

        var result = await ApplyAsync(
            novelId,
            novel,
            chapter,
            preview.PendingChanges,
            detectedChanges,
            suggestions,
            publishChapter: true,
            warnings,
            ct
        );
        return result;
    }

    public async Task<PublishResult> ApplyAmendmentAsync(
        string novelId,
        string chapterId,
        string stateChangeId,
        CancellationToken ct = default
    )
    {
        var novel = await store.GetNovel(novelId, ct);
        var chapter = await store.GetChapter(novelId, chapterId, ct);
        if (novel is null || chapter is null)
            return Failure("小说或章节不存在");
        if (chapter.Status != ChapterStatus.Published)
            return Failure("补记只适用于已发布章节；草稿章节请直接发布");

        var all = await store.GetEntityStateChangesByChapter(novelId, chapterId, ct);
        var target = all.FirstOrDefault(c =>
            string.Equals(c.Id, stateChangeId, StringComparison.Ordinal)
        );
        if (target is null)
            return Failure("变化记录不存在");
        if (target.Lifecycle != ChangeLifecycle.Pending)
            return Failure("已生效变化不可重复应用；请新增一条修正变化");

        // 单条预检
        foreach (var effect in target.Effects)
        {
            var error = StateEffectValidator.Validate(effect, novel);
            if (error is not null)
                return Failure(error);
            if (await LoadEntityAsync(novelId, effect.TargetType, effect.TargetId, ct) is null)
                return Failure(
                    $"目标 {effect.TargetType.ToKebabCaseString()} {effect.TargetId} 不存在"
                );
        }

        var scenes = await store.GetScenesByNovel(novelId, ct);
        var chapters = await store.GetChapters(novelId, ct);
        var suggestions = CascadePlanner
            .Plan(novel, chapter, [target], scenes, chapters)
            .Where(s => s.IsAutomatic || s.DefaultSelected)
            .ToList();

        return await ApplyAsync(
            novelId,
            novel,
            chapter,
            [target],
            [],
            suggestions,
            publishChapter: false,
            [],
            ct
        );
    }

    private async Task<PublishResult> ApplyAsync(
        string novelId,
        Novel novel,
        Chapter chapter,
        IReadOnlyList<EntityStateChange> pending,
        IReadOnlyList<EntityStateChange> detectedChanges,
        IReadOnlyList<CascadeSuggestion> suggestions,
        bool publishChapter,
        IReadOnlyList<string> warnings,
        CancellationToken ct
    )
    {
        var set = new ChapterChangeSet
        {
            NovelId = novelId,
            ChapterId = chapter.Id,
            ChapterOrder = chapter.Order,
            Status = ChangeSetStatus.Applying,
            AppliedAt = DateTime.UtcNow,
        };

        var allRecords = await store.GetEntityStateChangesByChapter(novelId, chapter.Id, ct);
        var recordById = allRecords.ToDictionary(c => c.Id, StringComparer.Ordinal);

        // 1. 状态变化条目（Before = Pending 快照）
        foreach (var change in pending.Concat(detectedChanges))
        {
            set.Entries.Add(
                new ChangeSetEntry
                {
                    Kind = ChangeSetEntryKind.StateChange,
                    EntityType = change.TargetEntityType,
                    EntityId = change.TargetId,
                    EntityName = change.Summary,
                    BeforeJson =
                        change.Lifecycle == ChangeLifecycle.Pending
                            ? EntityJson.SerializeStateChange(change)
                            : null,
                    StateChangeId = change.Id,
                    Cascade = change.IsCascade,
                    Summary = change.Summary,
                }
            );
        }

        // 2. 受影响实体快照（效果目标 + 联动目标）
        var affected = new HashSet<(EntityType Type, string Id)>();
        foreach (var effect in pending.Concat(detectedChanges).SelectMany(c => c.Effects))
            affected.Add((effect.TargetType, effect.TargetId));
        foreach (var suggestion in suggestions)
        {
            if (!suggestion.IsCreate && !string.IsNullOrWhiteSpace(suggestion.TargetId))
                affected.Add((suggestion.TargetType, suggestion.TargetId));
        }

        foreach (var (type, id) in affected.ToList())
        {
            var entity = await LoadEntityAsync(novelId, type, id, ct);
            if (entity is null)
                continue;
            set.Entries.Add(
                new ChangeSetEntry
                {
                    Kind = ChangeSetEntryKind.EntityUpdate,
                    EntityType = type,
                    EntityId = id,
                    EntityName = DisplayName(entity),
                    BeforeJson = EntityJson.Serialize(entity),
                    Cascade = false,
                    Summary = $"更新{EntityFieldRegistry.Label(type)}「{DisplayName(entity)}」",
                }
            );
        }

        // 3. 创建条目（纪念地）
        var createSuggestions = suggestions.Where(s => s.IsCreate).ToList();
        var createdLocations = new List<(CascadeSuggestion Suggestion, Location Location)>();
        foreach (var suggestion in createSuggestions)
        {
            var location = new Location
            {
                NovelId = novelId,
                Name = suggestion.Value ?? suggestion.TargetName,
                Description = suggestion.Summary,
            };
            var entry = new ChangeSetEntry
            {
                Kind = ChangeSetEntryKind.EntityCreate,
                EntityType = EntityType.Location,
                EntityId = location.Id,
                EntityName = location.Name,
                Cascade = true,
                Summary = suggestion.Summary,
            };
            set.Entries.Add(entry);
            createdLocations.Add((suggestion, location));
        }

        await store.SaveChapterChangeSet(novelId, set, ct);

        var now = DateTime.UtcNow;
        var applyWarnings = new List<string>();
        try
        {
            using (OntologyWriteContext.BeginForChangeSet(set, chapter))
            using (OntologyWriteContext.SuppressChapterLifecycle())
            using (OntologyWriteContext.SuppressManualStateChange())
            {
                // 状态变化：Pending → Committed（一次写回整章分片）
                var merged = allRecords.ToList();
                foreach (var change in pending.Concat(detectedChanges))
                {
                    if (recordById.TryGetValue(change.Id, out var existing))
                    {
                        existing.Lifecycle = ChangeLifecycle.Committed;
                        existing.CommittedAt = now;
                        existing.ChangeSetId = set.Id;
                    }
                    else
                    {
                        change.Lifecycle = ChangeLifecycle.Committed;
                        change.CommittedAt = now;
                        change.ChangeSetId = set.Id;
                        merged.Add(change);
                    }
                }
                await store.SaveEntityStateChanges(novelId, chapter.Id, merged, ct);

                // 应用效果（按章内顺序；联动在后）
                var effects = BuildEffects(pending, detectedChanges, suggestions);
                var applied = await StateEffectApplier.ApplyAllAsync(
                    store,
                    novel,
                    chapter,
                    effects,
                    ct,
                    onBeforeModify: (type, entity) =>
                    {
                        // 联动/一致性规则会顺带修改未声明的实体（如角色换组织时的新旧组织）：
                        // 必须补进变更集，否则撤回无法精确回滚。
                        if (!affected.Add((type, entity.Id)))
                            return;
                        set.Entries.Add(
                            new ChangeSetEntry
                            {
                                Kind = ChangeSetEntryKind.EntityUpdate,
                                EntityType = type,
                                EntityId = entity.Id,
                                EntityName = DisplayName(entity),
                                BeforeJson = EntityJson.Serialize(entity),
                                Cascade = true,
                                Summary =
                                    $"更新{EntityFieldRegistry.Label(type)}「{DisplayName(entity)}」",
                            }
                        );
                    }
                );
                applyWarnings.AddRange(applied.Warnings);

                // 创建纪念地（保存）
                foreach (var (_, location) in createdLocations)
                {
                    await store.SaveLocation(location, ct);
                }

                if (publishChapter)
                {
                    chapter.Status = ChapterStatus.Published;
                    await store.SaveChapter(chapter, ct);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await TryRollbackAsync(novelId, chapter, set, publishChapter, ct);
            set.Status = ChangeSetStatus.Failed;
            await store.SaveChapterChangeSet(novelId, set, ct);
            return Failure($"发布失败，已回滚：{exception.Message}");
        }

        // 4. 回填 AfterJson
        var refreshed = await store.GetEntityStateChangesByChapter(novelId, chapter.Id, ct);
        var refreshedById = refreshed.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var createdEntityCount = 0;
        foreach (var entry in set.Entries)
        {
            switch (entry.Kind)
            {
                case ChangeSetEntryKind.StateChange:
                    if (
                        entry.StateChangeId is not null
                        && refreshedById.TryGetValue(entry.StateChangeId, out var record)
                    )
                        entry.AfterJson = EntityJson.StateChangeFingerprint(record);
                    break;
                case ChangeSetEntryKind.EntityUpdate:
                    if (
                        await LoadEntityAsync(novelId, entry.EntityType, entry.EntityId, ct) is
                        { } entity
                    )
                        entry.AfterJson = EntityJson.Fingerprint(entity);
                    break;
                case ChangeSetEntryKind.EntityCreate:
                    if (
                        await LoadEntityAsync(novelId, entry.EntityType, entry.EntityId, ct) is
                        { } created
                    )
                    {
                        entry.AfterJson = EntityJson.Fingerprint(created);
                        createdEntityCount++;
                    }
                    break;
            }
        }

        set.Status = ChangeSetStatus.Applied;
        set.AppliedAt = now;
        await store.SaveChapterChangeSet(novelId, set, ct);

        warnings = warnings.Concat(applyWarnings).ToList();
        return new PublishResult(
            true,
            set.Id,
            suggestions.Count,
            createdEntityCount,
            warnings,
            null
        );
    }

    private static List<StateEffect> BuildEffects(
        IReadOnlyList<EntityStateChange> pending,
        IReadOnlyList<EntityStateChange> detected,
        IReadOnlyList<CascadeSuggestion> suggestions
    )
    {
        var effects = new List<StateEffect>();
        foreach (
            var change in pending.Concat(detected).OrderBy(c => c.Sequence).ThenBy(c => c.CreatedAt)
        )
        {
            foreach (var effect in change.Effects)
            {
                effects.Add(
                    new StateEffect
                    {
                        TargetType = effect.TargetType,
                        TargetId = effect.TargetId,
                        Field = effect.Field,
                        Operation = effect.Operation,
                        Value = effect.Value,
                        From = effect.From,
                        Cascade = change.IsCascade,
                        SourceChangeId = change.Id,
                    }
                );
            }
        }
        foreach (var suggestion in suggestions.Where(s => !s.IsCreate))
        {
            effects.Add(
                new StateEffect
                {
                    TargetType = suggestion.TargetType,
                    TargetId = suggestion.TargetId,
                    Field = suggestion.Field,
                    Operation = suggestion.Operation,
                    Value = suggestion.Value,
                    Cascade = true,
                    SourceChangeId = suggestion.SourceChangeId,
                }
            );
        }
        return effects;
    }

    private async Task TryRollbackAsync(
        string novelId,
        Chapter chapter,
        ChapterChangeSet set,
        bool restoreDraft,
        CancellationToken ct
    )
    {
        using (OntologyWriteContext.SuppressJournal())
        using (OntologyWriteContext.SuppressChapterLifecycle())
        using (OntologyWriteContext.SuppressPublishedReferenceGuard())
        using (OntologyWriteContext.SuppressManualStateChange())
        {
            foreach (var entry in Enumerable.Reverse(set.Entries))
            {
                try
                {
                    switch (entry.Kind)
                    {
                        case ChangeSetEntryKind.EntityCreate:
                            await EntitySaveDispatcher.DeleteAsync(
                                store,
                                novelId,
                                entry.EntityType,
                                entry.EntityId,
                                ct
                            );
                            break;
                        case ChangeSetEntryKind.EntityUpdate:
                            if (
                                EntityJson.Deserialize(entry.EntityType, entry.BeforeJson ?? "") is
                                { } entity
                            )
                                await EntitySaveDispatcher.SaveAsync(store, entity, ct);
                            break;
                        case ChangeSetEntryKind.StateChange:
                        {
                            var records = await store.GetEntityStateChangesByChapter(
                                novelId,
                                chapter.Id,
                                ct
                            );
                            if (
                                EntityJson.DeserializeStateChange(entry.BeforeJson ?? "") is
                                { } record
                            )
                            {
                                records.RemoveAll(r => r.Id == record.Id);
                                records.Add(record);
                            }
                            else if (entry.StateChangeId is { Length: > 0 } createdId)
                            {
                                // 本次事务内新建的记录（正文检测死亡）：回滚即删除，否则会留下
                                // 「草稿章 + 已生效变化」的幽灵状态，且没有其它入口可清理。
                                records.RemoveAll(r => r.Id == createdId);
                            }
                            await store.SaveEntityStateChanges(novelId, chapter.Id, records, ct);
                            break;
                        }
                    }
                }
                catch
                {
                    // 回滚尽力而为：单个条目失败不阻断其余条目
                }
            }
            if (restoreDraft)
            {
                chapter.Status = ChapterStatus.Draft;
                await store.SaveChapter(chapter, ct);
            }
        }
    }

    private static EntityStateChange DetectedToChange(
        Chapter chapter,
        DetectedDeathCandidate candidate,
        int sequenceOffset
    ) =>
        new()
        {
            Id = candidate.Id,
            NovelId = chapter.NovelId,
            TargetEntityType = EntityType.Character,
            TargetId = candidate.CharacterId,
            ChapterId = chapter.Id,
            Aspect = EvolutionAspect.Status,
            FromSummary = "active",
            ToSummary = "deceased",
            EvidenceQuote = candidate.Evidence,
            Summary = candidate.Summary,
            Source = AuditSource.System,
            Effects =
            [
                new StateEffect
                {
                    TargetType = EntityType.Character,
                    TargetId = candidate.CharacterId,
                    Field = "status",
                    Operation = StateEffectOperation.Set,
                    Value = "deceased",
                    From = "active",
                },
            ],
            Sequence = sequenceOffset + 1,
            Lifecycle = ChangeLifecycle.Committed,
            CommittedAt = DateTime.UtcNow,
        };

    // ═══════════════════════════════════════════════════════════
    //  撤回
    // ═══════════════════════════════════════════════════════════

    public async Task<UnpublishPreview> PreviewUnpublishAsync(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        var chapter = await store.GetChapter(novelId, chapterId, ct);
        if (chapter is null)
        {
            return new UnpublishPreview(
                false,
                [],
                [new ChangeConflict(EntityType.Chapter, chapterId, "章节不存在")]
            );
        }

        var chapters = await store.GetChapters(novelId, ct);
        var later = chapters
            .Where(c =>
                c.Status == ChapterStatus.Published
                && c.Order > chapter.Order
                && !string.Equals(c.Id, chapter.Id, StringComparison.Ordinal)
            )
            .OrderBy(c => c.Order)
            .Select(c => c.Title)
            .ToList();

        var sets = (await store.GetChapterChangeSets(novelId, chapterId, ct))
            .Where(s => s.Status == ChangeSetStatus.Applied)
            .OrderByDescending(s => s.AppliedAt)
            .ToList();

        if (sets.Count == 0)
            return new UnpublishPreview(false, later, []);

        var conflicts = new List<ChangeConflict>();
        var records = await store.GetEntityStateChangesByChapter(novelId, chapterId, ct);
        var recordById = records.ToDictionary(c => c.Id, StringComparer.Ordinal);

        // 撤回按 AppliedAt 降序执行：同一实体只有「最新一个变更集」的 AfterJson 能与当前态匹配；
        // 更早变更集里的快照在它还未来得及回滚前必然不匹配，不能据此判冲突。
        var checkedEntities = new HashSet<(EntityType Type, string Id)>();

        foreach (var set in sets)
        {
            foreach (var entry in set.Entries)
            {
                if (
                    entry.Kind is ChangeSetEntryKind.EntityUpdate or ChangeSetEntryKind.EntityCreate
                    && !checkedEntities.Add((entry.EntityType, entry.EntityId))
                )
                {
                    continue;
                }

                switch (entry.Kind)
                {
                    case ChangeSetEntryKind.StateChange:
                        if (
                            entry.StateChangeId is null
                            || !recordById.TryGetValue(entry.StateChangeId, out var current)
                        )
                        {
                            conflicts.Add(
                                new ChangeConflict(
                                    entry.EntityType,
                                    entry.EntityId,
                                    $"状态变化 {entry.StateChangeId} 不存在"
                                )
                            );
                        }
                        else if (
                            entry.AfterJson is not null
                            && !string.Equals(
                                EntityJson.StateChangeFingerprint(current),
                                entry.AfterJson,
                                StringComparison.Ordinal
                            )
                        )
                        {
                            conflicts.Add(
                                new ChangeConflict(
                                    entry.EntityType,
                                    entry.EntityId,
                                    $"状态变化 {entry.StateChangeId} 已被修改"
                                )
                            );
                        }
                        break;
                    case ChangeSetEntryKind.EntityUpdate:
                    {
                        var entity = await LoadEntityAsync(
                            novelId,
                            entry.EntityType,
                            entry.EntityId,
                            ct
                        );
                        if (entity is null)
                        {
                            conflicts.Add(
                                new ChangeConflict(entry.EntityType, entry.EntityId, "实体不存在")
                            );
                        }
                        else if (
                            entry.AfterJson is not null
                            && !string.Equals(
                                EntityJson.Fingerprint(entity),
                                entry.AfterJson,
                                StringComparison.Ordinal
                            )
                        )
                        {
                            conflicts.Add(
                                new ChangeConflict(
                                    entry.EntityType,
                                    entry.EntityId,
                                    $"「{entry.EntityName}」发布后被修改"
                                )
                            );
                        }
                        break;
                    }
                    case ChangeSetEntryKind.EntityCreate:
                    {
                        var entity = await LoadEntityAsync(
                            novelId,
                            entry.EntityType,
                            entry.EntityId,
                            ct
                        );
                        if (entity is null)
                        {
                            conflicts.Add(
                                new ChangeConflict(
                                    entry.EntityType,
                                    entry.EntityId,
                                    "创建实体已被删除"
                                )
                            );
                        }
                        else if (
                            entry.AfterJson is not null
                            && !string.Equals(
                                EntityJson.Fingerprint(entity),
                                entry.AfterJson,
                                StringComparison.Ordinal
                            )
                        )
                        {
                            conflicts.Add(
                                new ChangeConflict(
                                    entry.EntityType,
                                    entry.EntityId,
                                    $"「{entry.EntityName}」发布后被修改"
                                )
                            );
                        }
                        break;
                    }
                }
            }
        }

        return new UnpublishPreview(sets.Count > 0, later, conflicts);
    }

    public async Task<UnpublishResult> UnpublishAsync(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        var chapter = await store.GetChapter(novelId, chapterId, ct);
        if (chapter is null)
            return new UnpublishResult(false, 0, [], "章节不存在");
        if (chapter.Status != ChapterStatus.Published)
            return new UnpublishResult(false, 0, [], "只有已发布章节可以撤回");

        var preview = await PreviewUnpublishAsync(novelId, chapterId, ct);
        if (preview.LaterPublishedChapters.Count > 0)
        {
            return new UnpublishResult(
                false,
                0,
                [],
                $"后续章节已发布（{string.Join("、", preview.LaterPublishedChapters.Take(5))}），请从后往前撤回"
            );
        }
        if (preview.Conflicts.Count > 0)
        {
            return new UnpublishResult(
                false,
                0,
                [],
                $"实体在发布后被修改：{string.Join("；", preview.Conflicts.Take(5).Select(c => c.Reason))}"
            );
        }

        var warnings = new List<string>();
        var sets = (await store.GetChapterChangeSets(novelId, chapterId, ct))
            .Where(s => s.Status == ChangeSetStatus.Applied)
            .OrderByDescending(s => s.AppliedAt)
            .ToList();

        if (sets.Count == 0)
        {
            using (OntologyWriteContext.SuppressChapterLifecycle())
            {
                chapter.Status = ChapterStatus.Draft;
                await store.SaveChapter(chapter, ct);
            }
            warnings.Add("该章在变更集机制上线前发布，未回滚实体");
            return new UnpublishResult(true, 0, warnings, null);
        }

        var revertedEntries = 0;
        var records = await store.GetEntityStateChangesByChapter(novelId, chapterId, ct);

        using (OntologyWriteContext.SuppressJournal())
        using (OntologyWriteContext.SuppressChapterLifecycle())
        using (OntologyWriteContext.SuppressPublishedReferenceGuard())
        using (OntologyWriteContext.SuppressManualStateChange())
        {
            foreach (var set in sets)
            {
                foreach (var entry in Enumerable.Reverse(set.Entries))
                {
                    ct.ThrowIfCancellationRequested();
                    switch (entry.Kind)
                    {
                        case ChangeSetEntryKind.EntityCreate:
                            await EntitySaveDispatcher.DeleteAsync(
                                store,
                                novelId,
                                entry.EntityType,
                                entry.EntityId,
                                ct
                            );
                            break;
                        case ChangeSetEntryKind.EntityUpdate:
                            if (
                                EntityJson.Deserialize(entry.EntityType, entry.BeforeJson ?? "") is
                                { } entity
                            )
                                await EntitySaveDispatcher.SaveAsync(store, entity, ct);
                            break;
                        case ChangeSetEntryKind.StateChange:
                            if (
                                EntityJson.DeserializeStateChange(entry.BeforeJson ?? "") is
                                { } record
                            )
                            {
                                records.RemoveAll(r => r.Id == record.Id);
                                records.Add(record);
                            }
                            break;
                    }
                    revertedEntries++;
                }
            }

            // 本章全部变化回到待发布（含此前变更集留下的 Committed 记录）
            foreach (var record in records)
            {
                record.Lifecycle = ChangeLifecycle.Pending;
                record.CommittedAt = null;
                record.ChangeSetId = null;
            }
            await store.SaveEntityStateChanges(novelId, chapterId, records, ct);

            chapter.Status = ChapterStatus.Draft;
            await store.SaveChapter(chapter, ct);
        }

        foreach (var set in sets)
        {
            await store.AppendOntologyActions(
                novelId,
                [
                    new OntologyAction
                    {
                        NovelId = novelId,
                        Element = OntologyElement.Action,
                        Operation = OntologyOperation.Revert,
                        TargetEntityType = EntityType.Chapter,
                        TargetId = chapter.Id,
                        TargetName = chapter.Title,
                        Summary = $"回撤第{chapter.Order}章发布：{set.Entries.Count} 项变更",
                        ChapterId = chapter.Id,
                        ChangeSetId = set.Id,
                        Source = AuditSource.System,
                    },
                ],
                ct
            );
            set.Status = ChangeSetStatus.Reverted;
            set.RevertedAt = DateTime.UtcNow;
            await store.SaveChapterChangeSet(novelId, set, ct);
        }

        return new UnpublishResult(true, revertedEntries, warnings, null);
    }

    public async Task<UnpublishResult> DeleteChapterAsync(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        var chapter = await store.GetChapter(novelId, chapterId, ct);
        if (chapter is null)
            return new UnpublishResult(true, 0, [], null);

        if (chapter.Status == ChapterStatus.Published)
        {
            var unpublish = await UnpublishAsync(novelId, chapterId, ct);
            if (!unpublish.Success)
                return unpublish;
        }

        await store.DeleteChapter(novelId, chapterId, ct);
        return new UnpublishResult(true, 0, [], null);
    }

    // ═══════════════════════════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════════════════════════

    private async Task<Entity?> LoadEntityAsync(
        string novelId,
        EntityType type,
        string id,
        CancellationToken ct
    ) =>
        type switch
        {
            EntityType.Character => await store.GetCharacter(novelId, id, ct),
            EntityType.Faction => await store.GetFaction(novelId, id, ct),
            EntityType.Location => await store.GetLocation(novelId, id, ct),
            EntityType.Item => await store.GetItem(novelId, id, ct),
            EntityType.Plotline => await store.GetPlotline(novelId, id, ct),
            EntityType.Relationship => await store.GetRelationship(novelId, id, ct),
            EntityType.Scene => await store.GetScene(novelId, id, ct),
            EntityType.Chapter => await store.GetChapter(novelId, id, ct),
            _ => null,
        };

    private static string DisplayName(Entity entity) =>
        entity switch
        {
            Character c => c.Name,
            Faction f => f.Name,
            Location l => l.Name,
            Item i => i.Name,
            Plotline p => p.Name,
            SceneDefinition s => s.Title,
            Chapter ch => ch.Title,
            _ => entity.Id,
        };

    /// <summary>当前字段值（预检「人工已修改」告警用；不支持返回 null）。</summary>
    private static string? ReadFieldValue(Entity entity, string field) =>
        entity switch
        {
            Character c => field switch
            {
                "status" => c.Status.ToString().ToLowerInvariant(),
                "faction-id" => c.FactionId,
                "current-location-id" => c.CurrentLocationId,
                "role" => c.Role.ToString().ToLowerInvariant(),
                _ => null,
            },
            Faction f => field switch
            {
                "status" => f.Status.ToString().ToLowerInvariant(),
                "leader" => f.Leader,
                "headquarters-id" => f.HeadquartersId,
                "type" => f.Type.ToString().ToLowerInvariant(),
                _ => null,
            },
            Item i => field switch
            {
                "status" => i.Status.ToString().ToLowerInvariant(),
                "owner-id" => i.OwnerId,
                "location-id" => i.LocationId,
                "type" => i.Type.ToString().ToLowerInvariant(),
                _ => null,
            },
            Location l => field switch
            {
                "status" => l.Status.ToString().ToLowerInvariant(),
                "parent-id" => l.ParentId,
                "type" => l.Type.ToString().ToLowerInvariant(),
                _ => null,
            },
            Plotline p => field switch
            {
                "status" => p.Status.ToString().ToLowerInvariant(),
                "tension" => p.Tension.ToString().ToLowerInvariant(),
                "planned-reveal-chapter-id" => p.PlannedRevealChapterId,
                "resolved-at-chapter-id" => p.ResolvedAtChapterId,
                _ => null,
            },
            Relationship r => field switch
            {
                "type" => r.Type.ToString().ToLowerInvariant(),
                "strength" => r.Strength.ToString().ToLowerInvariant(),
                "since-chapter-id" => r.SinceChapterId,
                "until-chapter-id" => r.UntilChapterId,
                _ => null,
            },
            _ => null,
        };

    private static PublishResult Failure(string error) => new(false, null, 0, 0, [], error);
}
