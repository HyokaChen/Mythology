using Core.Shared;
using Core.Tools;
using Plugins.FileSystem.Tools;
using Xunit;

namespace FileSystem.Tests;

public class TodoWriteToolTests
{
    [Fact]
    public async Task ExecuteAsync_Create_ReturnsCreatedItem()
    {
        var store = new TodoStore();
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new TodoWriteParams
            {
                Action = "create",
                Subject = "测试任务",
                Description = "描述",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("已创建", text);
        Assert.Contains("测试任务", text);
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithStatus_AppliesAndEchoesStatus()
    {
        var store = new TodoStore();
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new TodoWriteParams
            {
                Action = "create",
                Subject = "重写第一章",
                Status = "in_progress",
            },
            TestContext.Current.CancellationToken
        );

        // 状态必须生效（此前被静默丢弃，任务永远停在 Pending）
        Assert.False(result.IsError);
        var item = store.List(null).Single();
        Assert.Equal(TodoStatus.InProgress, item.Status);
        // 返回文案回显状态，LLM/用户可见
        var text = GetText(result);
        Assert.Contains("已创建", text);
        Assert.Contains("InProgress", text);
    }

    [Fact]
    public async Task ExecuteAsync_Create_WithoutStatus_DefaultsToPending()
    {
        var store = new TodoStore();
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new TodoWriteParams { Action = "create", Subject = "普通任务" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var item = store.List(null).Single();
        Assert.Equal(TodoStatus.Pending, item.Status);
        // 默认态不回显 [Pending]，文案保持简洁
        var text = GetText(result);
        Assert.DoesNotContain("Pending", text);
    }

    [Fact]
    public async Task ExecuteAsync_List_ReturnsItems()
    {
        var store = new TodoStore();
        store.Create("任务1");
        store.Create("任务2");
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new TodoWriteParams { Action = "list" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("任务1", text);
        Assert.Contains("任务2", text);
    }

    [Fact]
    public async Task ExecuteAsync_Update_ModifiesItem()
    {
        var store = new TodoStore();
        var item = store.Create("旧标题");
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new TodoWriteParams
            {
                Action = "update",
                Id = item.Id,
                Subject = "新标题",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var updated = store.List(null).First(i => i.Id == item.Id);
        Assert.Equal("新标题", updated.Subject);
    }

    [Fact]
    public async Task ExecuteAsync_Delete_RemovesItem()
    {
        var store = new TodoStore();
        var item = store.Create("待删除");
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new TodoWriteParams { Action = "delete", Id = item.Id },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Empty(store.List(null));
    }

    [Fact]
    public async Task ExecuteAsync_Clear_EmptiesStore()
    {
        var store = new TodoStore();
        store.Create("任务");
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new TodoWriteParams { Action = "clear" },
            TestContext.Current.CancellationToken
        );

        Assert.Empty(store.List(null));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsError()
    {
        var store = new TodoStore();
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new TodoWriteParams { Action = "invalid" },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateNonExistent_ReturnsError()
    {
        var store = new TodoStore();
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            "call_1",
            new TodoWriteParams { Action = "update", Id = 999 },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    private static string GetText(AgentToolResult result) =>
        result.Content?.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
}
