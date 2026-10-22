using System.Text;
using Core.Data;
using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>单本小说一次同步的汇总报告。</summary>
public sealed record SyncReport(
    int Pushed,
    int Pulled,
    int Deleted,
    int Conflicts,
    int Skipped,
    IReadOnlyList<string> ConflictPaths,
    IReadOnlyList<string> SkippedFiles
);

/// <summary>同步生命周期状态（TUI 状态栏 / Desktop 顶栏标识的数据源）。</summary>
public enum CloudSyncState
{
    /// <summary>空闲（从未同步或上次同步已完成且无未合并冲突）。</summary>
    Idle,

    /// <summary>同步进行中。</summary>
    Syncing,

    /// <summary>上次同步成功完成。</summary>
    Synced,

    /// <summary>上次同步完成但有未合并冲突（冲突副本待作者处理）。</summary>
    Conflicts,

    /// <summary>云端不可达（网络异常 / 服务端故障）——本地创作不受影响。</summary>
    Offline,
}

/// <summary>
/// 云端小说同步引擎（cloud-novel-sync v3）：乐观锁 + 保留双版本。
/// 三路比较（本地文件 / 云端 meta / 本地 state 基线）逐 key 决策：
/// 本地永远是作者的 working copy——内容冲突本地为主、云端版本落冲突副本；
/// 删除 vs 修改一律复活；重命名按 local.Path / remote.Path / state.Path 三路判定
/// （修复两设备互改路径的乒乓，双方都改时本地路径为主）。
/// 远端整体重置保护：云端空 + 本地有 state + 本地有文件 → 全量按新建 push（本地胜）。
/// </summary>
public sealed class CloudNovelSyncService(
    ICloudNovelSyncClient client,
    CloudSyncStateStore stateStore,
    NovelWorkspace workspace,
    string mythologyDir,
    ILogger? logger = null
)
{
    /// <summary>
    /// 同步串行闸门：启动自动 pull 与用户手动 :cloud sync 可能并发，
    /// 决策-执行全程共享同一项目目录/state → 必须（1,1）串行，后到者等待。
    /// </summary>
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private CloudSyncState _state = CloudSyncState.Idle;

    /// <summary>同步状态变化事件（UI 线程外触发，订阅方自行调度回 UI）。</summary>
    public event Action<CloudSyncState>? StateChanged;

    /// <summary>当前同步状态（最近一次事件的快照）。</summary>
    public CloudSyncState State => _state;

    private void SetState(CloudSyncState state)
    {
        if (_state == state)
        {
            return;
        }
        _state = state;
        StateChanged?.Invoke(state);
    }

    public async Task<SyncReport> SyncAsync(
        string novelId,
        bool pullOnly = false,
        CancellationToken ct = default
    )
    {
        await _syncGate.WaitAsync(ct);
        try
        {
            SetState(CloudSyncState.Syncing);
            var report = await SyncCoreAsync(novelId, pullOnly, ct);
            SetState(report.Conflicts > 0 ? CloudSyncState.Conflicts : CloudSyncState.Synced);
            return report;
        }
        catch (CloudApiException exception)
        {
            // 云端不可达 / 凭据失效：本地创作不受影响；状态标识转 Offline，异常上抛给调用方 Toast
            SetState(CloudSyncState.Offline);
            logger?.LogWarning(
                "Cloud sync unavailable ({Code}): {Message}",
                exception.Code,
                exception.Message
            );
            throw;
        }
        catch (HttpRequestException exception)
        {
            SetState(CloudSyncState.Offline);
            logger?.LogWarning(exception, "Cloud sync network failure");
            throw new CloudApiException("network_unreachable", "云端不可达，请检查网络后重试。");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 请求超时（非用户取消）
            SetState(CloudSyncState.Offline);
            throw new CloudApiException("sync_timeout", "云端同步超时，请稍后重试。");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// 本次同步落点：书所在的项目目录（清单并集里查 novel.yaml 的 id）；
    /// 查不到（新书/目录被移动）退回活动目录。根未被指定 → 直接拒绝：
    /// 宁可不同步，也不能把云端内容写进猜出来的目录（Desktop 打包后 cwd 不可控）。
    /// </summary>
    private string ResolveNovelRoot(string novelId)
    {
        if (!workspace.HasSpecifiedRoot)
        {
            throw new CloudApiException(
                "novels_root_undetermined",
                "尚未确定小说目录：请先在项目列表中打开（或添加）一个本地目录，再同步。"
            );
        }
        return workspace.FindRootForNovel(novelId) ?? workspace.ActiveRoot;
    }

    private async Task<SyncReport> SyncCoreAsync(
        string novelId,
        bool pullOnly,
        CancellationToken ct
    )
    {
        var novelsRoot = ResolveNovelRoot(novelId);
        var local = await SyncDocumentEnumerator.EnumerateAsync(novelsRoot, novelId, ct);
        var state = await stateStore.LoadAsync(novelId, ct);
        var remoteMeta = await ListRemoteMetasAsync(novelId, ct);

        var localByKey = local.ToDictionary(d => (d.DocumentType, d.EntityId));
        var remoteByKey = remoteMeta.ToDictionary(m => (m.DocumentType, m.EntityId));

        // 远端重置保护：云端命名空间空但本地与 state 均非空 → 不做删除传播，全量重新 push
        var remoteReset = remoteByKey.Count == 0 && state.Documents.Count > 0 && local.Count > 0;

        // 目录漂移保护：本地小说「目录本身」消失（换机器/目录被移动）≠ 用户删光文件。
        // 项目目录清单只保证「不再靠猜」，不保证书一定在；目录不在时「本地全缺失」
        // 会误触发删除传播清空云端。
        // 判定：state 中记录的文档路径首段（= 小说目录名）在 novelsRoot 下不存在 → 漂移。
        // 目录仍在但文件被用户删光 → 不算漂移（删除传播合法）。真想传播「删整本」走
        // 未来的显式 :cloud delete，不走静默同步。
        var driftSuspected = false;
        if (state.Documents.Count > 0 && remoteByKey.Count > 0 && local.Count == 0)
        {
            var novelDirName = state
                .Documents.Values.Select(docState => docState.Path.Split('/')[0])
                .FirstOrDefault(segment => !string.IsNullOrEmpty(segment));
            driftSuspected =
                string.IsNullOrEmpty(novelDirName)
                || !Directory.Exists(
                    Path.Combine(novelsRoot, novelDirName.Replace('/', Path.DirectorySeparatorChar))
                );
            if (driftSuspected)
            {
                logger?.LogWarning(
                    "Cloud sync: novel {NovelId} 的目录（{Dir}）在 novelsRoot ({Root}) 下不存在而 state 非空"
                        + " —— 疑似 novelsRoot 漂移，本次跳过删除传播（仅 pull）",
                    novelId,
                    novelDirName,
                    novelsRoot
                );
            }
        }

        var pushed = 0;
        var pulled = 0;
        var deleted = 0;
        var conflicts = 0;
        var skipped = 0;
        var conflictPaths = new List<string>();
        var skippedFiles = new List<string>();

        var pushes = new List<SyncPushItemDto>();
        var deletes = new List<SyncDeleteItemDto>();
        var fetchKeys = new List<(string Type, string EntityId)>();
        var conflictFetches = new List<(string Type, string EntityId)>();

        // state 更新（在 push/delete 结果回来前先记账本地已定动作）
        var nextDocuments = new Dictionary<string, CloudSyncDocState>(state.Documents);

        // ── 决策：遍历三类 key 的并集 ──
        foreach (var localDocument in local)
        {
            var key = (localDocument.DocumentType, localDocument.EntityId);
            var keyText = $"{key.Item1}:{key.Item2}";
            state.Documents.TryGetValue(keyText, out var baseline);

            if (!remoteByKey.TryGetValue(key, out var remote))
            {
                // 本地 ✅ 远端 ❌
                if (baseline is not null && !remoteReset)
                {
                    if (baseline.Sha256 == localDocument.Sha256)
                    {
                        // 远端删除且本地未改 → 同步删除本地
                        TryDeleteLocalFile(novelsRoot, localDocument.RelativePath);
                        nextDocuments.Remove(keyText);
                        deleted++;
                        continue;
                    }
                    // 远端删除但本地已改 → 保留本地，移除 state（下次 sync 复活）
                    nextDocuments.Remove(keyText);
                    continue;
                }
                // 新文件或远端重置 → push 新建
                if (!pullOnly)
                {
                    pushes.Add(ToPushItem(localDocument, baseRevision: 0));
                }
                continue;
            }

            // 本地 ✅ 远端 ✅
            var localShaMatchesRemote = localDocument.Sha256 == remote.ContentSha256;
            if (localShaMatchesRemote && localDocument.RelativePath == remote.RelativePath)
            {
                // 一致（含 path 相同）→ state 收敛到远端真值
                nextDocuments[keyText] = new CloudSyncDocState(
                    remote.Revision,
                    remote.ContentSha256,
                    remote.RelativePath
                );
                continue;
            }

            if (localShaMatchesRemote)
            {
                // 内容一致但 path 不同 → v3 重命名三路判定
                ResolveRename(
                    localDocument,
                    remote,
                    baseline,
                    pullOnly,
                    pushes,
                    fetchKeys,
                    nextDocuments,
                    keyText
                );
                continue;
            }

            // 内容不一致
            if (baseline is null)
            {
                // 首次同步分歧：保守写冲突副本（本地为主），不静默覆盖任一方。
                // 基线记远端真值（非本地 sha）：下次 sync 判「远端未改，本地改」→ push 本地稿（本地为主）。
                // 若记本地 sha 会被误判为「本地未改，远端改」→ pull 覆盖本地稿 → 丢稿。
                conflictFetches.Add(key);
                nextDocuments[keyText] = new CloudSyncDocState(
                    remote.Revision,
                    remote.ContentSha256,
                    localDocument.RelativePath
                );
                conflicts++;
                continue;
            }
            if (baseline.Sha256 == remote.ContentSha256)
            {
                // 远端未改，本地改 → push
                if (!pullOnly)
                {
                    pushes.Add(ToPushItem(localDocument, remote.Revision));
                }
                continue;
            }
            if (baseline.Sha256 == localDocument.Sha256)
            {
                // 本地未改，远端改 → pull 覆盖
                fetchKeys.Add(key);
                continue;
            }
            // 双方都改 → 冲突副本 + 本地为主。基线记远端真值：下次 sync 判
            // 「远端未改，本地改」→ push 本地稿（作者未合并）或推送合并稿（作者已合并）。
            // 若记本地 sha 会被误判为「本地未改」→ pull 覆盖 → 冲突前本地稿丢失。
            conflictFetches.Add(key);
            nextDocuments[keyText] = new CloudSyncDocState(
                remote.Revision,
                remote.ContentSha256,
                localDocument.RelativePath
            );
            conflicts++;
        }

        // ── 远端有、本地无 ──
        foreach (var remote in remoteMeta)
        {
            var key = (remote.DocumentType, remote.EntityId);
            if (localByKey.ContainsKey(key))
            {
                continue;
            }
            var keyText = $"{key.Item1}:{key.Item2}";
            state.Documents.TryGetValue(keyText, out var baseline);

            if (baseline is null)
            {
                // 新设备 bootstrap / 远端新建 → pull
                fetchKeys.Add(key);
                continue;
            }
            if (baseline.Sha256 == remote.ContentSha256)
            {
                // 本地删除，远端未改 → 传播删除
                if (!pullOnly && !remoteReset && !driftSuspected)
                {
                    deletes.Add(
                        new SyncDeleteItemDto(
                            remote.DocumentType,
                            remote.EntityId,
                            baseline.Revision
                        )
                    );
                }
                else
                {
                    nextDocuments.Remove(keyText);
                }
                continue;
            }
            // 本地删除后远端又改 → 拉取远端内容写回（删除意图让位于修改）
            fetchKeys.Add(key);
        }

        // state 中已无处安放的 key（本地、云端都消失）→ 清理
        foreach (var keyText in state.Documents.Keys)
        {
            var separator = keyText.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            var type = keyText[..separator];
            var entityId = keyText[(separator + 1)..];
            // 本地、云端都已无此 key（含本次将成功删除的）：state 悬空 → 清理；
            // 若本次 delete 冲突，结果处理器会以新基线重写 state
            if (
                !localByKey.ContainsKey((type, entityId))
                && !remoteByKey.ContainsKey((type, entityId))
            )
            {
                nextDocuments.Remove(keyText);
            }
        }

        // ── 执行：push（分批）──
        if (pushes.Count > 0)
        {
            var results = await client.PushDocumentsAsync(novelId, pushes, ct);
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                var item = pushes[i];
                var keyText = $"{item.DocumentType}:{item.EntityId}";
                switch (result.Status)
                {
                    case "accepted":
                        nextDocuments[keyText] = new CloudSyncDocState(
                            result.Revision ?? 1,
                            item.ContentSha256,
                            item.RelativePath
                        );
                        pushed++;
                        break;
                    case "conflict":
                        if (result.Current is not null)
                        {
                            conflictPaths.Add(
                                await SyncConflictWriter.WriteAsync(
                                    mythologyDir,
                                    novelId,
                                    result.Current.RelativePath,
                                    result.Current.Content,
                                    result.Current.ContentEncoding,
                                    ct
                                )
                            );
                        }
                        // 基线记远端真值（Current.ContentSha256）：下次 sync 重推本地稿（本地为主）。
                        // 记 item.ContentSha256（本地 sha）会被误判「本地未改」→ pull 覆盖 → 丢稿。
                        nextDocuments[keyText] = new CloudSyncDocState(
                            result.Revision ?? result.Current?.Revision ?? 1,
                            result.Current?.ContentSha256 ?? item.ContentSha256,
                            item.RelativePath
                        );
                        conflicts++;
                        break;
                    default:
                        skipped++;
                        skippedFiles.Add(item.RelativePath);
                        logger?.LogWarning(
                            "Cloud sync push invalid: {Type}:{EntityId} → {Error}",
                            item.DocumentType,
                            item.EntityId,
                            result.Error
                        );
                        break;
                }
            }
        }

        // ── 执行：delete（分批）──
        if (deletes.Count > 0)
        {
            var results = await client.DeleteDocumentsAsync(novelId, deletes, ct);
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                var item = deletes[i];
                var keyText = $"{item.DocumentType}:{item.EntityId}";
                switch (result.Status)
                {
                    case "deleted":
                        nextDocuments.Remove(keyText);
                        deleted++;
                        break;
                    case "conflict" when result.Current is not null:
                        // 远端在列表与删除之间又被改：先保全远端版本，state 指向新基线，下次 sync 重试删除
                        conflictPaths.Add(
                            await SyncConflictWriter.WriteAsync(
                                mythologyDir,
                                novelId,
                                result.Current.RelativePath,
                                result.Current.Content,
                                result.Current.ContentEncoding,
                                ct
                            )
                        );
                        nextDocuments[keyText] = new CloudSyncDocState(
                            result.Current.Revision,
                            result.Current.ContentSha256,
                            result.Current.RelativePath
                        );
                        conflicts++;
                        break;
                    case "not_found":
                        nextDocuments.Remove(keyText);
                        deleted++; // 目标已达成（远端本就没有）
                        break;
                    default:
                        // 未知状态（服务端未来扩展）：保守保留基线，下次 sync 重试删除；
                        // 与 push 侧的 default 分支同构，避免静默吞结果。
                        skipped++;
                        logger?.LogWarning(
                            "Cloud sync delete unexpected status: {Type}:{EntityId} → {Status} ({Error})",
                            item.DocumentType,
                            item.EntityId,
                            result.Status,
                            result.Error
                        );
                        break;
                }
            }
        }

        await stateStore.SaveAsync(novelId, new CloudSyncNovelState(1, nextDocuments), ct);

        // ── 执行：fetch + 本地写盘（分批）──
        if (fetchKeys.Count > 0)
        {
            var documents = await client.FetchDocumentsAsync(novelId, fetchKeys, ct);
            foreach (var document in documents)
            {
                ct.ThrowIfCancellationRequested();
                var targetPath = Path.Combine(
                    novelsRoot,
                    document.RelativePath.Replace('/', Path.DirectorySeparatorChar)
                );
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                if (document.ContentEncoding == "base64")
                {
                    await File.WriteAllBytesAsync(
                        targetPath,
                        Convert.FromBase64String(document.Content),
                        ct
                    );
                }
                else
                {
                    await Core.Shared.AtomicFile.WriteAllTextAsync(
                        targetPath,
                        document.Content,
                        ct
                    );
                }
                var keyText = $"{document.DocumentType}:{document.EntityId}";
                var baseline = state.Documents.GetValueOrDefault(keyText);
                nextDocuments[keyText] = new CloudSyncDocState(
                    document.Revision,
                    document.ContentSha256,
                    document.RelativePath
                );
                // 重命名跟随/路径迁移：旧路径文件若存在且内容仍等于旧基线（未改）→ 清理；否则留 orphan 日志
                if (
                    baseline is not null
                    && baseline.Path != document.RelativePath
                    && baseline.Path.Length > 0
                )
                {
                    CleanupOrphanFile(novelsRoot, baseline.Path, baseline.Sha256);
                }
                pulled++;
            }
            await stateStore.SaveAsync(novelId, new CloudSyncNovelState(1, nextDocuments), ct);
        }

        // ── 执行：冲突副本（fetch 远端现行内容后写入 cloud-conflicts/）──
        if (conflictFetches.Count > 0)
        {
            var conflictDocuments = await client.FetchDocumentsAsync(novelId, conflictFetches, ct);
            foreach (var document in conflictDocuments)
            {
                conflictPaths.Add(
                    await SyncConflictWriter.WriteAsync(
                        mythologyDir,
                        novelId,
                        document.RelativePath,
                        document.Content,
                        document.ContentEncoding,
                        ct
                    )
                );
            }
        }

        return new SyncReport(
            pushed,
            pulled,
            deleted,
            conflicts,
            skipped,
            conflictPaths,
            skippedFiles
        );
    }

    /// <summary>
    /// `:cloud sync all`：枚举清单内全部项目目录下的本地小说逐一同步，返回 (小说数, 上传, 下载, 冲突) 聚合。
    /// </summary>
    public async Task<(int Novels, int Pushed, int Pulled, int Conflicts)> SyncAllAsync(
        bool pullOnly = false,
        CancellationToken ct = default
    )
    {
        if (!workspace.HasSpecifiedRoot)
        {
            throw new CloudApiException(
                "novels_root_undetermined",
                "尚未确定小说目录：请先在项目列表中打开（或添加）一个本地目录，再同步。"
            );
        }
        var novels = SyncDocumentEnumerator.DiscoverNovels(workspace.Roots);
        var pushed = 0;
        var pulled = 0;
        var conflicts = 0;
        foreach (var (novelId, _) in novels)
        {
            ct.ThrowIfCancellationRequested();
            var report = await SyncAsync(novelId, pullOnly, ct);
            pushed += report.Pushed;
            pulled += report.Pulled;
            conflicts += report.Conflicts;
        }
        return (novels.Count, pushed, pulled, conflicts);
    }

    /// <summary>`:cloud pull` 无本地小说时：拉取云端全部小说（bootstrap）。</summary>
    public async Task<IReadOnlyList<string>> PullAllNovelsAsync(CancellationToken ct = default)
    {
        var novels = await client.ListNovelsAsync(ct);
        var synced = new List<string>();
        foreach (var novel in novels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SyncAsync(novel.NovelId, pullOnly: true, ct);
                synced.Add(novel.NovelId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogError(exception, "Cloud pull failed for novel {NovelId}", novel.NovelId);
            }
        }
        return synced;
    }

    /// <summary>
    /// 远端文档清单。云端对「尚无该小说命名空间」返回 200 + 空数组（已验证的服务端语义：
    /// 未知 novelId 不产生 404），空列表即 bootstrap/远端重置信号，无需 404 分支。
    /// </summary>
    private async Task<List<SyncDocumentMetaDto>> ListRemoteMetasAsync(
        string novelId,
        CancellationToken ct
    ) => [.. await client.ListDocumentsAsync(novelId, ct)];

    private static SyncPushItemDto ToPushItem(LocalSyncDocument document, int baseRevision) =>
        new(
            document.DocumentType,
            document.EntityId,
            baseRevision,
            document.Sha256,
            document.Title,
            document.RelativePath,
            document.ContentEncoding,
            document.Content
        );

    /// <summary>
    /// v3 重命名三路判定（内容一致、path 不同时）：
    /// 远端改名 → 跟随；本地改名 → push；双方都改 → 本地路径为主（乒乓终止）。
    /// </summary>
    private static void ResolveRename(
        LocalSyncDocument local,
        SyncDocumentMetaDto remote,
        CloudSyncDocState? baseline,
        bool pullOnly,
        List<SyncPushItemDto> pushes,
        List<(string Type, string EntityId)> fetchKeys,
        Dictionary<string, CloudSyncDocState> nextDocuments,
        string keyText
    )
    {
        var baselinePath = baseline?.Path ?? remote.RelativePath;
        var localRenamed = local.RelativePath != baselinePath;
        var remoteRenamed = remote.RelativePath != baselinePath;

        if (remoteRenamed && !localRenamed)
        {
            // 远端改名：本地文件跟随远端路径（内容一致）——fetch 落盘新路径 + 清理旧路径，不 push
            fetchKeys.Add((remote.DocumentType, remote.EntityId));
            nextDocuments[keyText] = new CloudSyncDocState(
                remote.Revision,
                remote.ContentSha256,
                remote.RelativePath
            );
            return;
        }
        if (pullOnly)
        {
            return;
        }
        // 本地改名（含双方都改 → 本地路径为主）：push 本地 path（内容 sha 相同，安全）
        pushes.Add(ToPushItem(local, remote.Revision));
        nextDocuments[keyText] = new CloudSyncDocState(
            remote.Revision,
            remote.ContentSha256,
            local.RelativePath
        );
    }

    private void TryDeleteLocalFile(string novelsRoot, string relativePath)
    {
        try
        {
            var path = Path.Combine(
                novelsRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)
            );
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Cloud sync: failed to delete local file {Path}",
                relativePath
            );
        }
    }

    /// <summary>删除旧路径文件（若其内容仍等于基线 sha，即未被本地编辑）。</summary>
    private void CleanupOrphanFile(string novelsRoot, string oldRelativePath, string baselineSha)
    {
        try
        {
            var path = Path.Combine(
                novelsRoot,
                oldRelativePath.Replace('/', Path.DirectorySeparatorChar)
            );
            if (!File.Exists(path))
            {
                return;
            }
            var bytes = File.ReadAllBytes(path);
            var hasBom =
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var content = new UTF8Encoding(false).GetString(hasBom ? bytes[3..] : bytes);
            if (
                Convert
                    .ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            Encoding.UTF8.GetBytes(content)
                        )
                    )
                    .ToLowerInvariant() == baselineSha
            )
            {
                File.Delete(path);
            }
            else
            {
                logger?.LogWarning(
                    "Cloud sync: orphan file at {Path}（内容与基线不同，保留由用户清理）",
                    oldRelativePath
                );
            }
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "Cloud sync orphan cleanup failed for {Path}",
                oldRelativePath
            );
        }
    }
}
