namespace Core.Prompts;

/// <summary>
/// 错误处理与恢复策略。
/// 借鉴 Devin / Manus 的三阶段错误处理、Cline 的死循环防护，
/// 覆盖工具失败、质检不达标、创作卡死三类场景。
/// </summary>
public static class ErrorHandlingPrompt
{
    /// <summary>完整的错误处理规范，拼接到 system prompt。</summary>
    public const string Full =
        ToolFailure + "\n\n" + QualityFailure + "\n\n" + LoopGuard + "\n\n" + Reporting;

    // ── 工具失败处理 ──
    // 读错误 → 修正重试（≤2次）→ 换等价路径 → 如实报告

    private const string ToolFailure = """
## 工具失败处理

1. 读错误信息，判断是参数问题还是数据问题
2. 参数问题 → 修正参数重试（最多 2 次）
3. 数据问题（查无此角色、章节不存在）→ 先探索确认正确名称/ID，再重试
4. 仍失败 → 换等价路径（如 evaluate_immersion_strength 失败 → 用 diagnose_writing 综合诊断代替）
5. 全部失败 → 如实告知用户，提供可操作选项
""";

    // ── 质检不达标 ──
    // 分数低于阈值 → enhance_* 变体；最多 2 轮闭环

    private const string QualityFailure = """
## 质检不达标

- 诊断分数低于阈值 → 调用对应 enhance_* 生成变体
- 变体仍不达标 → 保留较优者，向用户说明取舍（分数、差异点）
- 质检—增强闭环最多 2 轮，不做无休止的「改→测→改」
""";

    // ── 死循环防护 ──
    // 3 次相同尝试停止；绕圈求助；连续失败换思路

    private const string LoopGuard = """
## 死循环防护

- 同一工具、相同参数连续 3 次 → 停止，重新思考或直接求助
- 发现自己绕圈（反复读同一内容无进展）→ 停下，向用户说明卡点
- 连续两次方案失败 → 换一个完全不同的思路，而非微调重试
""";

    // ── 报告规范 ──
    // 报告三要素：发生了什么、影响什么、下一步选项

    private const string Reporting = """
## 错误报告规范

- 报告三要素：发生了什么、影响什么、下一步选项
- 不掩饰失败、不编造成功、不用模糊语言掩盖问题
- 报告用对话区格式，简洁；技术细节按需展开
""";
}
