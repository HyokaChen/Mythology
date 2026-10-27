# Changelog

本仓（Mythology 主程序：Core / Tui / Desktop / Host）的变更记录。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)；版本号取自
`Directory.Build.props` 的 `MythologyVersion`。

## [Unreleased]

### Changed
- **词法与 FTS 重建：词典变化只重分词，且 FTS 按小说分区**（`docs/lexical-retokenize-plan.md`）。
  `NovelLexicon` 改为**全局**名字注册表（JiebaNet 词表是进程单例，之前「每部小说独立词典」不成立）：
  实体名（角色/组织/地点/道具/情节线/场景）去空格、按标点分段后以 `freq=100_000` 注册——
  2 字名的最大概率路径阈值可达数千（善我 31.9、有我 2318.6），`freq=10` 会被 DAG 否决；
  书名/卷名/章名**不再注册**（描述性短语，实测注册后「转学生」9/9 → 8/9）。
  `LexicalText` 索引侧改用 `CutForSearch`（长词 + 已知子词，潮音堂书店 → 潮音/书店/潮音堂书店；真实语料 token +8.8%、索引体积 +9.5%），
  查询侧 AND 只取最佳路径、OR 含子词。
  `SqliteRetrievalStore` 把单一全局 `retrieval_chunks_fts` 拆成**每部小说一个分区**
  （视图 `chunks_p_*` + external-content FTS `fts_p_*` + `retrieval_fts_partitions` 元数据，
  `novel_id` 为空的文档落默认分区），写路径改为 C# 显式维护、启动按分区 `COUNT` 自愈；
  分区隔离了 BM25 统计（实测共用一张表时同一词分数可从 6.995 被邻书稀释到 0.000），
  `rebuild` 成本从「全库」降到「该小说」。
  新增 `IRetrievalStore.RetokenizeNovelAsync`（幂等门 + 分区 rebuild，实测真实库 76 块 30 ms、零 embedding），
  `VectorSyncService` 改为两阶段（先注册所有小说名字、再逐部同步）并删掉只为全量重建服务的 `SyncNovelAsync(novelId)`；
  文档级 `lexical_version` 列移除（版本上移到分区），`IsUpToDate` 不再因词典变化判定文档过期。
  TUI 实时路径用 per-novel 单飞 + dirty 合并的 `QueueRetokenize` 取代原来的全量重建。
  回归：Core 1522/1522；新增分区隔离、重分词幂等、搜索模式发射、词典归一化等用例。

### Added
- **`edit_entities`：设定面板全字段批量编辑工具**（NovelTools）：一次 tool call 跨小说/分卷/章节细纲/
  角色/组织/地点/情节/场景/道具/关系批量修改字段，字段白名单复用 `Core.Data.EntityFieldRegistry`
  （从 Desktop 下沉，编辑窗与工具同源），补齐 `manage_*` 的 12 处字段缺口（角色 status/tags/abilities/notes、
  关系 since/until-chapter-id、场景 item-ids、章节 title/summary/plotline-ids/character-ids/notes/volume-id、
  小说 author/target-words/target-audience/enhancement-profile/auto-diagnose/auto-enhance/completion-status）；
  章节 `order`/`content` 明确拒绝并指路 `reorder_chapters` / `write_chapter`。写入经 `INovelStore`
  （面板刷新 + 向量索引），角色 `faction-id` 与组织 `members` 双向对账；任一字段非法时整条不落库。
  同时修复 `FileNovelStore` 不落盘 `target-audience`/`enhancement-profile`/`auto-diagnose`/`auto-enhance`
  的既有缺陷（旧 `novel.yaml` 缺键走模型默认值）。回归：Core 往返 2 例、NovelTools 5 例 + 注册断言。
- **双速自进化 v1（单账本折叠架构，`CloudDiagnosis:EvolutionEnabled` 默认关）**：
  新增 `Core/Agent/TacticLedger`（唯一持久化 `<mythologyDir>/evolution/ledger.jsonl`，append-only 四种事件
  `diagnose`/`learn`/`inject`/`verdict`，扁平小写键；构造时单遍折叠派生战术统计/抑制/待结算；
  UCB1 确定性选择、Laplace 胜率、结算样本 ≥8 且胜率 <0.40 退役；坏行抛 `InvalidDataException` 带行号）。
  `WritingFlowHook` 构造器末尾追加可选 `TacticLedger? ledger`：云端诊断成功后结算本小说早于本章的
  待结算注入（复现同 code→fail；本轮 pass→pass；断更缺口 abstain 并出队）、写 diagnose
  （Decision>Action>Pass 优先级 + genre + 维度集合）、注入「[写作战术 · 历史验证]」块
  （每章至多一次、会话 ≤20 次、1200 字符预算；`<evolution 目录>/evolution-stop` 存在即停机）。
  `get_writing_stats` 新增 `tactics` 档（读账本快照，不触达存储；未装配账本返回「未启用」）。
  `CloudDiagnosisOptions` 新增 `EvolutionEnabled`（默认 false）/`MaxInjectPerSession`（默认 20）与对应配置键；
  Host/Desktop 两条装配路径注册 `TacticLedger` 单例并传入两个 `WritingFlowHook` 站点。
  回归：`TacticLedgerTests` 13 例（折叠/退役/抑制/UCB/坏行行号/键形契约）、
  hook 端到端 2 例（默认关零写入；两章复现 code → verdict fail 结算）、stats 工具 3 例。
- **桌面端自动更新（GitHub Releases 检测 + 静默下载 + 弹窗/设置页双入口安装）**：
  `src/Desktop` 新增 `UpdateCheckService`（匿名直连 `repos/HyokaChen/Mythology/releases/latest`，
  按 RID 匹配 `Mythology-Desktop-<V>-<rid>` 资产并做数值版本比较，剥 `v`/`+sha`；失败一律静默为「无更新」）、
  `UpdateState`（弹窗与设置页「关于」共用状态机：检测 → 静默下载 → sha256 校验 → Ready/Failed；
  「稍后提醒」只关弹窗；`IsBusyProbe` 忙时不弹只亮红点、回合结束补弹）与 `UpdateCheckScheduler`
  （启动 +20s 首查、此后每日东八区 03:00）；`UpdatePromptDialog` 为非模态 overlay（遮罩不拦截点击、
  Esc = 稍后提醒），设置页新增「关于」Tab（版本 + 更新卡片 + 红点），顶栏设置按钮带红点；
  Ready 后按平台启动安装器（setup.exe / dmg / deb）并经 `RequestAppExit` 退出应用。
  仓库公开前匿名 API 404 → 功能静默休眠（仅日志，不影响启动）。
  回归：`UpdateVersionTests`（比较方向）/ `UpdateAssetMapTests`（资产映射）/
  `UpdateCheckSchedulerDelayTests`（东八区 03:00）/ `UpdateCheckServiceTests`（匹配决策、404/畸形 JSON、
  下载落盘与不留 `.part`）/ `UpdateStateTests`（Phase 迁移、单飞闸、忙探针、稍后提醒、校验失败删文件）/
  `UpdatePromptDialogStructureTests`（遮罩 IsHitTestVisible、MainWindow 挂载、设置入口结构锁）。

### Fixed
- **中文词法检索 + embedding 模型版本对账**：FTS5 `unicode61` 会把整句中文当成一个 token
  （`眷恋`/`灵魂` 命中 0）。`retrieval_chunks` 新增 jieba 预分词 `tokens` 列，FTS 改为
  `tokens` + `chunk_id UNINDEXED`，旧库启动时自动回填并重建；查询按 profile 用 OR（暗线/写作）
  或 AND（一致性），单字泛词在精确车道直接跳过。同步修复 `INSERT OR REPLACE` 不触发 DELETE
  trigger 造成的 FTS stale row、`PurgeDeletedAsync` 直接 DELETE external-content FTS 的问题，
  以及无章节实体被空 chapter key 一起限流的多样性误伤。`IEmbeddingService` 新增 `ModelId`，
  落库真实模型名并纳入 `VectorSyncService.IsUpToDate`，换 embedding 模型会自动重建该小说知识域。
  每部小说使用独立 jieba 用户词典（书名/卷名/章名 + 角色/组织/地点/道具/情节线/场景名 `AddWord`），
  名称集合哈希写入 `lexical_version`；名称变化自动重建，未变化时启动不重复 re-embed。
  TUI 实时实体变更（`EnqueueIndexTaskAsync`）先刷新词典：新增/删除或名称/标题字段变化时后台重建
  该小说全部文档；正文/性格等非名称字段变化只索引变更实体。
  回归：Core 1514/1514；新增 `LexicalTextTests`、`NovelLexiconTests`、中文词法集成测试、diversity 两例。

### Changed
- **Desktop 对话加载与思考可见性**：思考正文默认展开在可折叠详情区（流式中与完成后都可见，
  点「收起」只折叠正文，标题仍在）；每轮思考独立成卡，工具/思考之间的中间正文冻结为独立
  Content 步骤，严格按 `思考 → 正文 → 工具 → 思考 → 最终正文` 的事件顺序插入，不再复用首轮思考卡
  把后续推理堆在最上方，也不会把工具前的说明留到回合末尾冒充最终答复。中间正文使用与最终气泡
  同款的 Bone 小卡（16/12 padding、Mist 1px 边框），卡内用 14/24 轻量 TextBlock 而非 Markdown
  渲染器，避免工具边界新建渲染器造成卡顿，冻结前后底色一致，不会从黄色最终卡闪成无底色中间行。
  冻结时 Trim 首尾空行，避免 TextBlock 把 `\n\n` 撑成 24px 空行。正文仅在出现非空白字符后渲染，
  流式首批换行/空格不会撑出空白 Bone 卡。中断提示改用与系统消息同一套「系统」状态行
  （Eyebrow「系统」+ 13/20 `SystemNoticeText`，SageGray），文案「⏹ 中断生成」，不再写进 Markdown
  引用块、不再在 `AgentBubble` 内嵌套第二层边框，也不再使用 Crimson 文字 + 2px 红左引线；旧会话的
  `> ⏹ 已中断` 在历史映射时还原为同一标记。13px 比旧 12px 大一档，仍小于旧 14px 红色提示。
  所有系统消息（压缩/重试/持久化故障/实体变更）共用同一排版；Desktop 新增
  `INovelStore.OnEntityChanged` 订阅，按 `Core.Data.EntityChangeSummary` 输出
  `📝 新建/修改/删除…` 系统行（字段更新带变更键，小说级变更不显示）。输入卡左内边距按 TextBox
  模板固定的 5px 内缩补偿为 11px，输入文字与正文左缘对齐。普通引用块改为透明底、无边框，
  引用语义由「▎」前缀承担。
  首个正文/步骤到达前，助手消息内显示 `spinner +「执行中」` 等待行，首个可见活动或回合结束时收起；
  左下角状态栏恢复忙碌期 `spinner + 随机古诗词`（完整诗词进 tooltip），空闲恢复 `idle`。
  回归：`ConversationRenderingTests`（思考流式/完成态、中间正文顺序与排版、空白正文隐藏、等待行渲染）、
  `StreamEventHandlerThinkingTests`（跨轮/跨工具思考独立）、
  `StreamEventHandlerContentBlockTests`（中间正文冻结/最终正文留在气泡）、
  `ConversationVmContractTests`（等待行生命周期）、`ConversationDetailStructureTests` 与
  `TuiInteractionParityTests` 结构锁。
- **会话交互格式统一**：`ask_user` 工具结果与 TUI 追加的 user message 统一为 OMP 风格
  `User answers:` + 每组一行（多选 `[a, b]`、取消 `(已取消)`、未回答 `(未回答)`/`[]`）；
  新增 `009_SessionInteractionFormat` 迁移历史 `sessions/**/*.jsonl` 中的旧 `**用户回复:**`/
  `用户回复:` 块与变体 refine/结构化 prompt 正文，迁移幂等、保留行尾并只重写实际变更行；
  变体正文注入 prompt 统一用 `<variant ...>...</variant>` 包裹，章节写入/直接采用仍保留原文。
  回归：Core 格式化与迁移、FileSystem `ask_user`、TUI 工具卡与答案追加、Desktop prompt 映射。
- **云端计费口径调整（客户端）**：客户端同步术语与配置键（`DeepDenied/DeepUnits`、
  `DeepBudgetPerSession/DeepBudgetPerNovel`），鉴权/额度类失败按一次性降级处理；
  具体计费规则与套餐策略由云端私有契约维护。
- **服务端新响应键的客户端消费（review 轴 + 设定图）**：`story.check` 顶层
  `rule-findings`/`continuity-audit`/`plan-conformance` 解析进 `StoryCheckOutcome` 并转为注入行动项
  （新码 `C-1`（连续性；conflict=rank 12，仅次于免费档高危直通）/`R-1`（规则引擎，rank 55）/
  `P-1`（计划符合度，rank 60），参与既有「按 rank 取前 3 + 证据/处方/边界」三段式；段缺席
  （quick/旧服务端）→ 既有规则与排序逐字节不变）；`setup.check` 的 `graph` 摘要与
  `outline-metrics.volumes` 解析进 `CheckSetupDetails`，在 `check_setup` 报告、TUI 设定面板与
  Desktop 设定检查窗展示（文案由 `SetupGraphInfo/VolumeOutlineEntry.Describe()` 单点提供）。
  回归：`ReviewFindingsConsumptionTests` 8 例 + 端到端注入 1 例 + 工具/面板/VM 各 2 例。
- **能力分类与时间线接线（客户端消费面补齐）**：两处跨仓接线——
  (1) `/v1/capabilities` 的 `capabilities[]` 不再只读 `contract-version`/`expires-at`：探活解析
  descriptor（`Cloud云端能力目录Store`，last-known-good）后，`DiagnosisScheduler` 按服务端声明的
  `supported-depths`/`scope`/能力清单组装请求（深度回退 quick、跨章窗口门禁、未声明能力跳过，降级码
  `capability_not_advertised`；无快照全部 fail-open）；
  (2) `Context.Timeline` 从 `Novel.TimelineEvents` 上传（此前客户端契约无该字段 → 云端
  `PlotHoleDetector` 时间线冲突检查自接线起空转）：story.check 的 `coherence` 维度自此可产出时间线冲突。
  回归：`云端能力目录SnapshotTests` 11 例、`DiagnosisSchedulerCapabilityGateTests` 4 例、
  探活写入 5 例（TUI 3 + Desktop 2）、`HarnessContextBuilder` 时间线映射 + wire 契约断言各 1 例。
- **story.check 携带前章窗口 + 结局窗口误用修复**:`DiagnosisScheduler.BuildRequest` 对
  `fiction.story.check` 填 `Context.PreviousChapters`（target 之前最近 ≤20 章、升序、role=previous），
  服务端据此加宽连续性窗口（standard 20 / deep 50）与规则 pass 的跨章上下文；`scene.plan` /
  `context.retrieve` 不挂（免费档轻量）。同时修复云端 `ExcitementEnding` 的结局检测窗口被
  「前一章」误注入的缺陷（`LastPreviousChapterText` → `LastChapterText`，语义为末章/收束章，
  云端请求不携带 → 分析器按目标章尾部自派生）——因此本项**不改变 23 维分析器分数**。
  billing 只按 `Chapters` 计，不加价。详见 `云端私有文档`
  §3.2/§4 与 云端诊断设计（历史文档，已移除）。回归：`DiagnosisSchedulerTests.FunnelRequests_StoryCheckCarriesUpstreamWindow_Only`。
- **关系提案管线升格（`scan_relationships`：全本体 + 证据闸门 + 回执）**：本体收敛到
  `Core/Data/RelationshipOntology`（15 条 `RelationshipType` 的域/值域/有向/云端两通道 token +
  20 条结构性边词汇），`SetupDigestBuilder` 的类型与有向映射改读本体（行为不变）；
  候选目录从角色扩到角色/组织/地点/道具/情节线/场景（非角色实体占区间，避免「青云宗」里的「青云」误命中），
  候选对按本体域/值域门过滤；判定候选集按类型对取本体允许的关系 + `none`/`uncertain`；
  证据闸门输出状态 + 原因码（`provider_selected_none` / `provider_requested_more_context` /
  `relation_failed_schema_check` / `below_probability_threshold` / `below_confidence_threshold` /
  `passed_configured_gates`），新增 `system-one.relations.min-confidence`（默认 0.8）与逐批回执
  `RelationshipScanDetails.Receipts`（model/tokens/耗时/成败）。仍不写库（确认后经
  `manage_relationship`）。契约见 `docs/vec.md` §2.10/§2.12 与变更记录。
  回归：`RelationshipOntologyTests`（6 例）、`RelationshipScanTests`（14 例）。
- **诊断请求挂设定图（scene.plan/story.check）**：`WritingFlowHook` 在调度命中后构建设定图摘要
  （`SetupDigestBuilder`，集合加载同 `check_setup` 工具），经 `DiagnosisInput.SetupDigest` 传给
  `DiagnosisScheduler`，后者只在 `fiction.scene.plan` / `fiction.story.check` 的 `Context.Setup` 上挂载
  （`context.retrieve` 保持轻量不挂）。服务端据此回填 `plan-conformance` / `next-beats` /
  `planned-beats`（修正为按章序匹配后首次真正生效）/ `continuity-audit` / `rule-findings`；
  旧客户端行为不变（服务端新键 additive，按 key 读）。
  跳过轮不构图（成本门控），本地读取失败静默降级（不阻断本轮诊断）。契约见
  云端诊断设计（历史文档，已移除）。回归：`DiagnosisSchedulerTests`（scene.plan/story.check 挂 / context.retrieve 不挂）、
  `WritingFlowHookCloudTests`（命中轮构图一次、幂等跳过轮不构图）。
- **TUI 总纲入口与大纲动作收敛**：新建小说表单可填写总纲并保存到 `Novel.Outline`；小说行菜单「查看总纲」打开全文阅读窗。`检查结果` 只展开最近一次报告（未运行时显示说明），`运行设定检查` 单独调用云端 `check_setup` 并把报告追加到对话；未分卷组移除没有可执行项的 `⋯`。回归覆盖总纲持久化/阅读、检查状态和节点动作。
- **新增真实终端流分析器 `scripts/analyze_tui_capture.py`（TUI 排障）**：
  把 `script` 录制的真实字节流重放成网格，逐帧统计「未设置背景的格」（这类格终端用默认底渲染）
  并输出背景字符地图与可疑序列片段。用它对用户实机录制（154×42、80+ 帧）分析结果：
  对话区 `#1F1F1F` / 面板 `#5B3A29` / 输入卡片 `#2D2424` **每帧全部有显式底色**，
  `OSC 11` 已发、24 位色 2312 次；**仅 1 格无背景**（输入卡片区 (3,29) 的 `\`）——
  即应用输出中不存在黑带，实机黑块需在「黑块可见那一刻」再做一次录制对比定位。
- **未上色格普查回归锁（TUI）**：`Frame_HasNoUnstyledCells` —— 对「面板 + 实体对话框」整帧统计
  **没有任何背景色的格子**（这类格子框架永远不会发往终端，终端用默认底渲染 ⇒ 默认底为黑即黑块）。
  实测整帧 0 个未上色格；同时字节级比对确认框架输出忠实 ⇒ 本机侧（应用 + 框架输出）无可归因缺陷，
  差异发生在终端侧（实机 Ghostty 的宽字形渲染/字体回退）。
- **终端默认底色对齐应用主题（OSC 11）——修「一个字符宽的黑块 / CJK 字间空档」**：
  逐字节解码实测：框架首帧只发 `ESC[2J` 擦屏 + 只写「变化过的格子」，
  **从不设置终端默认底色**（OSC 11 出现 0 次）⇒ 框架从未写过的格子、以及 BCE 擦除填的格子，
  取的都是**终端自己的默认底色** —— 在 Ghostty 上是黑，于是对话框/面板交界处出现
  一个字符宽的黑块、CJK 之间出现空档（数据弯引号 U+2018/U+2019 等宽字形密集处最明显）。
  仓库 `src/Host/Program.cs` 里「灰块 + 黑条」的注释记录的是同一族问题。
  修复：启动时 `OSC 11 rgb:<主题底色>` 对齐默认底（退出 `OSC 111` 复位；`/theme` 切换时同步）。
  新增 `TerminalOutputFidelityTests`：把实际发往终端的字节流解码回网格，与框架缓冲逐格比对
  （实测字符/位置完全忠实 ⇒ 排除"框架写错位置"，问题在"从未写过"）。
  回归锁 `Startup_AlignsTerminalDefaultBackground`（启动输出必须含主题底色的 OSC 11）。
- **光标改细竖线（TUI）**：用户实机「恰好一个字符宽的黑块、遮住文字、悬浮/焦点一动就出现并移动、
  点别处消失」—— 与**块光标**（DECSCUSR block）在部分终端（Ghostty）整格覆盖文字吻合；
  且这解释了为何无头 harness 永远复现不出（硬件光标不进渲染缓冲）。
  修复：TUI 初始化完成后（raw mode 已就绪）设 `AnsiCursorStyle.SteadyBar`（DECSCUSR 6，细竖线，
  占位极小、不遮字）。
  同时实测排除字形宽度：数据字形收集到 U+2018/U+2019（中文弯引号），实测均为 1 格、无加宽需求，
  Info/Warning 均未触发 ⇒ 宽度记账不是本症根因。
  回归锁 `FullRenderEmitSemanticsTests.Startup_SetsBarCursorShape`（启动输出必须含 `ESC[6 q`）。
- **格宽探测与诊断补强（TUI）**：
  ①**框架语义实测**：`TerminalApp.RequestFullRender()` 只重画框架缓冲、向终端仍只发差异格
     （140×44 屏全量请求仅新增约 6.7 KB，整屏重发应为数十 KB）⇒ **框架无法整屏重发**，
     一旦终端网格与框架缓冲分歧（宽字形记账不一致），那格黑块会永久残留。
     表征锁 `FullRenderEmitSemanticsTests`。此前"指针移动即全量重绘"的尝试已实测无效并移除。
  ②**探测解耦**：代表字形（EAW=A / 中性装饰）探测失败时，逐字形清单仍继续实测；
     只有全都没测到才回退框架默认。
  ③**探测结果可见**：探测未生效时弹 Warning（终端未应答 DSR），实测到不一致时弹 Info（含数量）。
  ④**数据自带字形**：面板实体行渲染时收集未分类非 ASCII 字形（跳过 CJK/全角/emoji 等恒定宽区块），
     落盘 `~/.mythology/tui-pending-glyphs.txt`；下次启动（UI 接管屏幕前）对其做 DSR 实测并加宽。
     这样数据里的字形（关系名 `⇔` 之类）无需人工登记也能被覆盖。
     回归锁 `TerminalWidthProbeTests`（25 例）。
- **终端格宽探测补充：封闭集字形逐个实测（TUI）**：用户实机现象是「黑块恰好一个汉字宽」= 一个宽字形该占的
  位置没被写上 → 差 1 格来自**具体字形的字体回退**（Ghostty 默认宽度模型下 EAW=A 并不加宽）。
  于是启动探测除代表字形外，还把封闭集里**框架按 1 格**的全部字形（`TerminalWidthProbe.InventoryCodePoints`，
  由 `ChromeGlyphPolicyTests` 的字形表机器生成）逐个实测，凡终端画成 2 格的一律加宽；
  结论落日志（`终端格宽探测：EAW=A 整类加宽=…，额外加宽字形=… [U+…]`），可直接回传定位。
  反向情形（框架记 2 格、终端画 1 格）无法通过 WideRuneResolver 修（该旋钮只加宽），只记录不处理。
- **终端格宽探测（TUI）**：用户实机（Ghostty + 中文环境）悬浮时黑块盖字、缺字 → 属「框架记账格宽 ≠
  终端实际占位」族。新增 `TerminalWidthProbe`：UI 接管屏幕前用 DSR（ESC[6n）实测代表字形格宽，
  生成 `TerminalRunOptions.WideRuneResolver` —— 覆盖 ①Unicode EAW=A 整类（`→ ⇔ ─ │ █ ● ★ × …`，
  含实体数据自带字形）②中文 fallback 下变宽的中性装饰字形（`▸ ✕ ▾ ❯`，逐字形实测）。
  探测失败回退框架默认（emoji 加宽），行为与本修复前一致。EAW=A 表由
  `scripts/gen-ambiguous-table.py`（Python unicodedata 16.0.0，179 区间）机器生成。
  实际终端上的探测结果需实机确认（无头环境无法验证 DSR 应答）。
  回归锁 `TerminalWidthProbeTests`（22 例：表完整性 / 判定与 Unicode 数据一致 / resolver 两模式 / 回退）。
- **修「鼠标从对话框移到背板出现黑条」= 滚动条未上色（TUI）**：框架
  `ScrollBarStyle.Default` / `ScrollViewerStyle.Default` 的 `TrackStyle`/`ThumbStyle` 都是 `null`，
  滚动条轨道/滑块格落到终端默认样式 —— 内容超长的对话框页 hover 显出滚动条时就是一条黑竖条，
  切 tab 换字段位置随之变化。修法：`PanelThemes.InjectOverlayStyles` 注入
  `ThemeStyles.BuildScrollBar` + `BuildScrollViewer`（调色板 `ScrollbarTrack`/`ScrollbarThumb`）。
  实测（真实 TerminalApp 帧）未注入：`'█' fg=none bg=#1f1f1f`、`'░' fg=none`（6/6 未上色）；
  注入后：滑块 `#6e4a36`（篝火岩）、轨道 `#1f1f1f`（夜空）。
  新增回归锁 `ScrollBarThemeLiveTests`（含「框架默认两处为 null」的静态锁）。
- **对话框 tab 条改 Pill 样式（TUI）**：用户实机「色块跟着 tab 切换搬家、点内容又消失」→ 定位为
  框架重画 TabControl strip 时宽度算错，把「tab 条底色带」画到了对话框右缘内外。
  `ThemeStyles.BuildTabControl` / `PanelThemes.DesertTabControl` 的 `StripStyle`、`TabStyle`（未选中）、
  溢出按钮底色统一改成**对话框内容面**（Pill Tab：只有选中态有填色），宽度算错也看不出来；
  新增回归锁 `TabStyles_StripBlendsWithDialogSurface`。
  实测帧（真实 TerminalApp）：`#3a3a3a[22..24] #a65e2e[25..32] #3a3a3a[33..87]`（修复前中间是 `#5b3a29[33..87]`）。
- **弹层家族主题色统一注入根视觉（TUI）**：`PanelThemes.InjectOverlayStyles` 一次性注入
  `Popup` / `OptionList` / `ListBox`（框架自带 `Select` 的列表）/ `MenuList` / `Tooltip`，
  由 `ThemeService` 在启动与切主题时应用到根视觉——框架自带的弹层（不止我们的 `AnchoredPopup`）
  从此都继承主题浮层色，不再各自用自带默认主题（中性冷灰 / 基础底 = 暖色主题里的「一块黑」）。
  新增回归锁 `OverlayStyles_CarryThemeSurfaceBackground`（五类样式都必须带浮层底色）。
- **弹层黑块修复：Popup / OptionList / Theme.PopupSurface 全部上主题底色（TUI）**：
  - 用户报障「对话框里的类型下拉、面板里的 ⋯ 菜单展开是一块黑」→ 根因：`AnchoredPopup` 从没给
    `Popup`/`OptionList` 上底色（表面透出 app 基础底 `#1F1F1F`），且框架从 scheme 推导的
    `Theme.PopupSurface` 是中性冷灰 `#1E2D37`，暖色主题里就是黑。
  - 修法：`PanelThemes.BuildPopup` / `BuildOptionList`（普通/悬停/选中三态 + 表面/边缘）在
    `AnchoredPopup` 里统一应用（下拉 / 行菜单 / 补全三处一次修好）；`Theme.PopupSurface` 在
    `ThemeRegistry` 里显式设为浮层色（Desert = 提亮夜空 `#3A3A3A`，其余主题 = token `Surface`）。
  - 回归锁（真实 App 帧）：弹层展开后该行「最长同色段」必须等于主题浮层色且 ≥ 40 格；
    测试初始化补齐生产主题注入（UiTheme + 控件样式包），此前用框架默认主题跑，测的不是生产形态。
- **TUI 大纲层级图标改走框架的图标槽（用户要求：IconResolver 按小说/卷/章固定图标）**：
  - `TreeViewStyle.IconResolver` 按 `OutlineRow.Kind` 返回 `📖 小说 / 📦 卷 / 📄 未分卷 / ⚪🟡🟢 章状态`
    （`SetUpPanel.OutlineIcon`，internal 可测），标题文本里不再拼 emoji；
    `SpaceBetweenGlyphAndText = 1`。实测帧：`→└─▾ 📖 恋爱吧！折花君 · 1 卷 · 2 章 …   ＋ ⋯`。
  - 回归锁：`OutlineTree_LevelIcons_PerKind`（四级图标 + 标题不得再出现图标字形）；
    按行类型（而非标题字形）定位节点的测试辅助 `IsVolumeNode`。
- **大纲行去掉重复图标 + 对话框页内容面加固 + 黑块守卫重写（TUI，实机截图反馈）**：
  - 一行两个 emoji：`TreeView` 默认节点图标（`TreeViewStyle.IconResolver`）叠在行首自带的 `📖／📦／⚪` 上 →
    `IconResolver` 收敛成空白 + `SpaceBetweenGlyphAndText = 0`，图标只由行模型决定。
  - `PanelThemes.CreateDialogTabs` 给每个标签页内容包一层「内容面 + 主题化滚动条」
    （与 `CreateScrollableDialogContent` 同形态）：裸 `TabControl` 页面宿主不画背景、页内滚动条用默认样式，
    内容溢出/悬停时会透出终端默认底（黑块类缺陷的已知形态）。
  - `RealFrameBlackBlockTests` 重写为**真实 App 帧**守卫：`InMemoryTerminalBackend` + 真实 `TerminalApp` +
    生产布局（分栏 / 面板 / 状态栏），断言整帧无未绘制格（新增「整行未绘制」判定），
    并覆盖「弹窗悬停 + 聚焦」「下拉弹层展开」两条用户路径。
    > 旧版离屏渲染里 `ThinSplitter` 不参与布局 → 44 行帧只有 2 行有效内容，守卫形同虚设（实测绿灯）。
- **Desktop 行上动作与 TUI 对齐：只留 `＋` / `⋯`，图标全部 Lucide（用户要求「同理 desktop 也得改」）**：
  - 大纲树节点行去掉 `详情`(PenLine) 与 `删除`(Trash) 两个并排按钮 → 行上只剩 `＋`（下一层新建）与 `⋯`；
    `⋯` 的 `ContextMenu` 里每层新增置顶「详情」（小说信息 / 卷编辑器 / 章节字段编辑窗），
    删除置底并加分隔线（`删除分卷` / `删除章节`，Trash 图标 + Crimson 描边），
    其余项（新建分卷 / 新建章节 / 本卷新建章节 / 重命名 / 上移 / 下移 / 打开正文 / 切换状态 / 上一章 /
    下一章 / 设定检查 / 世界规则 / 切换完结状态）全部配上 Lucide 线性图标（Plus / Pencil / ChevronUp /
    ChevronDown / BookOpen / RefreshCw / Search / CircleCheckBig），不再用 emoji 文本。
  - 死代码清理：`OutlineNodeDetail_OnClick` / `OutlineNodeDelete_OnClick`（行上按钮专用）删除，
    `OutlineNodeVm.CanDelete` 随之下线（删除收进菜单，确认弹窗流程不变）。
  - 测试：节点模板结构锁改为「只有 ＋ / ⋯ 两个处理器 + 模板里不得出现 emoji」；
    ⋯ 菜单断言扩到 3 条「详情」+ 全量层级动作 + `Kind="Trash"`；`CanDelete` 断言删除。
- **行上动作收敛为「＋ / ⋯」，其余进 popup（TUI，按用户实机截图反馈）**：
  - 实体行右侧原来是 `📝 📋 📜 🗑` 四个抽象小图标（终端里又小又难辨认、`🗑` 还有色差）→ 只留一个 `⋯`：
    popup = `📄 查看 / 编辑` · `📋 插入引用` · `📜 历史` · `❌ 删除`（行名点击插入引用的主入口不变）。
  - 大纲树节点右侧原来 `📄 详情 / ＋ / ❌ / ⋯` 四个按钮 → 只留 `＋`（下一层新建）与 `⋯`：
    详情置顶、删除置底都在菜单里；创建项不再与 `＋` 重复（`📦 新建分卷` / `＋ 本卷新建章节` / `📝 编辑细纲` 等去重）。
  - 弹层机制统一：`ShowActionMenu(anchor, items)`（实体行与大纲节点同一实现，`AnchoredPopup` 生命周期不变）。
  - 回归锁：节点动作标签 = `["＋","⋯"]` / `["⋯"]`；菜单含「📄 详情」、卷/章菜单含「删除」且能触发宿主事件；
    真机帧测试改为「点 ⋯ → 菜单弹出且含详情」。字形白名单移除已不再使用的 `🗑`。
- **搜索字段占满整行 + 右端内嵌图标按钮（TUI + Desktop）**：
  - TUI 工具带改用 `Grid` 的 `Star` 列（`HStack` 只给子项自然宽度 → 搜索框此前并不拉伸，用户报障）；
    右侧只剩两个图标按钮：`✕`（有内容才出现，空框时搜索框独占据整行）与上下文动作图标
    （实体分区 `➕` 新建 / 大纲分区 `🔍` 筛选；筛选命中数写进占位文案 `🔍 搜索章节标题 · 筛选中 12/48`）。
  - Desktop 工具带 = 单列 `*` Grid：TextBox 占满整行（右侧留 58px 内边距），magnifier / `✕` 图标按钮
    **内嵌在字段内**右对齐（`Button.IconButton`，无边框、pill 圆角、hover 洗色）；
    计数与「＋ 添加」移到元信息行，不再与搜索框抢宽度；magnifier 的真实动作 = 聚焦搜索框（不做假的提交）。
  - 回归锁：TUI 断言「空框时清空键隐藏 + 搜索框宽 ≥ 面板宽 − 8」；Desktop 断言字段 Grid 无并列列、
    两个图标按钮同挂字段内、`Padding` 为图标留白、`SearchIcon_OnClick` 有实现。
- **设定面板内容区布局收敛（TUI + Desktop 同构，DESIGN.md「compact / 一屏一个主动作」）**：
  - 每个分区只剩**一行工具带**：`🔍 搜索 <当前分区>` + `✕ 清空` + **唯一上下文动作**（实体分区 = `➕ 新建`；
    大纲分区 = `🔍 筛选`）。Desktop 旧「搜索行 + 标题行 + 进度卡 + 两行筛选」四段 chrome 合一；
    TUI 旧「工具栏行 + 搜索行」两行合一，占位文案随分区切换（说清搜的是哪个分区）。
  - **删除作用于不可见目标的三按钮工具栏**（TUI `📝 编辑 / ❌ 删除` 作用于「当前展开项」，未展开时静默无效）：
    编辑 / 引用 / 历史 / 删除下沉到每行行首，TUI 新增行内 `🗑`（确认弹窗不变），与 Desktop 行内 `⋯`/`❌` 对齐。
  - 大纲筛选对话框只剩「范围 + 状态」——关键字唯一入口 = 搜索框（一个条件一个输入口）；
    Desktop 删除与搜索框绑同一属性的第二个「搜索章节」输入框。
  - Desktop：tab 带由 4×2 等宽网格改为**单行可折行 pill 带**（`WrapPanel`）；进度卡并入书级摘要条
    （百分比 + 3px 细进度线）；条目卡收紧（10,7 内边距 / 4px 间距 / 状态标记改为与名字同行的 pill，省一行）；
    空态区分「还没有 X」与「未命中 X」；`清除` 按钮仅在有搜索词时出现；
    小说节点不再渲染「走向」行（与摘要条同一句，窄面板里重复两次）。
  - 清理：TUI `OnInlineCreateChapter`（树迁移后无触发点）+ `_expandedChapterId`、Desktop `ProgressText` 等死代码删除。
- **大纲 tab 迁移到 TreeView（TUI = XenoAtom `TreeView`，Desktop = Avalonia `TreeView`）+ 层级动作上树**：
  - 结构由共享行模型 `OutlineRows` 唯一决定，两端只做「行 → 树节点」映射；节点**常驻物化**，展开/折叠是控件行为（重建不丢展开态，TUI 用 `_applied*` 区分「用户改的展开态」与「我们设的」）。
  - 每个节点右侧 Always 可见的层级动作：小说 `📄 详情` · `＋`（新建分卷）· `⋯`（书级菜单：小说信息 / 设定检查 / 世界规则 / 完结状态 / 新建分卷 / 新建章节）；卷 `📄 详情` · `＋`（本卷新建章节）· `❌ 删除` · `⋯`；章 `📄 详情` · `❌ 删除` · `⋯`（打开正文 / 编辑细纲 / 重命名 / 状态 / 上下章 / 删除）。
  - **删除一律确认弹窗**（TUI `ConfirmDeleteDialog` / Desktop `ConfirmDeleteWindow`）。
  - **顶部工具栏收敛**：TUI 大纲工具栏只剩「🔍 筛选」+ 云端同步行；Desktop 工具栏只留范围/状态/搜索/同步 —— 总纲 / 检查 / 规则 / 设定检查 / 新建章节 / 新建分卷 全部上移到小说节点菜单（不再重复占位）。
  - 移除分页窗口 / 「继续加载」/ 行内新建输入条 / DocumentFlow 行菜单（TreeView 自身负责展开、滚动与可视裁剪）；Desktop `TreeViewItem.IsExpanded` 经 `Style x:DataType` 双向绑定节点 VM（Avalonia TreeView how-to 写法），外层 `ScrollViewer` 与分页续页处理器一并删除。
- **模糊搜索框移入各 tab 的内容区首行**（tab 分栏下方）：位置本身说明「搜的是当前 tab 的内容」；同一查询仍同步过滤其余 tab 与大纲章节；跨分区结果视图下线（`Core.Shared.SearchHits` 删除）。
- **设定面板模糊搜索（TUI + Desktop 同口径）**：新增 `Core.Shared.FuzzySearch`（子序列 + 忽略大小写 + 多词全中，中英文通用）作为两端唯一的搜索口径。
  - **搜索激活即切到跨分区结果视图**：分区栏与分区内容让位，表头给命中条数，命中按分区分组列出（小说 / 分卷 / 章节 / 角色 / 组织 / 地点 / 情节 / 场景 / 道具 / 关系），点条目**退出搜索并跳到该条目**（切分区；卷 / 章在大纲里展开定位 + 高亮）。
  - **TUI**：面板顶部搜索框（`🔍 搜索` + `✕` 清空，`DynamicUpdate` 订阅）；同时各 tab 列表按同一关键词过滤——过滤发生在「60 条行数上限裁剪」**之前**（第 61 条以后也搜得到），未命中显示 `（未命中「X」）` 空态行；`RefreshAsync(reload:false)` 复用实体缓存，搜索变更**零 I/O** 重建列表；`ChapterListFilter.Matches` 与大纲行构建的章节关键字过滤同样改为模糊匹配（搜索框与「🔍 筛选」共用同一关键字槽位，搜索框优先）。
  - **Desktop**：面板顶部同款搜索框（清除按钮），VM `ChapterTitleFilter` 更名 `SearchText`（实体集合 + 大纲章节 + 结果视图共用一个搜索概念）；实体 tab 集合按全量基准过滤重填（`PublishItems`/`FillCollection`，集合变更投递 UI 线程）；大纲工具栏的章节搜索框指向同一属性；结果视图 `SearchResults` + `SearchSummary`，命中点击由视图切 Tab、VM 负责展开定位。
  - 回归：Core `FuzzySearchTests` + 大纲子序列过滤；TUI 跨分区结果 + 导航 + 未命中 / 每 tab 过滤 / 上限之外命中 / 大纲章节过滤 + 真机键入路径；Desktop 跨分区结果 + VM 过滤 + 视图绑定结构断言。
- **TUI + Desktop 大纲层级重构：小说 → 卷 → 章，逐级「详情」**：行模型新增层级根行 `OutlineRowKind.Novel`（当前小说：书名 / 作者·类型·目标字数 / 总纲首行），`OutlineViewState.NovelExpanded`（默认展开）折叠时不再物化卷/章行（首屏只给「当前小说」，点开左侧箭头才展开底下的卷）。TUI：原顶部书级摘要条改为根节点行——▸/▾ 折叠 + 行尾「📄 详情」开小说信息编辑器（大纲/作者/类型/写作风格），其下仍挂书级分节（总纲正文 / 设定检查）；卷行 / 章行行尾各补「📄 详情」（卷编辑器：卷信息 + 卷纲；章节编辑器：章序 / 章名 / 细纲 / 正文），原 ＋/⋯ 结构操作与展开明细（卷纲字段、细纲 / 计划推进 / 角色）不变。Desktop：行模板新增 Novel 分块与卷/章「详情」按钮，VM 增加 `_novelExpanded` 与 Novel 切换分支（两端同一行模型，布局同构）。
- **TUI Popup 三处重复实现收敛 + SetUpPanel 拆 partial**：`MythPromptEditor`（斜杠/实体补全）/ `MythSelect`（下拉）/ `SetUpPanel`（行尾 ⋯ 菜单）各自手写一套 `Popup + OptionList` 生命周期（构造 / ItemTemplate / SelectedIndex=0 / ItemActivated 先关后派发 / Escape 关闭+焦点还原 / Closed 清理）约 150 行逐字重复——收敛到 `AnchoredPopup<TOwner>`，调用方只声明差异（方向 / 锚宽 / Tab / 锚矩形 / 附加回调 / 焦点去处）；`SetUpPanel` 3068 → 2642 行（行菜单 → `SetUpPanel.RowMenu.cs`；七类实体详情构建器 392 行 → `SetUpPanel.EntityDetails.cs`）。纯重构，行为不变，`Tui.Tests` 707/707 全绿。
- **TUI 黑块防线三层硬化（封闭集 + 数据边界 + 叠层快照）**：原守卫只禁 17 个已知易碎序列，在役字形（🔴🟡🔵👍👎🟢🟠 等 32 个）从未登记宽度，框架按 1 格记账 / 终端按 2 格渲染即黑块。
  - **白名单封闭化**：`ChromeGlyphPolicyTests` 两张宽度表（宽 95 / 窄 72，实测登记）+ 差集扫描 `ProductionSources_OnlyRegisterGlyphs`——生产源码出现表外非 ASCII 字形即红（剔除 CJK / 全角 / 假名等恒宽区块）。
  - **运行时数据边界**：小说实体名 / 章标题 / 检索片段 / LLM 选项 / 云端摘要 / 插件清单进 Markup 前统一 `ToolCallRenderer.Escape`；`EntityEditDialog` 动态标题补 `ChromeText.ForTitle`（此前唯一漏网的动态标题）。新增 `RuntimeDataBoundaryTests`（敌意载荷：标签 + VS16 + ZWJ + ❎）。
  - **弹窗叠层快照**：`DialogOverlayLayoutTests` 合成 `WindowLayer(SetUpPanel) + Dialog` 真实帧，逐行校验宽度账本（∈[视口宽, 视口宽+1]）+ 对话框右缘外面板仍可见（防整屏覆盖）。
  - **测试**：`Tui.Tests` 707/707 全绿（新增 71 例）。
- **Desktop 图标体系收口：功能字形统一 Lucide、窗口标题去 emoji**：`⋯`「更多」/ `＋`「新建章节」/ `⇅ 同步` / `⬇ 拉取` / `← →` 翻章 / `发送 ↵` / `⏹ 停止` 等操作字形改为 Lucide 1px 描边图标（与既有 `ChevronLeft` 同体系）；`AskUserWindow` / `ConfirmDeleteWindow` / `ChapterSearchWindow` / `EntityEditWindow` 标题去掉 emoji；`AgentStep.StatusIcon` 与 `SetupFindingItem.VerdictIcon` 由 emoji 字符串改为 `LucideIconKind`（判定附 `VerdictLabel` 文字标签）。会话内容里的 emoji（系统提示 / 工具摘要 / 压缩标记）属用户数据，保留。emoji 扫描由 9 文件 InlineData 扩为全量 `AllAxaml_ContainsNoEmoji`。
- **Novel 模型补 Author / TargetWords 字段**：新建小说表单与小说信息编辑器里的「作者」「目标字数」此前是死字段——`Novel` 模型无对应属性，填写即丢。现已补 `Author` / `TargetWords`，接入 `novel.yaml`（`author` / `target-words`）持久化与三个加载适配器，Desktop / TUI 创建写入均接上；编辑器可改、项目列表可读。
- **TUI 大纲页书级入口正名：「📝 编辑总纲」→「📖 小说信息」**：该入口打开的本来就是完整小说信息编辑器（标题 / 类型 / 简介 / 总纲 / 写作风格）——旧名容易误会成只能编辑总纲；入口按钮 / 注释 / `docs/ui.md §3.9` / `SetUpPanelLayoutTests` 入口锁同步，`Tui.Tests` 635/635 全绿。
- **Desktop 书摘要条入口正名：「总纲」→「小说信息」**：该按钮打开的本来就是完整小说信息编辑器（标题 / 类型 / 简介 / 总纲 等全部字段）——旧名容易误会成只能编辑总纲；提示语 / 注释 / `docs/ui.md §3.9` / `SetUpPanelViewStructureTests` 入口锁同步，`Desktop.Tests` 271/271 全绿。
- **Desktop 新建小说表单：新增「总纲」字段**：创建时即可填写全书走向（写入 `Novel.Outline`）；留空照旧，建后可在小说信息编辑器中补写。
- **Desktop 设定面板入口精简：本体概览下线 + 总纲直达小说信息编辑**：书级入口只留真正需要交互的两项。
  - **本体概览移除**：摘要条按钮 / `OntologyOverviewWindow` / `OntologyRows` 六维统计装配一并删除——
    它是对既有数据的统计投影（对象 / 状态 / 关系 / 规则 / 行动 / 计算），不承载交互；
    结构树「书级内容不入树」不变（`OutlineRows.Build(showOntology: false)`）。
  - **总纲入口收直**：`总纲` 按钮由「只读窗（窗内再点编辑）」改为直接打开小说信息编辑器
    （`EntityType.Novel`，总纲字段在其中；关闭后刷新摘要条与大纲），删除 `NovelOutlineWindow`。
  - **同步**：`docs/ui.md §3.9` Desktop 入口描述更新为两入口；`AdalineDesignSystemTests` /
    `SetUpPanelViewStructureTests` / `SetUpPanelOutlineRowsTests` 清单与断言同步；`Desktop.Tests` 271/271 全绿。
- **Desktop 顶部导航「可切换性」深化 + §Nav Header 对齐**：小说 / 会话切换器的可发现性与键盘可达性收口。
  - **切换器胶囊**：尾部补 `ChevronDown` 12px 描边字形（与文字同色，静默的「可打开选择器」信号）；
    中间簇间距 10→6 与右簇统一；三处内联 `FontSize="12"` 覆盖移除，回到 `NavPill` 的 13px 基准。
  - **tooltip 补全**：小说 / 会话 tooltip 升级为「当前小说 / 当前会话」+ 全名与快捷键（Ctrl+O / Ctrl+E）——
    `TextTrimming` 截断时仍可读全名；「设置」tooltip 补（Ctrl+P）。
  - **键盘焦点**：`Button.NavPill:focus-visible` 与 hover 同源 wash（此前按钮无任何焦点样式）。
  - **布局**：头部高度对齐 §Nav Header 64px（46px logo + 9×2 内边距）。
  - **测试**：`HeaderNavInteractionTests` 补 3 条结构锁（chevron / 全名 tooltip / 焦点 wash）。
- **Desktop 实体关联双向编辑（写穿透）+ 书级三窗（总纲 / 本体 / 检查）**：设定面板与实体编辑窗之间的「只读关联摘要」升级为可编辑、可跳转的双向通道——一次保存、一条写通道。
  - **关联 Tab（挂起编辑 → 保存写穿透）**：编辑窗新增「关联」Tab（`EntityRelationsSection`），改动挂起为待写清单，保存时与字段、成员对账统一经 `RelationWriteThrough.ApplyAllAsync` 落盘（同处一次手动写作用域，异常整体上抛）；底部细线左显「关联改动 N 项」；对端实体行点击可打开其编辑窗（返回后重建关联数据、暂存不丢）。
  - **成员对账**：组织「成员」↔ 角色 `FactionId` 双写收敛——开窗快照、保存时 diff 补齐，单侧编辑不再互相覆盖。
  - **书级三窗**：设定面板「总纲 / 本体 / 检查」三个 Flyout 平移为独立窗口（`NovelOutlineWindow` / `OntologyOverviewWindow` / `SetupCheckWindow`），检查反馈（采纳 / 忽略 / 跳转）与深链随迁；编辑总纲关闭后刷新面板书摘要。
  - **字段注册表清剿**：删除 `ReadOnlyTags` 伪字段（关联场景 / 对外关系 / 下级地点——双向编辑取代只读摘要）；章节「关系」组更名「本卷规划」；字段覆盖测试不再豁免跨实体字段。
  - **测试**：新增 `RelationWriteThroughTests`；`Desktop.Tests` 274/274 全绿。
- **Desktop 头部导航交互深化（小说/会话切换器 + 主题链路）**：收口一批「编译期不报、界面静默退化」的缺陷面。
  - **切换器键盘化 + 当前项锚点**：「你的书房」与会话管理器标记并预选「当前」项（回车即切回）；
    列表层回车打开；Esc 分层（表单 → 列表 → 关窗）。
  - **会话行可读性**：不再直显会话 Id，改为标题兜底 + 本地时间（统一经 `ToLocalTime` 归一，避免 UTC 直显）；
    两窗底部左侧补 mono 计数（`共 N 部` / `共 N 条`）。
  - **深色主题表层键**：`DesktopThemeService` 补 `SukiCardBackground` / `SukiText` / `MenuFlyout*`
    等 SukiUI / Fluent 硬编码表层键的深色翻转与浅色原值还原（此前深色下 tooltip / 菜单浮层残留浅色）。
  - **细节**：主题按钮 tooltip 随状态指向目标主题；禁用态 `Primary`/`Ghost` 降为纸面灰
    （此前禁用的「打开」仍是满填充 CTA）；吊销横幅「×」→ Lucide X；项目选择「← 返回」→ Lucide ChevronLeft。
  - **测试**：新增 `HeaderNavInteractionTests` 结构锁。
- **2026-10-04（云端计费与额度配置调整，双仓库）**：云端计费/额度配置与账本字段调整，
  客户端无需改动；细节见云端私有文档。
- **Desktop UI 对齐 DESIGN.md（Adaline）**：修两类「编译期不报、界面静默偏离」的缺陷，并统一视觉规范。
  - **悬空资源键**：`SettingsView` 上下文 Tab 的 `BrushSageMint` → `BrushSageMist`（前者不存在，
    `DynamicResource` 静默回退默认色）。
  - **控件主题跨类型误用**：`WorldRuleWindow` / `TreeWindow` 的输入框把 `AdalineComboBox`
    （`TargetType="ComboBox"`）挂在 `TextBox` 上——类型不匹配时 Avalonia 静默忽略，控件回退到
    SukiUI 默认外观；改为 `Classes="Adaline"`。
  - **emoji → Lucide 描边图标**（DESIGN.md §Imagery「图标 1px 描边、注释而非装饰」）：设定面板大纲
    工具栏（☁️📕🌐📄📦）、检查反馈（👍👎🔗）、云端登录（☁️✅）、分支树行标记（🔀←🏷）、
    菜单「📜 历史」、新建章节/分卷标题栏、帮助/项目选择窗标题。
  - **Danger 按钮去彩色填充**：`Button.Danger` 由 Crimson Specimen 填充改为描边 + hover 反转。
    依据 DESIGN.md：行动调色板只有 Forest Ink / Olive Press，Crimson 仅作 supporting accent、
    「不可作状态色」，且明令「不得引入彩色 CTA（蓝、红、亮绿）」。
  - **CTA 唯一性**：设置页账号 Tab「去登录」由 `Primary` 降为 `Ghost`，让底部「保存并生效」保持
    每屏唯一填充 CTA。
  - **规范收敛**：新增 `RadiusCardsSm` / `BrushAmberWash` token 并替换 8 处裸圆角与 1 处内联
    `#2280581C`；`FontSans` 补 CJK 回退字体栈（对齐 `TabItem` 既有做法，消除中英混排行高漂移）；
    状态栏三段 mono 文本间补 `·` 分隔；对话框根边距收敛到 `24,20,24,16`；新增
    `TextBlock.DialogTitle`；大纲行 hover 样式由 3 处实例级 `<Style>` 提为
    `Border.OutlineRow` 全局类。
- **Desktop 对话/状态栏交互深度对齐 TUI（dots 加载 + 诗词状态栏 + 系统提示）**：
  - **加载态动画**：新增 `BusySpinner` 控件（Braille 帧序列与 XenoAtom `SpinnerStyles.Dots`
    逐帧一致、80ms 节拍；多实例共享一个 DispatcherTimer，全部失活自动停表）；对话加载行
    （原 `ProgressBar`）、Agent 步骤行、状态栏三处统一为 dots 动画；步骤完成后恢复 `✓/✗` 静态符。
  - **状态栏**：运行期随机展示古诗词名句（`PoetryStore` 由 `Tui/Data` 迁至 `Core/Data` 共享，
    TUI 同步改用）；诗句按 440px 截断展示 + tooltip 全句补全；spinner 由 `IsBusy` 驱动
    （写作中或压缩中均转）；会话 / 云端 / 上下文 / 模型 / 工具各段全部可点击直达
    （会话管理器 / 登录 / `/state` / 模型快切 / 工具设置）。
  - **系统提示**：`MessageSender.System` 消息改用居中细注渲染（压缩 / 溢出重试 / 持久化故障等
    不再伪装为左右气泡）；空思考文本不再占位（`HasThinking`）。
  - **补全弹窗**：鼠标点击条目直接插入（此前仅键盘可用）；键盘导航时选中项自动滚入可视区
    （长列表不丢视野）。
  - **压缩态**：新增 `CompactStateChanged` 事件（VM → 主窗），手动 `/compact` 期间状态栏同步旋转
    （自动压缩已由运行态覆盖）。
- **Desktop 帮助窗口数据化**：`AboutDialog` 的 33 组键位/命令由 590 行重复 XAML 抽为
  `Shortcuts` / `Commands` 两张数据表 + `ItemsControl` 模板（-73% 行数，零行为变化）。

### Fixed
- **出站 Harness 请求丢版本指纹（跨仓断点）**：`ClientPlatform.FictionHarnessRequestDto.ContextDto`
  缺 `CharacterVersion` / `PlotlineVersion`，而 `HarnessContextBuilder.Build` 一直在算这两个 SHA256 指纹 →
  服务端 `novel_meta` 的版本比对/去重（HARNESS-CLOUD §14 Q12）从真实客户端**永不触发**，指纹算完即弃。
  现补两字段与映射，并在 `HttpFictionHarnessContractTests` 断言 `character-version` /
  `plotline-version` 上线（同批补 `setup` 透传断言，锁住 scene.plan/story.check 的设定图通道）。
- **压缩拒绝语义拆分——接续模式不再误报「压缩进行中」**：`CompactResult.Rejected` 一档混了两种语义——
  B4 防重入（已有压缩进行中，稍后重试可能成功）与模式互斥（接续模式不产生 compact 条目，重试永远无用）。
  旧代码对两者一律提示「压缩进行中，请稍候再试」：一旦未来新增触发源漏判模式，用户会看到完全错误的提示。
  现拆为 `RejectedInProgress` / `RejectedWrongMode`（TUI 与 Desktop 同批修改，文案分开），
  溢出路径的返回值也随之分开——前者返回 `true` 交上层重试，后者返回 `false` 停止重试并报错（什么都没做，重试无用）。
  回归：Desktop 3 例（含 compact 模式放行对照组 + 文案区分断言）+ TUI 3 例，并做过变异验证
  （把任一分支改回旧行为，对应测试即红）。上下文管理两套逻辑的完整设计见 `docs/context-management.md`。
- **修复 Desert Light 对话框未聚焦输入框的深色底带**：修复前 `Theme.DefaultLight.InputFill` 是半透明黑色
  `RGBA(0,0,0,16)`，在暖色对话框上形成图 1 中姓名/年龄输入框的暗带；图 2 中聚焦的姓名框变白、
  年龄框仍未聚焦。Desert Light 现将普通输入底设为调色板对话框面色 `DialogContentBg`，保留白色聚焦底。
- **撤回此前对该现象的 OSC 11 / 框架 emit 归因**：截图与运行时输入填充值吻合；旧探针只检查合成缓冲，
  未按真实 hover → 输入框聚焦序列证明终端输出存在 overlay z-order 缺陷。OSC 11 恢复为应用对话区底色；
  上游发射层覆绘提案（历史文档，已移除）已标记撤回，不应据此提交框架缺陷。
- **定位「汉字后一块随主题变色的黑块/白块」= 终端把汉字渲染成 1 格宽（字体问题）**：
  用 `scripts/analyze_tui_capture.py` 对用户真实录制做「宽字形续格被覆盖」检测，抓到
  `off=42093 (69,19) 覆盖了宽字形 '…' 的续格，写入 '析'` —— 应用与框架按「汉字=2 格」排版，
  终端实际按 1 格渲染 ⇒ 每个汉字后留 1 格空位，那格显示应用底色（暗主题=夜空 #1F1F1F 看着黑、
  浅主题=暖奶油 #FBF1C7 看着白，所以它**跟随主题变色**）。同时解释了「恰好一个字符宽」
  「汉字之间有空档」「在文字后面」等全部现象，也解释了为何本机侧一切测量都干净。
  这类**反向**不一致无法用 WideRuneResolver 修正（该旋钮只能加宽）⇒
  `TerminalWidthProbe` 新增 CJK 反向探测（实测 `中`/`あ` 格宽），启动即告警：
  「终端把汉字渲染成 1 格宽（字体/宽度设置问题）……请改用等宽 CJK 字体」。
  修复在终端侧：配等宽 CJK 字体（Sarasa Mono CJK / Noto Sans Mono CJK）。
- **修复实体弹窗右缘黑/白块（TUI 主分隔条底色）**：主分隔条构造时采用面板色，但 `TuiApp.ApplyTheme`
  切主题时又把 `BackgroundColor` 覆盖为 `ConversationBg`（暗色）或 `Border`（浅色）；`ThinSplitter`
  将其画成整列底色，弹窗遮挡后只露出块状残段。
  修复：构造与主题重应用均保持 `SidePanelBg` 为主分隔条背景，对比色只用于 `LineColor`。
  回归：`FullRenderEmitSemanticsTests.SplitterBars_NeverUseConversationBackground`（启动）与
  `ThemeChange_KeepsMainSplitterBackgroundOnSidePanel`（真实 TuiApp 打开 EntityEditDialog 并切换主题）；
  修复前主题切换实测底色为 `#282828`，应为主题面板色。
- **TUI 除角色 tab 外其余 tab 折叠项点不开（真机复现）**：分区宿主用 `ContentSwitcher`，它只把「活动子项」暴露给树遍历（`ChildrenCount`/`GetChild` 只含选中项）→ 非活动子树永远不执行 `AttachToApp`（`App == null`）→ app 侧绑定依赖（`Collapsible.IsExpanded → PrepareChildren/Measure`、刷新重建）在其上全部失效。症状：切到任意非首个 tab 后折叠项点得中（`IsExpanded=true`）但内容永不 Attach、条目高度不增长（像没反应）；角色 tab 正常仅因它是启动时的活动子树。修法：分区宿主改为「8 个子面板常驻 VStack + 只切 `IsVisible`」。另补实体 tab 列表 / 折叠项详情两个 ScrollViewer 的面板底色滚动条样式（默认样式只有前景色，悬停高亮时单元格背景直通终端默认底 = 黑块）。回归：新增 `SetUpPanelLivePointerTests`——真实 `TerminalApp` + `InMemoryTerminalBackend` + 鼠标事件注入，走生产输入管线（7 个 tab 逐一点击断言展开 + 内容 Attach + 高度增长；70 条目列表点第 6 条；展开后 hover 从真实帧缓冲断言无「无背景格」）。
- **TUI 实体列表鼠标滚轮无效**（关系 / 角色列表滚动条在但滚不动）：`ApplyWheelScroll` 只钳下界不钳上界——滚过底后偏移停在超界值，`ScrollViewer.ArrangeCore` 每帧把它 Clamp 回 `max`，表现为"滚轮时不时失灵"。改为按内容 extent（按当前视口宽做无限高 Measure 得到，不碰库 internal 字段）双向钳制，顶部提前返回防抖动。回归：`WheelScroll_StepsThreeRows_AndClampsAtTop` / `WheelScroll_ClampsAtBottom`。
  端到端滚轮回归：`SetUpPanelLivePointerTests.WheelOverSceneList_ScrollsDownWithinBounds` 通过真实 `TerminalApp` 输入管线向 70 项场景列表注入滚轮事件，断言偏移前进 3 行且仍在可滚范围内。
- **TUI 大纲 tab 首屏过乱**：书级分节（总纲 / 检查）默认改为都折叠，首屏只剩「书名摘要行 + 卷章树」；工具栏按钮按功能分组重排（云端/状态 → 书级开关 → 结构操作）；`📖 小说信息` 入口在展开总纲段后出现。新增 `RealFrameBlackBlockTests`：用生产控件拼出与 TuiApp 一致的根视觉（Grid + ThinSplitter + WindowLayer + 同样式 Dialog）→ `VisualSnapshotRenderer` 离屏渲染 → `CellBuffer.ToMarkupLines` 逐格样式令牌断言「无未绘制段、对话框左右外侧保留面板背景」（ANSI 流不能当黑块判据：虚拟终端不上报颜色能力，整帧无背景 SGR）。
- **TUI 图形字符宽度安全（黑块清剿）**：VS16/VS15/ZWJ 修饰序列与 BMP 争议字符（☁ ⚠ ⚔ ⛩ 🏙 🏛 🛡 🏷 ⚧ ⛓ ⬆ ⬇ 👨‍👦）在部分终端按 2 格渲染、框架按 1 格记账；弹窗覆盖写入不清除溢出伙伴格，残格呈黑块。外壳文本统一换用单码点稳定字形（☁→🌐 ⚠→❗ ⚔→💥 ⛩→🏯 🏙→🌆 🏛→🏦 🛡→🤝 🏷→🔖 ⚧→🧑 ⛓→🚧 ⬆→↑ ⬇→↓ 👨‍👦→👪），覆盖状态栏、云端登录、设定面板、待办、实体关系与会话/标签弹窗路径。
- **TUI 对话框交互收口**：主题选择弹窗补右上角关闭按钮（与 Esc 同义：恢复打开前主题）；变体审核内容改转义渲染（LLM 输出不再被当 Markup 解析）；会话列表 / 章搜索 / 删除确认标题接入 `ChromeText`（清洗易碎序列 + 按显示宽度截断）。
- **TUI 回归门**：新增 `ChromeGlyphPolicyTests`（字形宽度白名单 + 生产源码扫描）与 `ChromeTextTests`。
- **实体读路径丢字段（关系写穿透 6/25 红的根因 + 同族清剿）**：`FileNovelStore.GetCharacter`
  两个单实体重载漏映射 `FactionId` / `Weakness` / `Habits`（列表版 `GetCharacters` 完整），
  「单读 → 编辑 → 保存」会把 `faction-id` 静默写空（组织成员回读丢失）。同类审计另补齐：
  `GetFaction` / `GetLocation` 单实体重载补 `CreatedAt` / `UpdatedAt`；`GetRelationshipsViaSemantic`
  补 `SinceChapterId` / `UntilChapterId`；`GetRelationshipsByCharacter`、`FindRelationshipInDir`
  补 `CreatedAt` / `UpdatedAt`（后者另补 `SinceChapterId` / `UntilChapterId`）。
  回归全绿：RelationWriteThroughTests 25/25、Desktop.Tests 253/253、Core.Tests 1259/1259。
- **Desktop 帮助窗命令漂移**：新增回归测试，从 `ConversationViewModel.TryExecuteCommand` 的
  case 标签（IL 字符串字面量）反向校验帮助窗列出的每条斜杠命令确实可派发。现状全绿；
  该测试能拦住「帮助里写了、实际没实现」的漂移。
- **采样门槛（层 1，跨仓库）**：当前章**实质字符**数（去空白/标点/符号）低于阈值的退化/短样本
  **不产出分数、不触发人工裁决**——客户端本地门槛 `CloudDiagnosis:MinTargetContentChars`（默认 2，
  只拦退化输入：空/纯标点）；云端引擎内部实现 对零实质字符直接短路
  （`InsufficientEvidence`，不再以四维先验分冒充诊断）；能力层 `Cloud:ScenePlanMinSubstantiveChars`
  （默认 200 实质字）之下 `fiction.scene.plan` 返回 `repair-intent.action=insufficient_data`
  （`reason-code=insufficient-evidence`、`requires-human-decision=false`、`features.overall-score` 缺席），
  客户端 `WritingFlowHook` 注入一行跳过说明（不再落入 PASS 文案）。理由：引擎对无信号样本只会返回
  「先验基准分」的伪分数（实测 5 字标点 0.6576 > 219 字连贯正文 0.5938）；层 2/3 见 Notes。
- **scene.plan 三态阈值：可配 + 临时人工标定（跨仓库）**：云端 `Cloud:ScenePlanPassScore` /
  `Cloud:ScenePlanWarningScore` 取代硬编码阈值（层 1 之下另走第四态 `insufficient_data`，见上条）；
  实测（n≈20 生产级请求）普通样本（无爽点命中）可达带约 **0.54–0.66**——0.8/0.5 下 `pass` 只对
  命中充分的样本可达、普通样本恒落 warning；故按用户决策做**临时人工标定：默认 0.62 / 0.55**
  （普通带顶部可 pass、`failed` 只留给明显低于普通带的样本）；客户端
  `CloudDiagnosis:PassScoreThreshold` 70→62 与云端 pass 线对齐，
  `HumanDecisionBridge` 临界区上沿随 warning 线 0.5→0.55。正式值待语料分位数标定（W-1）；
  同族随即定标（同日）：`CloudDiagnosis:本地升级阈值` 默认值调整 与 pass 线对齐、
  本地预算 默认值调整（见下条「深度诊断额度上调」）。
- **深度诊断节流参数调整（客户端，2026-10-03）**：本地深度预算与升级阈值默认值调整，
  云端硬限流保持 `usage_limit_exceeded` 语义；具体额度由云端私有契约维护。
- **DECISION 前置过滤阈值可配**（`src/CloudDiagnosis`）：`ShouldSkipDecision` 的
  「结构化角色数 < 3」「章节序 < 5」两条硬编码阈值改由 `CloudDiagnosis:DecisionMinCharacters` /
  `CloudDiagnosis:DecisionMinChapterOrder` 提供（默认值不变），默认行为与配置解析均有测试锚定。
- **大纲 / 卷的呈现与交互重构**（契约见 `docs/ui.md §3.9`）：
  - 两端统一「一行一个主操作 + `⋯` 溢出菜单」：卷行 `＋ ⋯`、章行 `📖 ⋯`，其余结构动作进菜单
    （TUI = `Popup` + `OptionList`，Desktop = `ContextMenu`），替代原先 TUI 行上 5 / 8 个 emoji 按钮。
  - 缩进即层级：行按 `OutlineRow.Depth` 缩进（卷与书级 = 0、章 = 1），Desktop 章行缩进 18px。
  - 书级三行（总纲 / 本体概览 / 设定检查）真实可折叠，默认只展开总纲
    （此前两端都把 Novel/Ontology/Check 画成可点箭头，但 `ToggleOutlineRow` 没有对应分支 → 死控件）。
  - Desktop：卷 / 章由 Card 改为平面树行；章行补上此前完全缺失的展开 caret；空卷内给「＋ 新建章节」入口。
  - 章状态图标改为 `⚪ 草稿 / 🟡 审核 / 🟢 终稿`；`📝` 只表示「编辑」，消除同屏语义撞车。
- **创建流程**：**行内创建**（卷头 `＋` / `⋯ → 新建章节` / 空态行 `＋`，目标分组固定）——TUI 输入行插在
  目标分组末尾（组折叠时紧跟组头），Desktop 输入条停靠树脚下；`Enter` 提交**可连打**、`Esc` 取消、
  空白标题落「新章节」。工具栏 `📄 新建章节` / 新建卷仍走轻量对话框（TUI `TextInputDialog` /
  `ChapterCreateDialog`；Desktop `VolumeQuickCreateWindow` / `ChapterQuickCreateWindow`）；
  卷纲 / 细纲 / 正文等重字段仍走原完整实体编辑器（行 `⋯` 菜单）。
- **创建后定位（reveal）**：接入 `OutlineViewState.RevealVolumeId / RevealChapterId`（两端此前均未传值，
  整套 Core 能力处于死代码状态）——自动展开所在分组、加载到所在页；TUI 渲染 `✨ 刚创建`，
  Desktop 加 Amber 包边并滚动到可见；高亮一次性（下一次渲染即消费，避免「永远高亮」）。
- **云端诊断深度分级**：付费档 `fiction.story.check` 由 quick 升为 standard（启用 RST 修辞结构
  Layer B：基准/切换点/verdicts）；单次单元成本与耗时可能上升（服务端 30s 执行上限内）。
  免费档（context.retrieve / scene.plan）保持 quick。
- **书级内容移出结构树**（`docs/ui.md §3.9`）：总纲 / 本体概览 / 设定检查不再是树里的三行——
  它们是「书的属性」而非「结构」，改由顶部摘要条承载（书名 · 走向 · `N 卷` `N 章` `终稿 N` `N 字`）。
  TUI = 树首摘要行 + 工具栏三个展开开关（`📖 总纲` / `🔬 本体概览` / `🔍 检查`，
  与其余工具栏按钮同处 `WrapHStack`，窄面板折行不压缩）；Desktop = 树上方摘要条 +
  `总纲` / `本体` / `检查` 三个 Flyout（检查 Flyout 保留 👍/👎/🔗 反馈按钮）。
  `Core.Novels.OutlineRows.Build` 因此返回 `OutlineBuildResult`（行 + `BookSummary`）；
  `OutlineViewState.ExpandedSections` 随之删除（书级折叠态改由各宿主自持）。
- **大纲行排版列**（规则集中在 `Core/Novels/OutlineRowText.cs`，两端同数）：
  章行 = 状态形状 + 序号列（`01`/`12` 两位对齐，千章自然扩展）+ 标题 + 状态标签 + 字数列
  （`1,001 → 1.0k`）；卷行 = `N 章 · 字数 · 终稿 done/total`（同一括号组，空卷不出进度）。
  `OutlineRow` 新增 `CharCount` / `ProgressDone` / `ProgressTotal` 与预格式化列
  `OrderText` / `WordsText` / `ProgressLine`；`MetaLine` 不再拼接字数（字数独立成列）。
- Desktop 卷 / 章行加 `:pointerover` 底色反馈（此前整行无 hover 态）。

### Fixed
- **深度档粘滞降级收窄**（`src/CloudDiagnosis.DiagnosisScheduler`）：会话级粘滞（付费档长期停用）现在
  只对**权限/订阅类**拒绝生效（`capability_not_entitled` / `unauthorized` / `forbidden` /
  `invalid_api_key`，见 `CloudErrorMapper.IsEntitlementDenial`）；输入/契约类 4xx
  （`invalid_analysis_input` / `payload_too_large` / `contract_version_conflict` / `not_found` /
  `conflict`）改为**一次性**降级——修正前一次 422（如空正文/参数错误）会永久关掉整个会话的深度档。
  同时修正既有 403 用例的空断言（原用 isRewrite 章节，无法区分「粘滞」与「首次重写未命中 L2」）。
- **scene.plan `focus-areas` 去重**（云端 `scene.plan` 实现）：同维度多条 issue 时
  `focus-areas` 会重复（实测 `["去AI味","去AI味","爽感"]`）→ 已 `Distinct()`；A-8 建议文本不再重复。
- **删除死代码**：`CloudAnalysisTool.ParseOverallScore`（读 `features.overall-score`）无任何调用点——
  各工具壳一律消费 `dimensions.*.overall-score`（story.check 路径，百分制）。若将来需要展示
  `features` 级综合分，需按 0-1 线协议 ×100（见 云端诊断设计（历史文档，已移除）结果契约）。
- **云端分数标度（跨仓库）**：`features.overall-score` 的线协议为 **0-1**，但云端 Harness 实现
  曾直接发出百分制（`CalculateOverallScore` 的 `product×100`）——导致 ①`fiction.scene.plan` 的
  可配置三态阈值恒真（`choose_focus` / `human_decision_required` 永不触发，人工决策弹窗成为死路径）；
  ②`fiction.context.retrieve` 的低分升级判定（`< 本地升级阈值`）恒假；
  ③信号文本里的综合分出现 6213 量级。云端已按 0-1 归一（`ToWireScore`），客户端无须改动
  （三处解析器本就 ×100）。上述三条路径因此**恢复生效**：场景规划将开始产出 A-8 聚焦建议与
  人工裁决，低分且多条 high 信号时会升级付费档。
- **空正文无效云端轮次**（`src/CloudDiagnosis.DiagnosisScheduler.Decide`）：当前章正文为空时本地短路
  （不再因 L1-4 章末无钩子等命中组装空章节请求），避免白耗云端额度。
- **ACTION 信号段边界**（`src/CloudDiagnosis.SignalBuilder.FormatAction`）：证据/处方/边界/备注
  等云端自由文本先做单行化（空白折叠）再拼装——含换行的文本不再伪造出「处方:」「边界:」等
  行首标记，LLM 消费的三段式结构稳定。
- Desktop 卷「重命名（行内）」从未生效：`BeginInlineRename` 依赖 `Tag="editable-title"` 的标题块，
  而模板里没有任何一处设置该标记 → 直接 `return`。
- Desktop 卷重命名按 `Title.Split('·').Last()` 解析，卷名含 `·` 即错；改走新增的 `OutlineRow.Name`。
- TUI 章标题按 `Title[(Order.ToString().Length + 2)..]` 切片，标题不合 `"{Order}. "` 前缀约定即切错；
  同样改走 `OutlineRow.Name`。
- 章行状态图标 `📝` 与「编辑」动作图标同屏撞车。
- **云端诊断幂等键**（`src/CloudDiagnosis`）：由「当前章内容前 8 hex」改为「章节集指纹」
  （target + 最近 ≤3 章 previous）并改用完整 SHA-256——前文改写后不再复用旧语境结果，
  消除 32bit 截断碰撞面；升级后同章同文将产生一次性新云端调用（含付费档）。
- **云端诊断重复调用**（`src/CloudDiagnosis`）：成功轮不再二次拉取 `fiction.context.retrieve`
  ——直接复用免费档快照更新缓存（`ContextSnapshotCache.UpdateFromSnapshot`）；
  免费档失败时保留单独刷新兜底。
- **云端诊断本地存储**（`src/CloudDiagnosis`）：预算池/决策历史/快照缓存打开 `vectors.db` 时对齐
  Core 侧约定（WAL + `busy_timeout=30s`）——多组件共写时等待而非立即失败，
  减少静默降级造成的内存/盘分叉。
- 章序号原渲染为 `{Order}.`（与标题里已有的「第 N 章」重复）→ 改为两位对齐的独立序号列。
- TUI 摘要行标题一度用 `new Markup($"…")` 直接插值 → 绑定插值字符串处理器，把已转义的值再转义一次
  （`OutlineRows_HeadersParseMarkup` 记录过的事故形态）；改为先拼普通字符串再走 `Markup(string)`。
- 章行状态与字数原先连写（`草稿 600`）→ 补 `·` 分隔。

### Added
- **决策模式统计结构化**（`src/CloudDiagnosis`）：`DecisionHistoryEntry.AnswerKind`
  （`DecisionAnswerKind`：采纳建议 / 自定方向）由「选项身份」在记录时确定，模式统计不再依赖
  `Answer` 文本子串匹配；`decision_history` 新增 `answer_code` 列（旧库自动补列，旧行按文本回退归类）。
- **人工裁决原因码本地化**（`src/CloudDiagnosis`）：`repair-intent.reason-code`（稳定 kebab 原因码）
  优先于英文 `reason` 文本匹配——服务端文案改写不再影响本地化；`reason` 文本匹配保留为存量兜底
  （`SignalBuilder.ScenePlanOutcome.ReasonCode` + `HumanDecisionBridge.KnownReasonCodes`）。
- `OutlineRow.Name`（卷名 / 章名原始值，无「第N卷 ·」「{Order}.」前缀）。
- TUI `TextInputDialog`、`ChapterCreateDialog`；Desktop `VolumeQuickCreateWindow`、`ChapterQuickCreateWindow`。
- Desktop `ChapterMoveTargets`（「移动到卷」子菜单目标纯函数）。
- 回归测试：TUI / Desktop `RevealChapter_ExpandsGroupAndHighlightsOnce`、`ChapterMoveTargetsTests`；
  `OutlineRowsRevealTests` 钉死「章行 Depth 恒为 1」。
- **云端诊断用量对账**（`src/CloudDiagnosis`）：每 N 章（`CloudDiagnosis:UsageReconcileChapterInterval`，默认 5）
  低频拉取服务端用量摘要；深度预算告警附服务端本月余额；新增「云端额度预警」（剩余 ≤
  `CloudDiagnosis:UsageLowRemainingThreshold`，默认 100）与「云端额度已耗尽」一次性提示
  （区分额度耗尽与网络不可用）。
- **新建章插入到当前位置**：`ChapterOrdering.PlanInsert`（有锚点章 → 插其后；否则追加到目标卷末尾）
  产出「空号中点 / 末尾追加 / 需重排」三态计划；`PlanFullReorder` 在无空号时给出连续重排表 +
  哨兵预留号。四个新建入口（TUI / Desktop 的快速创建与完整编辑器）全部改走该计划。
- `INovelStore.ReorderAllChapters` / `FileNovelStore.ReorderAllChapters`：两阶段临时位搬移落盘，
  正文 `ch{Order:D3}.md` 与 `appearances` / `evolution` 分片一并迁移——分片按 id→Order 解析，
  改序不迁移即读空（既有上移/下移路径同样受益）。
- 回归测试：`ChapterOrderingTests`（插入三态 + 连续重排/哨兵预留）、
  `FileNovelStoreChapterReorderTests`（置换不丢内容、分片随序迁移、`created-at` 保留、插入槽位）

### Notes
- **scene.plan 分数为何恒定在 ≈0.60（机制 + 修复分层，W-2）**：`Analyzers/*Dimension` 的四个 quick 维度
  在「无信号」时各自返回**先验基准分**——爽感 `clamp(density/ideal×60+20)`（无命中恰 20）、代入感基准
  70、去AI味 `100−Σ权重`（无命中 100）、文笔质量各项 80 起调。四项先验经加权几何均值
  `Π((s+10)/110)^{α}` 相乘 ≈ `0.2727^¼×0.7273^¼×0.8545^¼×0.8182^¼ ≈ 0.610`——与实测
  0.5888–0.6576 一致，即**分数与内容无关**；且样本越短越容易走「无信号」路径，出现「5 字标点 0.6576
  > 219 字连贯正文 0.5938」的倒挂。修复分层（**顺序不可交换**）：
  1. **采样门槛（已落地，跨仓库）**：客户端 `CloudDiagnosis:MinTargetContentChars`（默认 2，只拦退化输入）
     → 云端引擎零实质字符短路（`InsufficientEvidence`）→ 能力层 `Cloud:ScenePlanMinSubstantiveChars`
     （默认 200 实质字）之下 scene.plan 返回 `insufficient_data`（不产分/不触发人工裁决）。
  2. **未测语义（复核后细化为「样本级不可测」，不采用逐维排除）**：逐维「无命中即未测、从乘积中剔除」
     **被否决**——长文无信号章节会因剔除先验维而分数虚高（去AI味 100 × 文笔 80 两维 ≈ 90.5 落 pass，
     而现状 0.61 落 warning）；据此只有「样本本身不可测」（短于门槛）才不产分，长文的「无信号」
     仍按低分测量（诊断规则照常报「本章无爽点」等）。逐维判别力属根因工程，待 W-1 语料再动。
     **未加对称门槛**：`context.retrieve` 仍返回四维分/综合分（仅引擎零实质字符短路）——已核实
     `SignalRules` F-5 低维预警需 ≥3 个弱维、短样本只可能「爽感」单维弱 → 不触发；是否给
     `context.retrieve` 也加同一实质字门槛（两能力共用）留待标定与产品决定。
  3. **标定（W-1，阻塞）**：`ScenePlanMinSubstantiveChars` / `ScenePlanPassScore` / `ScenePlanWarningScore`
     三者均为文档化的初始假设值，待语料分位数标定（`data/corpus` 当前无数据）。
- **本文件为新增文件**：`docs/stability-harness.md` 声明「重大变更会记录在 CHANGELOG.md 中」，此前主仓缺该文件。
- 未变更：实体 id、kebab-case YAML / JSON 线协议、分页与虚拟化铁律；章 / 卷排序契约本轮**有变**——
  `Chapter.Order` 仍是全书唯一的排序键与文件名（`ch{Order:D3}.md`），但新建章不再一律追加到全书末尾，
  而是插入到指定章之后（空号不足时先全章重排，见 Added）。
