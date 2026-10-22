using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Microsoft.Extensions.Logging;

namespace CloudDiagnosis;

/// <summary>调度输入 — Hook 从 INovelStore/WritingProfile 组装的确定性判定材料。</summary>
public sealed record DiagnosisInput
{
    public required string NovelId { get; init; }
    public required string ChapterId { get; init; }
    public required string ChapterTitle { get; init; }
    public required int ChapterOrder { get; init; }
    public required string CurrentContent { get; init; }
    public required bool IsRewrite { get; init; }

    /// <summary>全部章节（含当前章，按 Order 排序）— L1-1/L2-1/L2-2 判定与请求组装。</summary>
    public required IReadOnlyList<Chapter> Chapters { get; init; }
    public required Novel Novel { get; init; }

    /// <summary>前一章内容（L1-3 信息差突降对照；无前章为 null）。</summary>
    public string? PreviousContent { get; init; }

    /// <summary>快照缓存已 stale（§6.2/L2-4 补偿通道；由 Hook 从 ContextSnapshotCache 计算）。</summary>
    public bool SnapshotStale { get; init; }

    /// <summary>创作目标（scene.plan 规划语义驱动字段；null = 走云端 run.Goal 兜底）。</summary>
    public string? SceneGoal { get; init; }

    /// <summary>
    /// 当前章之前已出场的角色 ID 集（entity-evolution-lifecycle §5.2：L2-2 新角色首现判定改读
    /// 权威出场记录；null = 无记录数据，回退章节正文扫描）。
    /// </summary>
    public IReadOnlySet<string>? PriorAppearedCharacterIds { get; init; }

    /// <summary>
    /// 设定图摘要（Phase 5.1）：scene.plan 的计划符合度/未来 beat 与 story.check 的
    /// plan-conformance/continuity-audit/setup-grounding 的地基；
    /// 由 Hook 在调度命中后构建（跳过轮不建）；null = 不挂（旧行为，云端静默降级）。
    /// </summary>
    public SetupCheckInput? SetupDigest { get; init; }
}

/// <summary>调度决策（§3.1 三层漏斗产物）。</summary>
/// <param name="ShouldRun">是否调用云端。</param>
/// <param name="DeepCheck">是否升级深度档 story.check（任一 L2 事件命中且预算允许）。</param>
/// <param name="SkipReason">ShouldRun=false 时的跳过说明（注入信号用）。</param>
/// <param name="HitSummary">命中摘要（调试/日志）。</param>
/// <param name="LateNovelForceDeep">后半段强制深度（≥20 章，无视 L2 命中）。</param>
public sealed record DiagnosisDecision(
    bool ShouldRun,
    bool DeepCheck,
    string? SkipReason,
    string HitSummary,
    bool LateNovelForceDeep = false
);

/// <summary>
/// 一轮云端聚合结果 — 基础档（context.retrieve + scene.plan）恒先行，
/// 深度档 story.check 仅 DeepCheck 且预算允许时追加（§3.1 两档升级路径）。
/// </summary>
/// <param name="DeepUnits">深度档实际结算消耗（story.check 的 cloud-work-units；基础档/未跑深度档为 0）。</param>
public sealed record CloudRound(
    ScenePlanOutcome? ScenePlan,
    ContextRetrieveOutcome? ContextRetrieve,
    StoryCheckOutcome? StoryCheck,
    bool DeepDenied,
    double OverallScore,
    bool BudgetOverrun = false,
    int DeepUnits = 0
);

/// <summary>
/// 云端诊断调度器（HARNESS-CLOUD §3 组件 A）— E2 完整版 + H3 快照缓存接线。
/// L1 本地免费预筛（五指标）→ L2 结构性事件（L2-4 由 Hook 经 SnapshotStale 传入）→
/// 基础档（context.retrieve + scene.plan，各 1 unit）→ 预算允许时深度档
/// （story.check，4–6 units）。每章幂等、会话预算、异常静默降级；
/// Depth 按能力分级——基础档 quick，深度档 story.check 用 standard（RST Layer B 试点）。
///
/// 能力目录消费（2026-10-18）：探活写入的 <see cref="CloudCapabilityCatalogStore"/> 快照（服务端声明的
/// supported-depths / scope / 能力清单）约束请求组装与调用——深度按服务端接受档位回退、跨章窗口仅在
/// 能力声明 <c>chapter-window</c> 时发送、未声明能力直接跳过（不再发必然被拒的调用）。
/// 无快照（未探活/旧服务端）时全部 fail-open，行为与接线前逐字节一致。
/// </summary>
/// <remarks>
/// 线程安全（P-2）：会话状态（<c>_baseCheckedChapters</c> / <c>_deepCheckedChapters</c> /
/// <c>_rewriteCounts</c> / <c>_debutScanCache</c> / <c>_deepUsed</c> / <c>_lastCheckedMaxOrder</c> 等）
/// 无锁，**假设单写者**——同一时刻只由单个 Agent 写作循环调用（宿主按 novelId 隔离，不做多小说
/// 并发诊断）。若将来允许并发调用，须在调用侧串行化或为这些状态补锁；依赖注入的 options/client
/// 等只读对象不受此约束。
/// </remarks>
public sealed class DiagnosisScheduler(
    IFictionHarnessClient client,
    CloudDiagnosisOptions options,
    ContextSnapshotCache? snapshotCache = null,
    ILogger? logger = null,
    NovelBudgetStore? novelBudgetStore = null,
    CloudCapabilityCatalogStore? capabilityCatalog = null
)
{
    /// <summary>L1-2 AI 味阈值（对齐 WritingProfile.BuildSignalMessage 现行 1.0/千字语义）。</summary>
    private const double AiBuzzwordThreshold = 1.0;

    /// <summary>
    /// story.check 的 Context.PreviousChapters 窗口（5.2）：target 之前最近 N 章，升序。
    /// 服务端再按 standard 20 / deep 50 截尾并按章引用去重（与 Chapters 的重叠以 Chapters 为准）。
    /// </summary>
    private const int PreviousChapterWindow = 20;

    /// <summary>基础档每章幂等集合（§3.4：同章会话内 ≤1 次）。
    /// 进程内、重启即清（可接受：深度档有服务端预算兜底；基础档最多重复一次拉取）。</summary>
    private readonly HashSet<string> _baseCheckedChapters = new(StringComparer.Ordinal);

    /// <summary>深度档已调用章集合（同章 ≤1 次）。</summary>
    private readonly HashSet<string> _deepCheckedChapters = new(StringComparer.Ordinal);

    /// <summary>L2-3 同章重写计数。</summary>
    private readonly Dictionary<string, int> _rewriteCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// P-1：无权威出场记录时的新角色首现扫描结果（O(角色数 × 章节正文)）按 (chapterId, 正文) 记忆化；
    /// 正文引用/内容未变直接复用（string.Equals 先比引用，同实例近乎零成本）。
    /// 并发语义同其余会话状态：单写者假设（见 P-2 注明）。
    /// </summary>
    private readonly Dictionary<string, (string Content, bool Debut)> _debutScanCache = new(
        StringComparer.Ordinal
    );

    /// <summary>上次云端诊断时的最大章 Order（L1-1 节奏位锚点；0 = 会话内未诊断过）。</summary>
    private int _lastCheckedMaxOrder;

    /// <summary>深度档已用预算。</summary>
    private int _deepUsed;

    /// <summary>预算告警是否已注入（每会话 1 条）。</summary>
    private bool _budgetWarned;

    /// <summary>深度档被拒（权限/鉴权类拒绝；能力档位门禁已移除，保留为防御路径）后的降级标记（会话级粘滞）。</summary>
    private bool _deepDeniedSticky;

    /// <summary>预算超额标记（会话级粘滞，R-9 语义确认）：置位条件 = 后半段强制深度（方案 A，
    /// §16.5）或会话配额已耗尽但预算池续付；置位后本会话所有 CloudRound 恒带 BudgetOverrun=true，
    /// 信号备注持续说明「为何会话配额之外仍有深度检查」。</summary>
    private bool _budgetOverrun;

    /// <summary>DECISION 会话级频率计数（§16.2 频率红线）。</summary>
    private int _decisionSlotCounter;

    /// <summary>会话内深度预算已耗尽（用于告警）。</summary>
    public bool DeepBudgetExhausted => _deepUsed >= options.DeepBudgetPerSession;

    /// <summary>
    /// C-5：本轮降级的云端错误码（usage_limit_exceeded / timeout / unavailable……）。
    /// 每轮开始时重置；供 Hook 区分「额度耗尽」与「网络不可用」并作一次性提示。
    /// </summary>
    public string? LastDegradeCode { get; private set; }

    /// <summary>
    /// §16.5 方案 B/C：综合预算判定——会话配额 + 每小说持久池 + 银行。
    /// 返回 (exhausted, usingBank, bankBalance) 三要素。
    /// </summary>
    public (bool Exhausted, bool UsingBank, int BankBalance) CheckBudget(string novelId)
    {
        var sessionExhausted = _deepUsed >= options.DeepBudgetPerSession;
        if (novelBudgetStore is null)
            return (sessionExhausted, false, 0);
        var state = novelBudgetStore.GetOrCreate(
            novelId,
            options.DeepBudgetPerNovel,
            options.DeepBudgetPerSession
        );
        var totalRemaining = state.TotalRemaining;
        return (sessionExhausted && totalRemaining <= 0, totalRemaining > 0, state.BankBalance);
    }

    /// <summary>一次性预算告警消费位（Hook 注入告警信号后取 true）。</summary>
    public bool TryConsumeBudgetWarning()
    {
        if (_budgetWarned)
            return false;
        _budgetWarned = true;
        return true;
    }

    /// <summary>DECISION 频率位消费（§16.2 频率红线：每会话上限 DecisionMaxPerSession）。</summary>
    public bool TryConsumeDecisionSlot()
    {
        if (_decisionSlotCounter >= options.DecisionMaxPerSession)
            return false;
        _decisionSlotCounter++;
        return true;
    }

    /// <summary>
    /// §16.5 方案 C 生产调用点：结算某本小说的会话预算（剩余配额转入预算银行）。
    /// 由 Hook 在切换当前小说时对上一本触发（HARNESS-CLOUD §16.5 方案 C 接线修复）。
    /// </summary>
    public void SettleSession(string novelId) =>
        novelBudgetStore?.SettleSession(
            novelId,
            options.DeepBudgetPerNovel,
            options.DeepBudgetPerSession
        );

    /// <summary>
    /// §3.1 三层漏斗判定（纯本地，零成本）。L2 命中 → DeepCheck；
    /// 任一 L2 命中额外解除该章基础档幂等（U-6 对称：强制深度体检/快照越界需重新拉基础档信号）。
    /// </summary>
    public DiagnosisDecision Decide(DiagnosisInput input)
    {
        if (!options.Enabled)
            return new DiagnosisDecision(false, false, "总开关关闭", "");

        // U-7 扩展（层 1 采样门槛）：空/实质性内容过少（标点、空白为主）时，引擎四个 quick 维度
        // 全部走「无信号 → 先验基准分」路径，几何均值恒 ≈0.61——分数与内容无关，本地直接跳过。
        var substantiveChars = CountSubstantiveChars(input.CurrentContent);
        if (substantiveChars < options.MinTargetContentChars)
            return new DiagnosisDecision(
                false,
                false,
                $"当前章实质性内容不足（{substantiveChars} 字 < {options.MinTargetContentChars}），跳过云端诊断",
                ""
            );

        var hits = new List<string>();
        var deep = false;

        // §3.4 基础档幂等：同章已检查且无 L2 强制事件时，忽略本轮 L1 命中
        var alreadyBaseChecked = _baseCheckedChapters.Contains(input.ChapterId);

        // ── L2 结构性事件（命中即候选 + 升级深度档）──
        var l2Hit = false;
        if (input.IsRewrite && IsSecondRewrite(input.ChapterId))
        {
            hits.Add("L2-3 同章二次重写");
            deep = true;
            l2Hit = true;
        }
        if (HasNewCharacterDebut(input))
        {
            hits.Add("L2-2 新角色首次出场");
            deep = true;
            l2Hit = true;
        }
        if (IsForeshadowingRevealChapter(input))
        {
            hits.Add("L2-1 伏笔回收章写入");
            deep = true;
            l2Hit = true;
        }
        if (input.SnapshotStale)
        {
            hits.Add("L2-4 快照 stale 越界");
            l2Hit = true;
        }
        // U-6：任一 L2 命中都解除该章基础档幂等（对称）——结构性事件/快照越界需重新拉基础档信号，
        // 否则同章重写或快照刷新后 L1 命中会被旧幂等记录吞掉。
        if (l2Hit)
            _baseCheckedChapters.Remove(input.ChapterId);

        // ── L1 本地免费预筛 ──
        var maxOrder = MaxOrder(input.Chapters);
        var lateNovelThreshold = Math.Max(
            20,
            (int)Math.Ceiling(input.Chapters.Count * options.LateNovelOrderRatio)
        );
        var lateNovelForceDeep = maxOrder >= lateNovelThreshold; // P3 §16.2：后半段强制深度（地板 20 章，避免测试/短篇误触发）
        if (maxOrder - _lastCheckedMaxOrder >= GenrePacingFloor(input.Novel.Genre))
            hits.Add("L1-1 节奏位到期");
        if (WritingProfile.ScanAiBuzzwordDensity(input.CurrentContent) > AiBuzzwordThreshold)
            hits.Add("L1-2 AI味超阈");
        if (IsInfoGapSuspiciousDrop(input))
            hits.Add("L1-3 信息差泄底疑云");
        if (
            WritingProfile.ScanChapterHookType(
                input.CurrentContent,
                WritingProfile.FromNovel(input.Novel).GenreProfile
            )
            is null
        )
            hits.Add("L1-4 章末无钩子");

        // 基础档幂等：已检查过的章，L1 命中不再计入（L2 事件不受限）
        if (alreadyBaseChecked)
            hits.RemoveAll(h => h.StartsWith("L1-", StringComparison.Ordinal));

        if (hits.Count == 0)
            return new DiagnosisDecision(
                false,
                false,
                $"本轮跳过（非关键节点，距上次全量诊断 {Math.Max(0, maxOrder - _lastCheckedMaxOrder)} 章 / "
                    + $"预算剩余 {options.DeepBudgetPerSession - _deepUsed} 次）",
                ""
            );

        // 深度档还需预算与幂等放行
        var deepAllowed =
            deep
            && !_deepDeniedSticky
            && !_deepCheckedChapters.Contains(input.ChapterId)
            && (
                _deepUsed < options.DeepBudgetPerSession
                || novelBudgetStore
                    ?.GetOrCreate(
                        input.NovelId,
                        options.DeepBudgetPerNovel,
                        options.DeepBudgetPerSession
                    )
                    .TotalRemaining > 0
            );

        var summary = string.Join("、", hits);
        if (deep && !deepAllowed)
            summary += "（深度档不可用：预算耗尽或已检查）";

        return new DiagnosisDecision(true, deepAllowed, null, summary, lateNovelForceDeep);
    }

    /// <summary>
    /// 执行一轮云端调用（§3.1：先基础档后深度档）。返回 null = 全部失败 → Hook 本地降级。
    /// 用户取消 OCE 向上抛出（§12 红线 5）。
    /// </summary>
    public async Task<CloudRound?> RunAsync(
        DiagnosisInput input,
        DiagnosisDecision decision,
        CancellationToken ct
    )
    {
        if (!decision.ShouldRun)
            return null;

        // P-1：本轮章节排序只做一次（此前 context.retrieve / scene.plan / story.check / 兜底刷新各排一次）
        var orderedChapters = input.Chapters.OrderBy(c => c.Order).ToList();

        LastDegradeCode = null; // C-5：降级码每轮重置（防上一轮陈旧码在静默失败轮误报）
        ScenePlanOutcome? scenePlan = null;
        ContextRetrieveOutcome? contextRetrieve = null;
        StoryCheckOutcome? storyCheck = null;
        // 基础档原始快照：成功后直接更新缓存，免二次 context.retrieve；失败走下方兜底刷新
        AgentContextSnapshot? baseSnapshot = null;
        var deepDenied = false;
        var anySuccess = false;
        var deepUnits = 0; // 深度档实际结算消耗（对账展示；基础档恒 0）

        // ── 基础档 1/2：fiction.context.retrieve（GetContextAsync 内部强制能力）──
        if (!ServerAdvertises("fiction.context.retrieve"))
        {
            RecordNotAdvertised("context.retrieve");
        }
        else
        {
            try
            {
                using var cts = Linked(ct);
                var request = BuildRequest("fiction.context.retrieve", input, orderedChapters);
                var snapshot = await client.GetContextAsync(
                    request,
                    BuildIdempotencyKey("ctx", input, request),
                    cts.Token
                );
                baseSnapshot = snapshot;
                contextRetrieve = ParseContextRetrieveFromSnapshot(snapshot);
                anySuccess |= contextRetrieve is not null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LastDegradeCode = "timeout";
                LogDegraded("context.retrieve 超时");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordDegrade(ex);
                LogDegraded($"context.retrieve: {ex.Message}");
            }
        }

        // P3 §16.1 基础档反馈升级：基础档评分极低且存在高危信号时，强行升级深度档。
        // 跨仓库缺口 1.5 已让快照携带 signals（AgentContextSnapshot.Signals），恢复文档
        // 声明的双重条件（overallScore 低于阈值 且 signals 中 high severity ≥ 2）。
        var upgradeBecauseOfLowScore = false;
        if (
            contextRetrieve is not null
            && contextRetrieve.OverallScore < options.UpgradeScoreThreshold
            && contextRetrieve.Signals.Count(s => s.Severity == CloudContractLiterals.SeverityHigh)
                >= 2
            && !_deepDeniedSticky
            && !_deepCheckedChapters.Contains(input.ChapterId)
            && (
                _deepUsed < options.DeepBudgetPerSession
                || novelBudgetStore
                    ?.GetOrCreate(
                        input.NovelId,
                        options.DeepBudgetPerNovel,
                        options.DeepBudgetPerSession
                    )
                    .TotalRemaining > 0
            )
        )
        {
            upgradeBecauseOfLowScore = true;
        }

        // ── 基础档 2/2：fiction.scene.plan（repairIntent 来源）──
        if (!ServerAdvertises("fiction.scene.plan"))
        {
            RecordNotAdvertised("scene.plan");
        }
        else
        {
            try
            {
                using var cts = Linked(ct);
                var request = BuildRequest("fiction.scene.plan", input, orderedChapters);
                var result = await client.ExecuteAsync(
                    request,
                    BuildIdempotencyKey("plan", input, request),
                    cts.Token
                );
                scenePlan = SignalBuilder.ParseScenePlan(result, options.PassScoreThreshold);
                anySuccess |= scenePlan is not null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LastDegradeCode = "timeout";
                LogDegraded("scene.plan 超时");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordDegrade(ex);
                LogDegraded($"scene.plan: {ex.Message}");
            }
        }

        // ── 深度档：fiction.story.check（DeepCheck/LateNovelForceDeep/Upgrade 且预算/幂等放行时）──
        var shouldDeepCheck =
            decision.DeepCheck || decision.LateNovelForceDeep || upgradeBecauseOfLowScore;
        if (
            shouldDeepCheck
            && !_deepDeniedSticky
            && !_deepCheckedChapters.Contains(input.ChapterId)
        )
        {
            var budgetAvailable =
                _deepUsed < options.DeepBudgetPerSession
                || novelBudgetStore
                    ?.GetOrCreate(
                        input.NovelId,
                        options.DeepBudgetPerNovel,
                        options.DeepBudgetPerSession
                    )
                    .TotalRemaining > 0;
            // §16.5 方案 A：后半段（LateNovelForceDeep）L2 命中时强制允许深度档，
            // 即使预算耗尽——标记 BudgetOverrun 并在信号中警告。
            var forceOverrun = !budgetAvailable && decision.LateNovelForceDeep;
            if (!ServerAdvertises("fiction.story.check"))
            {
                RecordNotAdvertised("story.check");
            }
            else if (budgetAvailable || forceOverrun)
            {
                if (forceOverrun || _deepUsed >= options.DeepBudgetPerSession)
                    _budgetOverrun = true;
                try
                {
                    using var cts = Linked(ct);
                    var request = BuildRequest("fiction.story.check", input, orderedChapters);
                    var result = await client.ExecuteAsync(
                        request,
                        BuildIdempotencyKey("check", input, request),
                        cts.Token
                    );
                    storyCheck = SignalBuilder.ParseStoryCheck(result);
                    _deepCheckedChapters.Add(input.ChapterId);
                    _deepUsed++;
                    deepUnits = result.CloudWorkUnits; // 对账展示：本轮深度档实际结算消耗
                    novelBudgetStore?.TryConsume(input.NovelId, options.DeepBudgetPerNovel);
                    // §3.6：深度档解析成功即算云端路径成功（无 network 数据时不得丢弃深度结果）
                    anySuccess |= storyCheck is not null;
                }
                catch (CloudApiException ex) when (CloudErrorMapper.IsEntitlementDenial(ex.Code))
                {
                    // 权限/鉴权类拒绝 → 会话级粘滞降级到仅基础档（§3.6）。
                    // 统一积分墙（2026-10-03）后能力档位门禁已移除（全部能力 Availability=free），
                    // 本路径保留为防御（未来恢复分层或服务端策略收紧时仍生效）。
                    // #3 修正：只有权限/订阅类拒绝才粘滞；输入/契约类 4xx 落下方通用分支（一次性降级），
                    // 否则一次参数错误（422）会永久关掉整个会话的深度档。
                    _deepDeniedSticky = true;
                    deepDenied = true;
                    RecordDegrade(ex);
                    LogDegraded($"story.check 被拒（{ex.Code}），降级基础档");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    LastDegradeCode = "timeout";
                    LogDegraded("story.check 超时");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordDegrade(ex);
                    LogDegraded($"story.check: {ex.Message}");
                }
            }
            else if (!budgetAvailable)
            {
                LogDegraded("深度预算耗尽");
            }
        }

        if (!anySuccess)
            return null;

        // 会话状态推进（成功路径）
        _baseCheckedChapters.Add(input.ChapterId);
        _lastCheckedMaxOrder = Math.Max(_lastCheckedMaxOrder, MaxOrder(input.Chapters));

        // §6.2：成功后刷新快照缓存。复用本轮基础档已取得的快照（同内容不重复云端调用）；
        // 基础档未取到快照（超时/异常）时保留单独刷新兜底。失败静默沿用旧值。
        if (snapshotCache is not null)
        {
            if (baseSnapshot is not null)
                snapshotCache.UpdateFromSnapshot(input.NovelId, baseSnapshot, input.ChapterOrder);
            else
                await snapshotCache.RefreshAsync(
                    input.NovelId,
                    BuildRequest("fiction.context.retrieve", input, orderedChapters),
                    input.ChapterOrder,
                    ct
                );
        }

        var score = contextRetrieve?.OverallScore ?? scenePlan?.OverallScore ?? 0;
        return new CloudRound(
            scenePlan,
            contextRetrieve,
            storyCheck,
            deepDenied,
            score,
            _budgetOverrun,
            deepUnits
        );
    }

    // ── L1/L2 判定细节 ──

    private bool IsSecondRewrite(string chapterId)
    {
        _rewriteCounts[chapterId] = _rewriteCounts.GetValueOrDefault(chapterId) + 1;
        return _rewriteCounts[chapterId] >= 2;
    }

    private bool HasNewCharacterDebut(DiagnosisInput input)
    {
        if (string.IsNullOrEmpty(input.CurrentContent))
            return false;

        // entity-evolution-lifecycle §5.2：有权威出场记录时改读「此前已出场集」——
        // 当前章出现且不在前序记录中的角色 = 新登场（免逐章正文扫描）。
        if (input.PriorAppearedCharacterIds is { } priorAppeared)
        {
            foreach (var character in input.Novel.Characters)
            {
                if (string.IsNullOrWhiteSpace(character.Name))
                    continue;
                if (!input.CurrentContent.Contains(character.Name, StringComparison.Ordinal))
                    continue;
                if (!priorAppeared.Contains(character.Id))
                    return true; // 任一角色首现即命中
            }
            return false;
        }

        // 无记录数据 → 回退原正文扫描语义（既有测试/旧书路径）；P-1：按 (chapterId, 正文) 记忆化
        if (
            _debutScanCache.TryGetValue(input.ChapterId, out var cached)
            && string.Equals(cached.Content, input.CurrentContent, StringComparison.Ordinal)
        )
            return cached.Debut;

        var debut = ScanNewCharacterDebut(input);
        _debutScanCache[input.ChapterId] = (input.CurrentContent, debut);
        return debut;
    }

    /// <summary>回退扫描：当前章出现、其余章节从未出现的角色名 = 首现（P-1 记忆化目标）。</summary>
    private static bool ScanNewCharacterDebut(DiagnosisInput input)
    {
        foreach (var character in input.Novel.Characters)
        {
            if (string.IsNullOrWhiteSpace(character.Name))
                continue;
            if (!input.CurrentContent.Contains(character.Name, StringComparison.Ordinal))
                continue;
            var appearedBefore = input.Chapters.Any(c =>
                c.Id != input.ChapterId
                && c.Content.Contains(character.Name, StringComparison.Ordinal)
            );
            if (!appearedBefore)
                return true; // 任一角色首现即命中
        }
        return false;
    }

    private static bool IsForeshadowingRevealChapter(DiagnosisInput input)
    {
        var orderById = input.Chapters.ToDictionary(c => c.Id, c => c.Order);
        foreach (var plotline in input.Novel.Plotlines)
        {
            if (plotline.ResolvedAtChapterId is not null || plotline.PlannedRevealChapterId is null)
                continue;
            if (
                orderById.TryGetValue(plotline.PlannedRevealChapterId, out var plannedOrder)
                && plannedOrder == input.ChapterOrder
            )
                return true;
        }
        return false;
    }

    private bool IsInfoGapSuspiciousDrop(DiagnosisInput input)
    {
        if (WritingProfile.ScanInfoAsymmetryCount(input.CurrentContent) != 0)
            return false;
        return input.PreviousContent is not null
            && WritingProfile.ScanInfoAsymmetryCount(input.PreviousContent) > 0;
    }

    // ── 工具 ──

    private CancellationTokenSource Linked(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds)));
        return cts;
    }

    // ── 能力目录（探活快照）消费：无快照（未探活/旧服务端）时全部 fail-open，行为与接线前一致 ──

    /// <summary>服务端是否声明了该能力；未声明 = 调用必然被拒 → 跳过。无快照视为已声明。</summary>
    private bool ServerAdvertises(string capability) =>
        capabilityCatalog?.Current is not { } catalog || catalog.IsAdvertised(capability);

    /// <summary>服务端是否接受该深度（无快照 / 未声明 supported-depths → true）。</summary>
    private bool ServerSupportsDepth(string capability, string depth) =>
        capabilityCatalog?.Current is not { } catalog || catalog.SupportsDepth(capability, depth);

    /// <summary>服务端对该能力声明的数据范围是否含 <paramref name="scope"/>（无快照 / 未声明 scope → true）。</summary>
    private bool ServerAllowsScope(string capability, string scope) =>
        capabilityCatalog?.Current is not { } catalog || catalog.AllowsScope(capability, scope);

    /// <summary>跳过未声明能力（客户端侧原因，非服务端错误）：留降级码与日志。</summary>
    private void RecordNotAdvertised(string label)
    {
        LastDegradeCode = "capability_not_advertised";
        LogDegraded($"{label} 未在服务端能力目录中声明，跳过");
    }

    private HarnessRequest BuildRequest(
        string capability,
        DiagnosisInput input,
        IReadOnlyList<Chapter> scheduledChapters
    )
    {
        // P-1：章节排序由调用方（RunAsync）按轮计算一次后传入，避免每个能力各排一次
        var chapters = scheduledChapters;
        var target =
            chapters.FirstOrDefault(c => c.Id == input.ChapterId)
            ?? new Chapter
            {
                Id = input.ChapterId,
                Title = input.ChapterTitle,
                Order = input.ChapterOrder,
                Content = input.CurrentContent,
            };

        // Phase 5.1：场景规划/故事检查挂设定图（Context.Setup）——计划符合度、回溯连续性与
        // 设定接地依赖它；context.retrieve 保持不挂（基础档轻量）。
        var context = Core.Cloud.HarnessContextBuilder.Build(input.Novel, input.Novel.Genre);
        if (
            capability is "fiction.scene.plan" or "fiction.story.check"
            && input.SetupDigest is { } setupDigest
        )
            context = context with { Setup = setupDigest };

        // 5.2（2026-10-18）：story.check 挂 target 之前最近 ≤20 章（升序）——连续性窗口与规则 pass
        // 的跨章上下文（服务端按章引用去重、再按 standard 20 / deep 50 截尾）。
        // 注意：这不改变 23 维分析器输入（结局窗口已改为按目标章自派生，见 HARNESS-CLOUD §17.3）。
        // 目录门禁：仅当服务端把 story.check 的数据范围声明为 chapter-window 时才发送跨章窗口
        // （声明为 target-chapter 的旧/受限服务端不收跨章载荷；无快照 → 保持既有行为）。
        if (capability == "fiction.story.check" && ServerAllowsScope(capability, "chapter-window"))
        {
            var upstream = chapters
                .Where(c => c.Order < target.Order && !string.IsNullOrWhiteSpace(c.Content))
                .OrderByDescending(c => c.Order)
                .Take(PreviousChapterWindow)
                .OrderBy(c => c.Order)
                .Select(c => new ChapterInput(c.Id, c.Order, c.Title, "previous", c.Content))
                .ToList();
            if (upstream.Count > 0)
                context = context with
                {
                    PreviousChapterMode = "recent",
                    PreviousChapters = upstream,
                };
        }

        if (string.IsNullOrWhiteSpace(target.Content))
        {
            return new HarnessRequest
            {
                NovelId = input.NovelId,
                Capability = capability,
                Locale = "zh-CN",
                Chapters = [],
                Context = context,
                Profile = Core.Cloud.WritingProfileInput.FromProfile(
                    WritingProfile.FromNovel(input.Novel)
                ),
            };
        }

        // §3.5：target 当前章 + 最近 ≤3 章上下文（context.retrieve 由 GetContextAsync 内部组装，
        // 此处仅对 scene.plan/story.check 生效）；禁止全书上传。
        var previous = chapters
            .Where(c => c.Order < target.Order && !string.IsNullOrWhiteSpace(c.Content))
            .OrderByDescending(c => c.Order)
            .Take(3)
            .OrderBy(c => c.Order)
            .Select(c => new ChapterInput(c.Id, c.Order, c.Title, "previous", c.Content))
            .ToList();

        return new HarnessRequest
        {
            NovelId = input.NovelId,
            Capability = capability,
            // 深度档 story.check 升 standard（启用 RST 修辞结构 Layer B）；基础档保持 quick。
            // 目录门禁：服务端声明 supported-depths 且不含 standard 时回退 quick（不发无效深度）。
            Depth =
                capability == "fiction.story.check"
                && ServerSupportsDepth(capability, CloudContractLiterals.DepthStandard)
                    ? CloudContractLiterals.DepthStandard
                    : CloudContractLiterals.DepthQuick,
            Locale = "zh-CN",
            SceneGoal = capability == "fiction.scene.plan" ? input.SceneGoal : null,
            Chapters =
            [
                new ChapterInput(
                    target.Id,
                    target.Order,
                    target.Title,
                    "target",
                    target.Content ?? ""
                ),
                .. previous,
            ],
            // §3.5/MED-2：题材兜底——genreHint 进 PlotHints[0]（云端 ToAnalysisRequest 以之作 Genre 缺省来源）
            Context = context,
            // 用户写作配置（题材 + 增强档位 Tone）显式上传：云端 ToAnalysisRequest 以 Profile.Genre 优先
            Profile = Core.Cloud.WritingProfileInput.FromProfile(
                WritingProfile.FromNovel(input.Novel)
            ),
            // §15.2 AnalyzerFocus：深度档 story.check 按 L2 事件选择分析器（成本 6→2 units）；
            // context.retrieve 固定 IncludeFeatures=true（§6.2：narrativeEntropy 等深特征 + signals/dimensionScores 透传）；
            // scene.plan 保持默认（null = 全量/基础档语义）。
            Options =
                capability == "fiction.context.retrieve"
                    ? new AnalysisOptionsInput(
                        IncludeFeatures: true,
                        IncludeMaterialHints: options.MaterialHintsFromHarnessEnabled
                            && !options.MaterialHintsEnabled,
                        MaterialHintMaxChars: options.MaterialHintMaxChars
                    )
                : capability == "fiction.story.check"
                    ? new AnalysisOptionsInput(AnalyzerFocus: AnalyzerFocusFor(input))
                : null,
        };
    }

    /// <summary>
    /// §15.2 触发映射（L2 事件 → 预设 AnalyzerFocus）：
    /// L2-1 伏笔回收 → threads/coherence/payoff；L2-2 新角色 → characters/network；
    /// L2-3 同章重写 / L2-4 快照 stale → 全量（null，重写与 stale 值得最完整诊断）。
    /// </summary>
    private IReadOnlyList<string>? AnalyzerFocusFor(DiagnosisInput input)
    {
        if (input.IsRewrite || input.SnapshotStale)
            return null;
        var focus = new List<string>();
        if (IsForeshadowingRevealChapter(input))
            focus.AddRange(["threads", "coherence", "payoff"]);
        if (HasNewCharacterDebut(input))
            focus.AddRange(["characters", "network"]);
        return focus.Count == 0 ? null : focus;
    }

    private static int MaxOrder(IReadOnlyList<Chapter> chapters) =>
        chapters.Count == 0 ? 0 : chapters.Max(c => c.Order);

    /// <summary>L1-1 公开默认节奏间隔（题材级节奏由云端私有基准经 scene.plan/context.retrieve 信号承载，客户端不携带任何私有题材配置）。</summary>
    private static int GenrePacingFloor(string? genre) => 3;

    /// <summary>
    /// 幂等键：章节集指纹（target + 最近 ≤3 章 previous）替代「当前章内容前 8 hex」——
    /// 前文改写（目标章未动）也产生新键；完整 SHA-256 消除 32bit 截断碰撞面。
    /// 请求携带章节 = 服务端结果的实际输入；同请求重试仍幂等命中（服务端按键去重）。
    /// </summary>
    private static string BuildIdempotencyKey(
        string stage,
        DiagnosisInput input,
        HarnessRequest request
    )
    {
        var hash = ChapterSetFingerprint.Compute(request.Chapters);
        return $"{stage}:{input.NovelId}:{input.ChapterId}:{hash}";
    }

    /// <summary>从 GetContextAsync 的快照恢复基础档解析产物（features/signals/dimensionScores）。</summary>
    private static ContextRetrieveOutcome? ParseContextRetrieveFromSnapshot(
        AgentContextSnapshot snapshot
    )
    {
        if (
            snapshot.Features is not JsonElement features
            || features.ValueKind != JsonValueKind.Object
        )
            return null;

        double score = 0;
        if (
            features.TryGetProperty("overall-score", out var scoreEl)
            && scoreEl.ValueKind == JsonValueKind.Number
        )
            score = Math.Round(scoreEl.GetDouble() * 100, 1); // 云端 0-1 → 百分制

        // 跨仓库缺口 1.5 修复：快照现已透传 signals[] + dimensionScores（§6.1），
        // 基础档 S-1（high severity signals）与 F-1~F-5（16 中文键维度分）可据此判定。
        var signals = ParseSignals(snapshot.Signals);
        var dimensionScores = ParseDimensionScores(snapshot.DimensionScores);
        var trends =
            snapshot.Features is JsonElement fe && fe.ValueKind == JsonValueKind.Object
                ? ParseTrends(fe)
                : null;
        // Q13：快照 features.absentCharacters（云端推导的最近 K 章未出场角色）透传，
        // 供灵感方向①候选优先消费（缺失时客户端回退本地扫描）。
        var absentCharacters = SignalBuilder.ParseAbsentCharactersFromFeatures(features);
        return new ContextRetrieveOutcome(
            score,
            signals,
            dimensionScores,
            trends,
            absentCharacters,
            ParseMaterialHints(snapshot.MaterialHints)
        );
    }

    /// <summary>解析 context.retrieve 的 material-hints（缺失/空 prompt-block → null）。</summary>
    private static MaterialHintsOutcome? ParseMaterialHints(object? value)
    {
        if (value is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var status = element.TryGetProperty("status", out var statusEl)
            ? statusEl.GetString() ?? ""
            : "";
        var promptBlock = element.TryGetProperty("prompt-block", out var promptEl)
            ? promptEl.GetString() ?? ""
            : "";
        return string.IsNullOrWhiteSpace(promptBlock)
            ? null
            : new MaterialHintsOutcome(status, promptBlock);
    }

    /// <summary>从快照 signals[] 解析统一信号形状（severity=high 供 S-1）。</summary>
    private static IReadOnlyList<CloudSignalItem> ParseSignals(object? signals)
    {
        if (signals is not JsonElement el || el.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<CloudSignalItem>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var dimension = item.TryGetProperty("dimension", out var d) ? d.GetString() : null;
            var severity = SignalRules.NormalizeSeverity(
                item.TryGetProperty("severity", out var se) ? se.GetString() : null
            );
            var description = item.TryGetProperty("description", out var desc)
                ? desc.GetString()
                : null;
            var suggestion = item.TryGetProperty("suggestion", out var sug)
                ? sug.GetString()
                : null;
            var impact =
                item.TryGetProperty("impact-score", out var imp)
                && imp.ValueKind == JsonValueKind.Number
                    ? imp.GetDouble()
                    : 0;
            var confidence =
                item.TryGetProperty("confidence", out var conf)
                && conf.ValueKind == JsonValueKind.Number
                    ? conf.GetDouble()
                    : 1.0;
            var suggestedAction = item.TryGetProperty("suggested-action", out var sa)
                ? sa.GetString()
                : null;
            list.Add(
                new CloudSignalItem(
                    dimension ?? "unknown",
                    severity ?? CloudContractLiterals.SeverityLow,
                    description ?? "",
                    suggestion,
                    impact,
                    confidence,
                    suggestedAction
                )
            );
        }
        return list;
    }

    /// <summary>从快照 dimensionScores 解析 16 中文键维度分（F-1~F-5 数据源）。</summary>
    private static IReadOnlyDictionary<string, double> ParseDimensionScores(object? dimensionScores)
    {
        if (dimensionScores is not JsonElement el || el.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, double>();
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number)
                map[prop.Name] = prop.Value.GetDouble();
        }
        return map;
    }

    /// <summary>从 features 解析跨章趋势摘要（§15.1，信息性透传）。</summary>
    private static IReadOnlyList<string>? ParseTrends(JsonElement features)
    {
        if (
            !features.TryGetProperty("trends", out var trends)
            || trends.ValueKind != JsonValueKind.Object
        )
            return null;
        var list = new List<string>();
        foreach (var prop in trends.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;
            var direction = prop.Value.TryGetProperty("direction", out var dir)
                ? dir.GetString()
                : null;
            var slope =
                prop.Value.TryGetProperty("slope", out var sl)
                && sl.ValueKind == JsonValueKind.Number
                    ? sl.GetDouble()
                    : 0;
            if (!string.IsNullOrEmpty(direction))
                list.Add($"[趋势] {prop.Name}:{direction}（slope={slope:F3}）");
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>
    /// 层 1（采样门槛）：实质性字符数——空白、标点、符号不计；用于拦掉退化输入
    /// （纯标点/空白章节在引擎侧只会得到「先验基准分」的伪分数，实测高于真实长文）。
    /// </summary>
    internal static int CountSubstantiveChars(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;
        var count = 0;
        foreach (var ch in content)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
                continue;
            count++;
        }
        return count;
    }

    /// <summary>C-5：记录降级错误码（CloudApiException → 稳定错误码；其余异常 → unavailable）。</summary>
    private void RecordDegrade(Exception ex) =>
        LastDegradeCode = ex is CloudApiException api ? api.Code : "unavailable";

    private void LogDegraded(string reason) =>
        logger?.LogWarning("[云端诊断] 本轮降级本地信号：{Reason}", reason);
}
