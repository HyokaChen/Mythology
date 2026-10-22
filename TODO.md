已完成（Desktop 端五窗口 + 接线，2026-09-10）：
- VariantReviewWindow 变体审阅窗（explore 变体组：采用 / 继续修改 / 忽略）
- TreeWindow 分支树 UI（/tree：切换分支 / 分支+摘要 / 添加标签）
- CommandPaletteWindow Ctrl+K（命令面板；/commands 指向 AboutDialog 命令 Tab）
- 设置「外观」Tab（主题即时切换；账号 Tab 索引 4→5 同步）
- 模型发现按钮（连接器 Tab + 嵌入 Tab，共用 DiscoverAsync；ModelDiscoveryService 镜像 Tui 实现）

接线完成（MainWindowViewModel.CreateConversationVm，共 9 个事件）：
RequestTree / RequestMode / ExploreVariantRaised / ContextStatsChanged /
RequestCommandsDialog / RequestReopenVariants / RequestCloudColon /
RequestResume / SystemMessageRequested

排队语义：变体 refinement 在 Agent 处理中投递 Core SteeringQueue
（turn 边界消费 → SteerPromoted/MessageStart 渲染 user 气泡），不再丢弃；
View 的 Enter 守卫在 HasPendingRefinement 时放行。

测试：Desktop.Tests 63/63（新增 refinement prompt 契约 4 + 排队与 /resume 路由 2 + 模型发现分支 3）。
