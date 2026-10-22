using ClientPlatform;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Inference;
using Core.Plugins;
using Core.Tools;
using Core.Vector;
using Microsoft.Extensions.DependencyInjection;
using Plugins.NovelTools.Tools;

// 权限声明 — 编译进 DLL，受强名称签名保护
[assembly: PluginPermission(PluginPermissions.NovelRead)]
[assembly: PluginPermission(PluginPermissions.NovelWrite)]
[assembly: PluginPermission(PluginPermissions.CloudHarness)]

namespace Plugins.NovelTools;

/// <summary>
/// 小说工具插件：提供小说创作的核心工具集。
/// 包括项目管理、章节、角色、情节线、组织、地点、道具、时间线、关系网、场景、风格分析、导出、统计、漏洞检测等功能。
/// </summary>
public sealed class NovelToolsPlugin : IToolPlugin
{
    private INovelStore _store = null!;
    private Agent? _agent;
    private RetrievalKernel? _kernel;
    private IFictionHarnessClient? _cloudHarness;
    private ISystemOneClient? _systemOne;
    private RetrievalRestClient? _retrievalRest;
    private KnowledgeRestClient? _knowledgeRest;
    private CrawlRestClient? _crawlRest;

    /// <summary>双速自进化账本（单账本折叠；未装配 → 统计工具 tactics 档返回「未启用」）。</summary>
    private TacticLedger? _ledger;

    public void Initialize(IServiceProvider sp)
    {
        _store = sp.GetRequiredService<INovelStore>();
        _agent = sp.GetService<Agent>();
        _kernel = sp.GetService<RetrievalKernel>();
        _cloudHarness = sp.GetService<IFictionHarnessClient>();
        // 受限服务提供器（非 Builtin 信任级）不映射 ISystemOneClient → null，工具按可空降级
        _systemOne = sp.GetService<ISystemOneClient>();
        _retrievalRest = sp.GetService<RetrievalRestClient>();
        _knowledgeRest = sp.GetService<KnowledgeRestClient>();
        _crawlRest = sp.GetService<CrawlRestClient>();
        _ledger = sp.GetService<TacticLedger>();
    }

    public IEnumerable<AgentTool> GetTools()
    {
        // ════════════════════════════════════════════════
        // 4.2.1 项目管理（统一 CRUD）
        // ════════════════════════════════════════════════
        yield return new ManageNovelTool(_store);
        yield return new EditEntitiesTool(_store, _kernel);
        yield return new SaveSnapshotTool(_store);
        yield return new GetProjectSummaryTool(_store);

        // ════════════════════════════════════════════════
        // 4.2.2 章节与大纲
        // ════════════════════════════════════════════════
        yield return new CreateOutlineTool(_store, _kernel);
        yield return new GetOutlineTool(_store);
        yield return new ManageVolumeTool(_store);
        yield return new ReorderChaptersTool(_store);
        yield return new WriteChapterTool(_store, _kernel);
        yield return new RewriteSelectionTool(_store, _kernel);
        yield return new EditTextTool(_store, _kernel);
        yield return new GetWordCountTool(_store);
        yield return new SetChapterStatusTool(_store);
        yield return new SuggestContinuationTool(_store);

        // ════════════════════════════════════════════════
        // 4.2.3 角色管理（统一 CRUD + 独立搜索 + 批量创建）
        // ════════════════════════════════════════════════
        yield return new ManageCharacterTool(_store, _kernel);
        yield return new BatchCreateCharactersTool(_store, _kernel);
        yield return new FindSimilarCharacterTool(_store, _kernel);

        // ════════════════════════════════════════════════
        // 4.2.4 情节线（统一 CRUD + 领域行为 + 批量创建）
        // ════════════════════════════════════════════════
        yield return new ManagePlotlineTool(_store, _kernel);
        yield return new BatchCreatePlotlinesTool(_store, _kernel);
        yield return new AdvancePlotlineTool(_store, _kernel);
        yield return new RecallForeshadowingTool(_store);
        yield return new CaptureNoteTool(_store, _kernel, _agent);
        yield return new ManageNoteTool(_store, _kernel, _agent);
        yield return new ForecastPlotTool(_store, _kernel, _cloudHarness, _agent);

        // ════════════════════════════════════════════════
        // 4.2.5 组织/门派（统一 CRUD + 组织关系 + 批量创建）
        // ════════════════════════════════════════════════
        yield return new ManageFactionTool(_store, _kernel);
        yield return new BatchCreateFactionsTool(_store, _kernel);
        yield return new UpdateFactionRelationshipTool(_store);

        // ════════════════════════════════════════════════
        // 4.2.6 地点（统一 CRUD + 语义搜索 + 批量创建）
        // ════════════════════════════════════════════════
        yield return new ManageLocationTool(_store, _kernel);
        yield return new BatchCreateLocationsTool(_store, _kernel);
        yield return new FindLocationByDescriptionTool(_store, _kernel);

        // ════════════════════════════════════════════════
        // 4.2.7 道具与时间线（统一 CRUD + 领域行为 + 批量创建）
        // ════════════════════════════════════════════════
        yield return new ManageItemTool(_store, _kernel);
        yield return new BatchCreateItemsTool(_store, _kernel);
        yield return new TransferItemTool(_store, _kernel);
        yield return new BatchCreateTimelineEventsTool(_store);
        yield return new CheckTimelineConsistencyTool(_store);

        // ════════════════════════════════════════════════
        // 4.2.9 实体演化（ENTITY-EVOLUTION：出场/变化显式登记，L0 扫描在 WriteChapter/RewriteSelection 钩子内）
        // ════════════════════════════════════════════════
        yield return new RecordAppearanceTool(_store);
        yield return new RecordEvolutionTool(_store);
        yield return new DeleteEvolutionTool(_store);
        yield return new UndoManualStateChangeTool(_store);
        yield return new ManageWorldRuleTool(_store);
        yield return new CheckWorldRulesTool(_store, _agent);

        // ════════════════════════════════════════════════
        // 4.2.8 关系网（统一 CRUD + 图查询 + 批量创建）
        // ════════════════════════════════════════════════
        yield return new ManageRelationshipTool(_store);
        yield return new BatchCreateRelationshipsTool(_store);
        yield return new GetRelationshipNetworkTool(_store);
        yield return new RelationshipScanTool(_store, _systemOne);
        yield return new ScanOntologyTool(_store, _systemOne, _agent);

        // 章节/小说删除（保留在章节域）
        yield return new DeleteChapterTool(_store);

        // 导出
        yield return new ExportChapterTool(_store);

        // ════════════════════════════════════════════════
        // Wave 11: 写作增强工具 (风格分析/场景/漏洞检测/导出/统计)
        // ════════════════════════════════════════════════

        // 4.2.9 写作风格分析
        yield return new Tools.AnalyzeStyleTool(_store, _cloudHarness);

        // 4.2.10 场景管理（统一 CRUD + 批量创建 + 构建助手）
        yield return new ManageSceneTool(_store);
        yield return new BatchCreateScenesTool(_store);
        yield return new BuildSceneTool(_store);
        yield return new PlanSceneDeltasTool(_store);

        // 4.2.11 情节漏洞检测
        yield return new DetectPlotHolesTool(_store, _cloudHarness);
        yield return new DetectInconsistenciesTool(_store);

        // CHANGE_10：设定阶段云端分析（fiction.setup.check，0 章节 + Context.Setup）
        yield return new CheckSetupTool(_store, _cloudHarness);

        // 幻觉扫描（fiction.hallucination.scan：正文 vs 设定图反漂移审计）
        yield return new ScanHallucinationTool(_store, _cloudHarness);

        // 4.2.12 小说导出
        yield return new ExportNovelTool(_store);

        // 4.2.13 写作统计
        yield return new GetWritingStatsTool(_store, _ledger);

        // ════════════════════════════════════════════════
        // Wave 12: 并行探索工具
        // ════════════════════════════════════════════════
        if (_agent is not null)
            yield return new ExploreTool(_agent, _store);

        // ════════════════════════════════════════════════
        // Wave 13: P0 商业生存线 — 代入感/爽感/弧光
        // ════════════════════════════════════════════════
        yield return new EvaluateImmersionTool(_store, _cloudHarness);
        yield return new EvaluateSatisfactionTool(_store, _cloudHarness);
        yield return new TrackCharacterArcTool(_store, _cloudHarness);

        // ════════════════════════════════════════════════
        // Wave 14: P1 作品质量线 — 冲突/开篇/节奏/描写/配角/错误/命名/力量体系
        // ════════════════════════════════════════════════
        yield return new EvaluateConflictTool(_store, _cloudHarness);
        yield return new EvaluateOpeningTool(_store, _cloudHarness);
        yield return new AnalyzePacingTool(_store, _cloudHarness);
        yield return new DetectDescriptionQualityTool(_store, _cloudHarness);
        yield return new AnalyzeSupportingCharacterTool(_store, _cloudHarness);
        yield return new DetectAvoidableMistakesTool(_store, _cloudHarness);
        yield return new CheckNamingQualityTool(_store, _cloudHarness);
        yield return new EvaluatePowerSystemTool(_store, _cloudHarness);

        // ════════════════════════════════════════════════
        // Wave 15: P2 深度创作线 — 对话质量/注水检测/伏笔/结构
        // ════════════════════════════════════════════════
        yield return new AnalyzeDialogueQualityTool(_store, _cloudHarness);
        yield return new DetectContentPaddingTool(_store, _cloudHarness);
        yield return new SuggestForeshadowingTool(_store, _cloudHarness);
        yield return new EvaluatePlotStructureTool(_store, _cloudHarness);

        // Wave 16: P2 进阶创作线 — 角色完整性/兴奋点/读者卷入/天才角色/自我感动
        // ════════════════════════════════════════════════
        yield return new CheckCharacterCompletenessTool(_store, _cloudHarness);
        yield return new EvaluateExcitementCurveTool(_store, _cloudHarness);
        yield return new EvaluateReaderEngagementTool(_store, _cloudHarness);
        yield return new AnalyzeGeniusCharacterTool(_store, _cloudHarness);
        yield return new DetectSelfIndulgenceTool(_store, _cloudHarness);

        // Wave 18: 网络层（PageRank / TextRank / 章节枢纽 / 伏笔网络）— CHANGE_04 §31
        yield return new AnalyzeNetworkTool(_store, _cloudHarness);

        // ════════════════════════════════════════════════
        // Wave 17: 云端网页检索（Draco.Sharp Retrieval REST，扣费）
        // ════════════════════════════════════════════════
        yield return new WebScrapeTool(_retrievalRest);
        yield return new WebSearchTool(_retrievalRest);
        yield return new WebMapTool(_retrievalRest);
        yield return new WebDiscoverTool(_retrievalRest);

        // ════════════════════════════════════════════════
        // Wave 19: 云端爬取任务（异步：提交/轮询/取消，每成功页计费）
        // ════════════════════════════════════════════════
        yield return new WebCrawlTool(_crawlRest);
        yield return new WebCrawlStatusTool(_crawlRest);
        yield return new WebCrawlCancelTool(_crawlRest);

        // ════════════════════════════════════════════════
        // Wave 20: 平台写作素材库（knowledge.search 按次计费；浏览/取档零价）
        // ════════════════════════════════════════════════
        yield return new KnowledgeSearchTool(_knowledgeRest);
        yield return new KnowledgeHintTool(_knowledgeRest, _store, _agent);
        yield return new KnowledgeDocumentTool(_knowledgeRest);
        yield return new KnowledgeBrowseTool(_knowledgeRest);
        yield return new KnowledgeCategoriesTool(_knowledgeRest);
    }
}
