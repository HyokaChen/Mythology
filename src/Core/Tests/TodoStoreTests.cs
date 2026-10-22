using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// TodoStore 待办事项存储测试。
/// 验证 CRUD 操作和状态管理。
/// </summary>
public class TodoStoreTests
{
    [Fact]
    /// <summary>创建应返回正确字段的待办项。</summary>
    public void Create_ReturnsItemWithCorrectFields()
    {
        var store = new TodoStore();
        var item = store.Create("写测试", "编写单元测试", "dev", "writing tests", ["1"]);

        // Interlocked.Increment(ref _nextId) starts at 1, returns 2 on first call
        Assert.Equal(2, item.Id);
        Assert.Equal("写测试", item.Subject);
        Assert.Equal("编写单元测试", item.Description);
        Assert.Equal("dev", item.Owner);
        Assert.Equal("writing tests", item.ActiveForm);
        Assert.Equal(TodoStatus.Pending, item.Status);
        Assert.Single(item.BlockedBy);
        Assert.Equal("1", item.BlockedBy[0]);
    }

    [Fact]
    /// <summary>创建应递增 ID。</summary>
    public void Create_IncrementsId()
    {
        var store = new TodoStore();
        var item1 = store.Create("任务1");
        var item2 = store.Create("任务2");

        Assert.True(item2.Id > item1.Id);
    }

    [Fact]
    /// <summary>创建时可指定初始状态（LLM 常用 create + status=in_progress 一步建立进行中任务）。</summary>
    public void Create_WithStatus_AppliesStatus()
    {
        var store = new TodoStore();
        var item = store.Create("重写第一章", status: TodoStatus.InProgress);

        Assert.Equal(TodoStatus.InProgress, item.Status);
        // 不传 status 仍默认 Pending（向后兼容）
        var pending = store.Create("普通任务");
        Assert.Equal(TodoStatus.Pending, pending.Status);
    }

    [Fact]
    /// <summary>更新状态支持 snake_case 字符串（LLM 传 in_progress/completed，枚举名是 PascalCase）。</summary>
    public void Update_WithSnakeCaseStatus_AppliesStatus()
    {
        var store = new TodoStore();
        var item = store.Create("重写第一章");

        var updated = store.Update(item.Id, status: "in_progress");
        Assert.NotNull(updated);
        Assert.Equal(TodoStatus.InProgress, updated!.Status);

        var completed = store.Update(item.Id, status: "completed");
        Assert.Equal(TodoStatus.Completed, completed!.Status);
    }

    [Fact]
    /// <summary>可选字段应使用默认值。</summary>
    public void Create_DefaultsOptionalFields()
    {
        var store = new TodoStore();
        var item = store.Create("简单任务");

        Assert.Equal("", item.Description);
        Assert.Equal("", item.Owner);
        Assert.Equal("", item.ActiveForm);
        Assert.Empty(item.BlockedBy);
        Assert.Equal(TodoStatus.Pending, item.Status);
    }

    [Fact]
    /// <summary>按 ID 查找应返回对应项。</summary>
    public void Get_ReturnsItemById()
    {
        var store = new TodoStore();
        var created = store.Create("查找我");
        var found = store.Get(created.Id);

        Assert.NotNull(found);
        Assert.Equal("查找我", found.Subject);
    }

    [Fact]
    /// <summary>不存在的 ID 应返回 null。</summary>
    public void Get_ReturnsNullForMissingId()
    {
        var store = new TodoStore();
        Assert.Null(store.Get(999));
    }

    [Fact]
    /// <summary>更新应修改标题。</summary>
    public void Update_ChangesSubject()
    {
        var store = new TodoStore();
        var item = store.Create("旧标题");
        var updated = store.Update(item.Id, subject: "新标题");

        Assert.NotNull(updated);
        Assert.Equal("新标题", updated.Subject);
    }

    [Fact]
    /// <summary>更新应修改状态。</summary>
    public void Update_ChangesStatus()
    {
        var store = new TodoStore();
        var item = store.Create("任务");
        var updated = store.Update(item.Id, status: "InProgress");

        Assert.NotNull(updated);
        Assert.Equal(TodoStatus.InProgress, updated.Status);
    }

    [Fact]
    /// <summary>状态更新应忽略大小写。</summary>
    public void Update_StatusIgnoreCase()
    {
        var store = new TodoStore();
        var item = store.Create("任务");
        var updated = store.Update(item.Id, status: "completed");

        Assert.NotNull(updated);
        Assert.Equal(TodoStatus.Completed, updated.Status);
    }

    [Fact]
    /// <summary>更新应能添加和移除依赖项。</summary>
    public void Update_AddsAndRemovesBlockedBy()
    {
        var store = new TodoStore();
        var item = store.Create("任务");
        var added = store.Update(item.Id, addBlockedBy: ["3", "5"]);
        Assert.NotNull(added);
        Assert.Equal(2, added.BlockedBy.Length);

        var removed = store.Update(item.Id, removeBlockedBy: ["3"]);
        Assert.NotNull(removed);
        Assert.Single(removed.BlockedBy);
        Assert.Equal("5", removed.BlockedBy[0]);
    }

    [Fact]
    /// <summary>更新不存在的 ID 应返回 null。</summary>
    public void Update_ReturnsNullForMissingId()
    {
        var store = new TodoStore();
        Assert.Null(store.Update(999, subject: "不存在"));
    }

    [Fact]
    /// <summary>删除应移除对应项。</summary>
    public void Delete_RemovesItem()
    {
        var store = new TodoStore();
        var item = store.Create("待删除");
        Assert.True(store.Delete(item.Id));
        Assert.Null(store.Get(item.Id));
    }

    [Fact]
    /// <summary>删除不存在的 ID 应返回 false。</summary>
    public void Delete_ReturnsFalseForMissingId()
    {
        var store = new TodoStore();
        Assert.False(store.Delete(999));
    }

    [Fact]
    /// <summary>列表应返回所有项。</summary>
    public void List_ReturnsAllItems()
    {
        var store = new TodoStore();
        store.Create("任务1");
        store.Create("任务2");
        store.Create("任务3");

        var items = store.List();
        Assert.Equal(3, items.Count);
    }

    [Fact]
    /// <summary>列表应按状态过滤。</summary>
    public void List_FiltersByStatus()
    {
        var store = new TodoStore();
        store.Create("待办1");
        store.Create("待办2");
        var item = store.Create("进行中");
        store.Update(item.Id, status: "InProgress");

        var pending = store.List(TodoStatus.Pending);
        var inProgress = store.List(TodoStatus.InProgress);

        Assert.Equal(2, pending.Count);
        Assert.Single(inProgress);
    }

    [Fact]
    /// <summary>空存储应返回空列表。</summary>
    public void List_ReturnsEmptyForEmptyStore()
    {
        var store = new TodoStore();
        Assert.Empty(store.List());
    }

    [Fact]
    /// <summary>清空应移除所有项。</summary>
    public void Clear_RemovesAllItems()
    {
        var store = new TodoStore();
        store.Create("任务1");
        store.Create("任务2");
        store.Clear();
        Assert.Empty(store.List());
    }

    [Fact]
    /// <summary>中断回退：in_progress 任务应回到 pending，其他状态不受影响。</summary>
    public void RevertInProgressToPending_ResetsInProgressOnly()
    {
        var store = new TodoStore();
        var pending = store.Create("待办");
        var running = store.Create("进行中");
        var done = store.Create("已完成");
        store.Update(running.Id, status: "InProgress");
        store.Update(done.Id, status: "Completed");

        store.RevertInProgressToPending();

        Assert.Equal(TodoStatus.Pending, store.Get(running.Id)!.Status);
        Assert.Equal(TodoStatus.Pending, store.Get(pending.Id)!.Status);
        Assert.Equal(TodoStatus.Completed, store.Get(done.Id)!.Status);
    }

    [Fact]
    /// <summary>保存后应从文件恢复完全一致的任务快照，且 ID 继续递增。</summary>
    public void SaveAndLoad_RoundTripsItems()
    {
        var store = new TodoStore();
        var item = store.Create("任务", "描述", "dev", "working", ["2"]);
        store.Update(item.Id, status: "InProgress");

        var path = Path.Combine(Path.GetTempPath(), $"todos-{Guid.NewGuid():N}.json");
        try
        {
            store.SaveTo(path);

            var restored = new TodoStore();
            Assert.True(restored.TryLoadFrom(path));
            var loaded = Assert.Single(restored.List());
            Assert.Equal(item.Id, loaded.Id);
            Assert.Equal("任务", loaded.Subject);
            Assert.Equal("描述", loaded.Description);
            Assert.Equal("dev", loaded.Owner);
            Assert.Equal("working", loaded.ActiveForm);
            Assert.Equal(TodoStatus.InProgress, loaded.Status);
            Assert.Equal(["2"], loaded.BlockedBy);

            // 恢复后新任务 ID 不应与已加载项冲突
            var next = restored.Create("新任务");
            Assert.True(next.Id > loaded.Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    /// <summary>文件不存在时加载应返回 false 且保留现有内容。</summary>
    public void TryLoadFrom_MissingFile_ReturnsFalse()
    {
        var store = new TodoStore();
        store.Create("既有任务");

        var missing = Path.Combine(Path.GetTempPath(), $"todos-{Guid.NewGuid():N}.json");
        Assert.False(store.TryLoadFrom(missing));
        Assert.Single(store.List());
    }

    [Fact]
    /// <summary>损坏的 JSON 文件应返回 false 且不崩溃。</summary>
    public void TryLoadFrom_CorruptFile_ReturnsFalse()
    {
        var store = new TodoStore();
        store.Create("既有任务");

        var path = Path.Combine(Path.GetTempPath(), $"todos-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not valid json");
            Assert.False(store.TryLoadFrom(path));
            Assert.Single(store.List());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
