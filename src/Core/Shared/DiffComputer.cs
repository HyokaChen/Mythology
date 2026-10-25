using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Core.Shared;

/// <summary>
/// 差异行类型枚举。TUI 与 Desktop 共用同一套差异语义。
/// </summary>
public enum DiffLineType
{
    /// <summary>未改变的上下文行。</summary>
    Context,

    /// <summary>从旧文本中删除的行。</summary>
    Delete,

    /// <summary>在新文本中插入的行。</summary>
    Insert,
}

/// <summary>
/// 差异输出中的单行，包含类型和可选行号。
/// </summary>
public readonly record struct DiffLine(
    DiffLineType Type,
    int? OldLineNum,
    int? NewLineNum,
    string Text
);

/// <summary>
/// 差异计算工具 — 使用 DiffPlex 的 Myers 差异算法计算统一差异风格的行序列。
/// 与 git 和 Codex 使用相同的 O((N+M)D) 算法；TUI 与 Desktop 共用，避免两端算法漂移。
/// </summary>
public static class DiffComputer
{
    /// <summary>
    /// 计算旧文本和新文本之间的差异列表。
    /// 相邻的 Delete+Insert 对表示"修改"的行。
    /// 同时跟踪旧行号和新行号。
    /// </summary>
    /// <param name="oldText">旧文本。</param>
    /// <param name="newText">新文本。</param>
    /// <returns>差异行列表。</returns>
    public static List<DiffLine> Compute(string oldText, string newText)
    {
        // 使用 DiffPlex 的 Myers 差异算法计算行级差异
        var diff = InlineDiffBuilder.Diff(oldText, newText);
        var result = new List<DiffLine>(diff.Lines.Count);

        var oldNum = 0;
        var newNum = 0;

        foreach (var line in diff.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Unchanged:
                    oldNum++;
                    newNum++;
                    result.Add(new DiffLine(DiffLineType.Context, oldNum, newNum, line.Text));
                    break;
                case ChangeType.Deleted:
                    oldNum++;
                    result.Add(new DiffLine(DiffLineType.Delete, oldNum, null, line.Text));
                    break;
                case ChangeType.Inserted:
                    newNum++;
                    result.Add(new DiffLine(DiffLineType.Insert, null, newNum, line.Text));
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// 统计差异中的新增和删除数量。
    /// </summary>
    /// <param name="lines">差异行列表。</param>
    /// <returns>新增和删除数量的元组。</returns>
    public static (int added, int removed) CountChanges(IReadOnlyList<DiffLine> lines)
    {
        var added = 0;
        var removed = 0;
        foreach (var line in lines)
        {
            switch (line.Type)
            {
                case DiffLineType.Insert:
                    added++;
                    break;
                case DiffLineType.Delete:
                    removed++;
                    break;
            }
        }
        return (added, removed);
    }

    /// <summary>
    /// 对完整差异输出应用 GitHub 风格的上下文折叠。
    /// 保留每个变更周围的 <paramref name="contextLines"/> 行上下文，
    /// 将连续的未变更上下文行替换为跳过标记。
    /// 确保所有差异块在 TUI / Desktop 中可见，无需滚动数百行未变更内容。
    /// </summary>
    /// <param name="lines">差异行列表。</param>
    /// <param name="contextLines">上下文行数。</param>
    /// <returns>折叠后的差异行列表。</returns>
    public static List<DiffLine> FoldContext(IReadOnlyList<DiffLine> lines, int contextLines = 5)
    {
        if (lines.Count == 0)
            return [];
        // 1. 标记变更行（Delete 或 Insert）
        var isChange = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            isChange[i] = lines[i].Type is DiffLineType.Delete or DiffLineType.Insert;

        // 2. 扩展每个变更周围的上下文
        var shouldKeep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (!isChange[i])
                continue;
            // 标记变更前的 contextLines 行
            for (var j = Math.Max(0, i - contextLines); j < i; j++)
                shouldKeep[j] = true;
            // 标记变更行本身
            shouldKeep[i] = true;
            // 标记变更后的 contextLines 行
            for (var j = i + 1; j <= Math.Min(lines.Count - 1, i + contextLines); j++)
                shouldKeep[j] = true;
        }

        // 3. 构建输出，跳过的上下文行替换为省略标记
        var result = new List<DiffLine>(lines.Count);
        var i2 = 0;
        while (i2 < lines.Count)
        {
            if (shouldKeep[i2])
            {
                result.Add(lines[i2]);
                i2++;
                continue;
            }

            // 统计连续跳过的上下文行
            var skipStart = i2;
            while (i2 < lines.Count && !shouldKeep[i2] && lines[i2].Type == DiffLineType.Context)
                i2++;

            var skipCount = i2 - skipStart;
            if (skipCount > 0)
            {
                result.Add(
                    new DiffLine(DiffLineType.Context, null, null, $"⋯ {skipCount} lines skipped ⋯")
                );
            }
        }

        return result;
    }
}
