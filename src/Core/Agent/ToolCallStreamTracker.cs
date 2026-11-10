using LlmTornado.Chat;
using LlmTornado.ChatFunctions;

namespace Core.Agent;

/// <summary>
/// 在一次 LLM 流内跟踪 tool-call 参数增量。
///
/// 职责：
/// 1. 把 provider 逐 delta 的 <see cref="FunctionCallStreamUpdate"/> 去重成一次
///    <see cref="ToolCallStreamStarted"/>；
/// 2. 生成稳定 <c>ToolCallKey</c>（CallId → Index → Name → 匿名），供
///    <see cref="ToolExecutionStart"/> / <see cref="ToolExecutionEnd"/> 关联同一张 pending 卡；
/// 3. 首个 delta 若还没拿到工具名，先不发出 Started，避免 UI 建出无法更新标题的占位卡。
///
/// provider 在不同 delta 里可能只带 CallId、只带 Index，或两者都带；本类用别名表
/// 把这些形状映射到同一个 entry，不会因为 alias 变化重复建卡。
/// 本类不推断阶段边界，只反映 provider 已经发生的参数流式回调。
/// </summary>
internal sealed class ToolCallStreamTracker
{
    private sealed class Entry
    {
        public required string Key { get; init; }
        public string? Name { get; set; }
        public int? Index { get; set; }
        public bool Started { get; set; }
        public HashSet<string> CallIds { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _byCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Entry> _byIndex = new();
    private readonly Dictionary<string, List<Entry>> _byName = new(StringComparer.Ordinal);
    private int _anonymousCounter;

    /// <summary>
    /// 观察一个参数 delta。仅在该 tool call 第一次拿到工具名、且参数流真正开始
    /// （或 provider 标记 complete）时返回 Started；后续 delta 返回 null。
    /// </summary>
    public ToolCallStreamStarted? Observe(FunctionCallStreamUpdate update)
    {
        var hasArguments =
            !string.IsNullOrEmpty(update.ArgumentsDelta)
            || !string.IsNullOrEmpty(update.ArgumentsSnapshot);

        // 并行同名调用在只带 name 的 delta 里无法可靠归属；宁可忽略这个 delta，
        // 也不要错误合并到其中一张 pending 卡。
        if (IsAmbiguousNameOnly(update))
            return null;

        var entry = Find(update);
        if (entry is null)
        {
            // 没有 CallId / Index / Name 的 delta 无法与执行事件关联，
            // 不创建匿名 entry；执行开始时仍会由 ToolExecutionStart 建卡。
            if (!HasIdentity(update))
                return null;

            entry = new Entry { Key = CreateKey(update) };
            _entries[entry.Key] = entry;
        }

        RegisterAliases(entry, update);

        if (entry.Index is null && update.Index is int index)
            entry.Index = index;

        if (string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(update.Name))
            entry.Name = update.Name;

        // 首个 delta 可能只带 CallId/Index/Name，参数增量为空。先登记身份，
        // 等真正的参数增量到达时再发 Started；否则会丢失后续只有 Index 的 delta。
        if (!hasArguments && !update.IsComplete)
            return null;

        if (entry.Started || string.IsNullOrWhiteSpace(entry.Name))
            return null;

        entry.Started = true;
        return new ToolCallStreamStarted(entry.Key, entry.Name!, update.Index ?? entry.Index);
    }

    /// <summary>
    /// 完整 <see cref="FunctionCall"/> 对应的 pending-card key。
    /// 先按 provider 字段查别名；查不到再走与流式 delta 相同的 fallback 规则。
    /// </summary>
    public string ResolveExecutionKey(FunctionCall call)
    {
        Entry? entry = null;

        var id = call.ToolCall?.Id;
        if (!string.IsNullOrWhiteSpace(id))
            _byCallId.TryGetValue(id, out entry);

        if (entry is null && call.ToolCall?.Index is int index)
            _byIndex.TryGetValue(index, out entry);

        if (entry is null && !string.IsNullOrWhiteSpace(call.Name))
            entry = FindUniqueByName(call.Name);

        return entry?.Key ?? ResolveKey(call);
    }

    private Entry? Find(FunctionCallStreamUpdate update)
    {
        if (
            !string.IsNullOrWhiteSpace(update.CallId)
            && _byCallId.TryGetValue(update.CallId, out var byId)
        )
            return byId;

        if (
            update.Index is int index
            && _byIndex.TryGetValue(index, out var byIndex)
        )
            return byIndex;

        // Name 是弱别名：同名并行调用时不能直接命中第一张卡，
        // 必须先确认 Index / CallId 不与候选 entry 冲突。
        if (!string.IsNullOrWhiteSpace(update.Name))
        {
            var byName = FindUniqueByName(update.Name);
            if (byName is not null && CanMerge(byName, update))
                return byName;
        }

        // 单调用场景下，首帧可能只有 CallId、后帧只有 Index；alias 形状变化时
        // 仍应视为同一次调用。并行调用（>1 entry）不做这个兜底，避免错误合并。
        // 即使当前只有一个 entry，也要先确认 incoming 的已知身份不与之冲突。
        if (_entries.Count == 1)
        {
            Entry? only = null;
            foreach (var candidate in _entries.Values)
            {
                only = candidate;
                break;
            }

            if (only is not null && CanMerge(only, update))
                return only;
        }

        return null;
    }

    private Entry? FindUniqueByName(string name)
    {
        if (!_byName.TryGetValue(name, out var candidates) || candidates.Count == 0)
            return null;

        // 同名并行调用没有 CallId/Index 时无法判断归属。
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private bool IsAmbiguousNameOnly(FunctionCallStreamUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.CallId) || update.Index is not null)
            return false;

        return !string.IsNullOrWhiteSpace(update.Name)
            && _byName.TryGetValue(update.Name, out var candidates)
            && candidates.Count > 1;
    }

    /// <summary>
    /// 单 entry 兜底是否安全。已知 CallId / Index / Name 任一冲突，都说明
    /// incoming 是另一个并行调用，必须新建 entry。
    /// </summary>
    private static bool CanMerge(Entry entry, FunctionCallStreamUpdate update)
    {
        if (
            update.Index is int index
            && entry.Index is int existingIndex
            && index != existingIndex
        )
            return false;

        if (
            !string.IsNullOrWhiteSpace(update.Name)
            && !string.IsNullOrWhiteSpace(entry.Name)
            && !string.Equals(update.Name, entry.Name, StringComparison.Ordinal)
        )
            return false;

        if (
            !string.IsNullOrWhiteSpace(update.CallId)
            && entry.CallIds.Count > 0
            && !entry.CallIds.Contains(update.CallId)
        )
            return false;

        return true;
    }

    private void RegisterAliases(Entry entry, FunctionCallStreamUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.CallId))
        {
            _byCallId[update.CallId] = entry;
            entry.CallIds.Add(update.CallId);
        }

        if (update.Index is int index)
            _byIndex[index] = entry;

        if (!string.IsNullOrWhiteSpace(update.Name))
        {
            if (!_byName.TryGetValue(update.Name, out var list))
            {
                list = new List<Entry>();
                _byName[update.Name] = list;
            }

            if (!list.Contains(entry))
                list.Add(entry);
        }
    }

    private static bool HasIdentity(FunctionCallStreamUpdate update) =>
        !string.IsNullOrWhiteSpace(update.CallId)
        || update.Index is not null
        || !string.IsNullOrWhiteSpace(update.Name);

    private string CreateKey(FunctionCallStreamUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.CallId))
            return update.CallId;

        if (update.Index is int index)
            return $"idx:{index}";

        if (!string.IsNullOrWhiteSpace(update.Name))
        {
            var key = $"name:{update.Name}";
            if (!_entries.ContainsKey(key))
                return key;

            return $"{key}#{_anonymousCounter++}";
        }

        return $"pending:{_anonymousCounter++}";
    }

    /// <summary>
    /// 完整 <see cref="FunctionCall"/> 的 fallback 关联键，规则必须与流式 delta 保持一致。
    /// </summary>
    internal static string ResolveKey(FunctionCall call)
    {
        var id = call.ToolCall?.Id;
        if (!string.IsNullOrWhiteSpace(id))
            return id;

        if (call.ToolCall?.Index is int index)
            return $"idx:{index}";

        var name = call.Name;
        if (!string.IsNullOrWhiteSpace(name))
            return $"name:{name}";

        return "pending:0";
    }
}
