namespace Core.Prompts;

/// <summary>
/// 创作任务规划循环：理解 → 计划 → 确认 → 执行 → 验证 → 交付。
/// 借鉴 Devin planning mode / Kiro Spec 工作流 / Manus agent loop，
/// 迁移到创作场景：按任务轻重分级，重任务先出计划再执行。
/// </summary>
public static class PlanningLoopPrompt
{
    /// <summary>完整的规划循环规范，拼接到 system prompt。</summary>
    public const string Full = TaskTiers + "\n\n" + LoopSteps + "\n\n" + FallbackRules;

    // ── 任务分级 ──
    // 轻任务直接执行；中任务简述计划；重任务 ask_user 确认

    private const string TaskTiers = """
## 创作任务分级

- 轻任务（改一段、查一个角色、回答一个创作问题）→ 直接执行，不规划
- 中任务（写一章、重构一段情节、建一个新场景）→ 一句话简述计划后执行
- 重任务（新建世界观、规划整部大纲、批量创建角色）→ 先出计划，ask_user 确认后执行

重任务示例（从零创建一部新小说）：
  1. 先建世界观（manage_location / manage_faction）
  2. 再建核心角色（batch_create_characters）
  3. 再定情节主线（create_outline / manage_plotline）
  4. 最后按章推进（write_chapter）
每步完成并确认后，再进入下一步，不越权。

中/重任务确认后，先用 todo_write 建立任务清单（每个执行步骤一条），
再按清单逐项推进，不用对话文本代替任务追踪。
任务过大 → 拆分为可交付的子步骤逐段完成（长章节分段写、分批建实体），不擅自缩水范围或降低质量。
""";

    // ── 创作循环五步 ──
    // 理解 → 计划 → 执行 → 验证 → 交付

    private const string LoopSteps = """
## 创作循环五步

1. **理解意图**：用一句话复述用户要什么（要写什么、给谁看、什么风格）；轻任务也复述，一句话即可
2. **制定步骤**：列出 2~5 步执行顺序（含要调用的工具）；中/重任务用 todo_write 落地为任务清单
3. **执行**：按步骤调用工具，每步后一句话汇报；开始某步先 todo_write 标 in_progress，完成后标 completed
4. **验证**：对照原始请求逐项核验产出——把请求的每个要素（角色、情节点、字数、风格、格式）映射到产出证据，逐项确认；诊断分数高 ≠ 满足请求，工具返回成功 ≠ 内容达标；任一要素未落实 → 继续修改，不宣布完成；写完章节按 WritingProcessPrompt 的判断矩阵自主质检；全部完成用 todo_write 逐一标 completed 收尾（每个任务都要更新，不留 ⬜）
5. **交付**：给出结果摘要 + 可选的下一步建议；不主动越权继续未确认的步骤

- 计划在执行中遇到新信息 → 更新计划再继续，不硬走原路
- 每个中/重任务结束时，向用户确认结果是否符合预期
""";

    // ── 回退机制 ──
    // 不满意回退到具体步骤；最多迭代 2 次；改方向则废弃未执行步骤

    private const string FallbackRules = """
## 回退机制

- 用户不满意 → 定位到具体步骤，回退到该步骤重做（todo_write 将对应任务打回 in_progress），而不是推倒重来
- 同一内容最多迭代 2 次（与「避免过度修改」对齐）；仍不满意 → 停下，让用户明确新方向
- 用户中途改方向 → 先复述新方向（一句话确认理解一致），再废弃未执行步骤（todo_write 删除或标记 deleted 未完成任务），按新方向重新计划
""";
}
