using System.Collections.Concurrent;
using System.Text.Json;

namespace Core.Shared;

/// <summary>
/// 会话级 Todo 存储（内存），供 todo_write 工具和 TUI 读取。
/// 线程安全，ConcurrentDictionary 支持 Agent 并发访问。
/// 任何变更都会触发 <see cref="Changed"/> 事件，供 TUI 实时刷新。
/// </summary>
public sealed class TodoStore
{
    private readonly ConcurrentDictionary<string, TodoItem> _items = new(StringComparer.Ordinal);
    private int _nextId = 1;

    /// <summary>存储内容发生变更时触发（Create/Update/Delete/Clear）。事件在变更线程同步触发。</summary>
    public event Action? Changed;

    private void NotifyChanged() => Changed?.Invoke();

    /// <summary>创建一个新的 Todo 条目。</summary>
    /// <param name="subject">任务主题。</param>
    /// <param name="description">任务描述（可选）。</param>
    /// <param name="owner">任务负责人（可选）。</param>
    /// <param name="activeForm">任务的活跃形式描述（可选）。</param>
    /// <param name="blockedBy">阻塞该任务的依赖项（可选）。</param>
    /// <param name="status">初始状态（可选，默认 Pending）。LLM 常用 create + status=in_progress 一步建立进行中任务。</param>
    /// <returns>新创建的 TodoItem。</returns>
    public TodoItem Create(
        string subject,
        string? description = null,
        string? owner = null,
        string? activeForm = null,
        string[]? blockedBy = null,
        TodoStatus? status = null
    )
    {
        var id = Interlocked.Increment(ref _nextId);
        var item = new TodoItem
        {
            Id = id,
            Subject = subject,
            Description = description ?? "",
            Owner = owner ?? "",
            ActiveForm = activeForm ?? "",
            Status = status ?? TodoStatus.Pending,
            BlockedBy = blockedBy ?? [],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _items[id.ToString()] = item;
        NotifyChanged();
        return item;
    }

    /// <summary>根据 ID 获取 Todo 条目。</summary>
    /// <param name="id">Todo 条目 ID。</param>
    /// <returns>找到的 TodoItem，如果不存在则返回 null。</returns>
    public TodoItem? Get(int id) => _items.GetValueOrDefault(id.ToString());

    /// <summary>更新指定的 Todo 条目。</summary>
    /// <param name="id">Todo 条目 ID。</param>
    /// <param name="subject">新的主题（可选）。</param>
    /// <param name="description">新的描述（可选）。</param>
    /// <param name="status">新的状态字符串（可选）。</param>
    /// <param name="owner">新的负责人（可选）。</param>
    /// <param name="activeForm">新的活跃形式（可选）。</param>
    /// <param name="addBlockedBy">要添加的阻塞项（可选）。</param>
    /// <param name="removeBlockedBy">要移除的阻塞项（可选）。</param>
    /// <returns>更新后的 TodoItem，如果不存在则返回 null。</returns>
    public TodoItem? Update(
        int id,
        string? subject = null,
        string? description = null,
        string? status = null,
        string? owner = null,
        string? activeForm = null,
        string[]? addBlockedBy = null,
        string[]? removeBlockedBy = null
    )
    {
        var key = id.ToString();
        if (!_items.TryGetValue(key, out var item))
            return null;

        var updated = item with
        {
            Subject = subject ?? item.Subject,
            Description = description ?? item.Description,
            Owner = owner ?? item.Owner,
            ActiveForm = activeForm ?? item.ActiveForm,
            UpdatedAt = DateTime.UtcNow,
        };

        if (status is not null && TryParseStatus(status, out var s))
            updated = updated with { Status = s };

        if (addBlockedBy is not null)
        {
            var set = new HashSet<string>(item.BlockedBy);
            foreach (var b in addBlockedBy)
                set.Add(b);
            updated = updated with { BlockedBy = set.ToArray() };
        }

        if (removeBlockedBy is not null)
        {
            var set = new HashSet<string>(item.BlockedBy);
            foreach (var b in removeBlockedBy)
                set.Remove(b);
            updated = updated with { BlockedBy = set.ToArray() };
        }

        _items[key] = updated;
        NotifyChanged();
        return updated;
    }

    /// <summary>删除指定的 Todo 条目。</summary>
    /// <param name="id">Todo 条目 ID。</param>
    /// <returns>是否成功删除。</returns>
    public bool Delete(int id)
    {
        var removed = _items.TryRemove(id.ToString(), out _);
        if (removed)
            NotifyChanged();
        return removed;
    }

    /// <summary>列出所有 Todo 条目，可按状态筛选。</summary>
    /// <param name="filter">筛选状态（可选）。</param>
    /// <returns>Todo 条目列表。</returns>
    public IReadOnlyList<TodoItem> List(TodoStatus? filter = null)
    {
        var items = _items.Values.AsEnumerable();
        if (filter is not null)
            items = items.Where(i => i.Status == filter);
        return items.OrderBy(i => i.Id).ToList();
    }

    /// <summary>清空所有 Todo 条目。</summary>
    public void Clear()
    {
        _items.Clear();
        NotifyChanged();
    }

    /// <summary>将全部 in_progress 任务回退为 pending（用户中断/Agent 中止时调用，避免状态卡死在 🔄）。</summary>
    public void RevertInProgressToPending()
    {
        var changed = false;
        foreach (var (key, item) in _items)
        {
            if (item.Status != TodoStatus.InProgress)
                continue;
            _items[key] = item with { Status = TodoStatus.Pending, UpdatedAt = DateTime.UtcNow };
            changed = true;
        }
        if (changed)
            NotifyChanged();
    }

    /// <summary>
    /// 解析状态字符串 → 枚举。LLM 参数用 snake_case（in_progress/completed），
    /// 枚举名是 PascalCase（InProgress/Completed）——Enum.TryParse 对下划线严格匹配，
    /// 直接解析会静默失败导致状态永不生效（todo 永远停在 Pending）。
    /// 去掉下划线后忽略大小写匹配，双向兼容两种写法。
    /// </summary>
    private static bool TryParseStatus(string? status, out TodoStatus parsed)
    {
        parsed = default;
        return !string.IsNullOrEmpty(status)
            && Enum.TryParse(status.Replace("_", ""), ignoreCase: true, out parsed);
    }

    /// <summary>
    /// 将任务快照保存到 JSON 文件（按会话持久化，跨会话隔离）。
    /// 路径由调用方提供（如 ~/.mythology/todos/{sessionId}.json）。
    /// 复用 <see cref="AtomicFile.WriteAllText"/> 的 OS 级原子替换 + UTF-8 无 BOM。
    /// </summary>
    public void SaveTo(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var items = _items.Values.OrderBy(i => i.Id).ToArray();
        var json = JsonSerializer.Serialize(items, JsonHelper.JsonOptions);
        AtomicFile.WriteAllText(path, json);
    }

    /// <summary>
    /// 从 JSON 文件恢复任务快照（会话恢复时调用）。
    /// 文件不存在或损坏时返回 false，保留现有内容。
    /// </summary>
    public bool TryLoadFrom(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            var items = JsonSerializer.Deserialize<TodoItem[]>(
                AtomicFile.ReadAllText(path),
                JsonHelper.JsonOptions
            );
            if (items is null)
                return false;
            _items.Clear();
            foreach (var item in items)
            {
                _items[item.Id.ToString()] = item;
                if (item.Id >= _nextId)
                    _nextId = item.Id + 1;
            }
            NotifyChanged();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Todo 条目。</summary>
public sealed record TodoItem
{
    public int Id { get; init; }
    public string Subject { get; init; } = "";
    public string Description { get; init; } = "";
    public string Owner { get; init; } = "";
    public string ActiveForm { get; init; } = "";
    public TodoStatus Status { get; init; } = TodoStatus.Pending;
    public string[] BlockedBy { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

/// <summary>Todo 状态。</summary>
public enum TodoStatus
{
    Pending,
    InProgress,
    Completed,
    Deleted,
}
