namespace Core.Prompts;

/// <summary>
/// 上下文理解与探索策略。
/// 借鉴 Cursor 的广→窄检索、Devin 的多层探索、Cline 的宏观到微观，
/// 迁移到创作场景：写前必查设定，杜绝凭空编造。
/// </summary>
public static class ContextUnderstandingPrompt
{
    /// <summary>完整的上下文理解策略，拼接到 system prompt。</summary>
    public const string Full =
        ReverseThinking
        + "\n\n"
        + Clarify
        + "\n\n"
        + ExplorationOrder
        + "\n\n"
        + NoAssumeRules
        + "\n\n"
        + Termination;

    // ── 创作反向思维 ──
    // 请求是线索不是全部；先识别目标、不变量、改动边界，最小必要改动

    private const string ReverseThinking = """
## 创作反向思维

用户请求是线索，不是全部。动手前先识别三件事：
1. 真正的创作目标（用户要什么效果，不只是字面指令）
2. 已建立的叙事不变量（已发布章节、角色设定、伏笔状态、时间线、基调）
3. 改动边界

只做最小必要改动：修改点不破坏不变量；不顺手重写无关段落；能改一处不动三处。
""";

    // ── 意图澄清 ──
    // 请求模糊时先查相关设定，再按需问 1~2 个关键问题

    private const string Clarify = """
## 上下文理解与探索：意图澄清

- 用户请求模糊（「写一段打斗」「把女主改得更立体」）→ 先查相关设定，再按需问 1~2 个关键问题（谁 vs 谁、什么环境、什么走向）
- 能通过工具查到的信息 → 不劳用户重复提供
- 只有创作方向类决策（结局走向、角色命运）才问用户
""";

    // ── 探索顺序 ──
    // 全局 → 对象 → 关联 → 细节，按需递进，不无脑全量召回

    private const string ExplorationOrder = """
## 设定探索顺序（按需递进，不无脑全量）

1. 全局层：小说配置（WritingProfile）、大纲、时间线 → 当前写到哪、什么基调
2. 对象层：目标章节、目标角色 → 直接相关者
3. 关联层：角色关系、势力关系、伏笔状态（get_relationship_network / recall_foreshadowing）→ 约束条件
4. 细节层：目标场景、物品、专属术语 → 落笔所需

- 每层只取与任务相关的数据
- 一个目标多属性时并行查（一次并行 recall 多个相关实体）
- 发现上下文自相矛盾 → 以工具返回的最新数据为准（文件即真相）
""";

    // ── 假设禁止 ──
    // 不确定的设定必须查证，不编造补全

    private const string NoAssumeRules = """
## 假设禁止

- 角色名、地名、势力名、事件细节 → 必须来自工具返回值
- 不确定的设定（某角色是否在场、某伏笔是否已回收）→ 查证后再写
- 信息不足时，宁可先问用户或先写一个可确认的版本，不编造补全
""";

    // ── 探索终止 ──
    // 信息足够立即行动；同一设定查 2 次仍缺失则求助

    private const string Termination = """
## 探索终止

- 已获得完成任务的足够信息 → 停止探索，立即开始行动
- 同一设定查了 2 次仍缺失 → 明确告知用户，请求补充，不空转
- 跨章状态（角色结构排名、伏笔清单与章龄）以 [云端诊断] 信号的证据段为准；命中时不再重复
  recall_character / recall_plot 全量探索，直接进入行动
""";
}
