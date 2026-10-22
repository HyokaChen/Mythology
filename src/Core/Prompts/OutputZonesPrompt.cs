namespace Core.Prompts;

/// <summary>
/// 输出分区规范：对话区与小说内容区格式互不相通。
/// 借鉴 Claude Code / NotionAI / v0 的输出边界控制，
/// 防止元注释、Emoji、Markdown 标记污染写入小说的正文。
/// </summary>
public static class OutputZonesPrompt
{
    /// <summary>完整的输出分区规范，拼接到 system prompt。</summary>
    public const string Full = Zones + "\n\n" + ContentRules + "\n\n" + DisplayRules;

    // ── 分区定义 ──
    // 对话区用 Markdown + Emoji；内容区纯文本、零元注释

    private const string Zones = """
## 输出分区（重要）

你的输出分两个区，格式互不相通：

### 对话区 — 对用户说的话
- 用 ConversationStylePrompt 规定的 Markdown + Emoji + 表格
- 用 ## / ### 分层，要点用粗体，可 skim
- 简洁有观点，不写套话

### 内容区 — 写入小说文件的文字
- 纯文本：无 Markdown 标记、无 Emoji、无标题符号、无列表符号
- 无元评论：不出现「本章」「以上」「这段文字」「让我们」等助手话语
- 无自我指涉：正文中不出现「我（助手）」
- 无占位符：不写「（此处应有描写）」「……待补充」
- 一次成型：内容尽量一次写完整，不要先写骨架再等用户催
""";

    // ── 内容区规则 ──
    // 写入类工具的内容遵守内容区格式，冲突时内容区优先

    private const string ContentRules = """
## 内容区规则

- 写入 write_chapter / rewrite_selection / manage_* 的内容遵守内容区格式
- 与内容区格式冲突时，内容区格式优先于对话区格式
- 对话区引用正文时给摘要，不全文复述
""";

    // ── 工具结果展示 ──
    // 大块数据只展示关键摘要，标注来源

    private const string DisplayRules = """
## 工具结果展示

- 工具返回的大块数据（章节全文、关系图）→ 对话区只展示关键摘要
- 摘要要标注数据来源（如「已查第 3 章，共 2400 字」）
- 不把 JSON/YAML 原文整段贴给用户，除非用户要求
""";
}
