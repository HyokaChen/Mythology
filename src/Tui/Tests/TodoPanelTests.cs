using Core.Shared;
using Tui.Controls;
using XenoAtom.Terminal.UI.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// TodoPanel 渲染逻辑测试。
/// 通过 internal 纯函数（FormatHeader / FormatItem）与测试缝（IsExpanded / HeaderText / BodyText）
/// 直接断言——State 绑定是逐帧动态求值，单元测试无渲染循环，不能依赖绑定传播。
/// </summary>
public sealed class TodoPanelTests
{
    private static TodoItem Make(
        int id,
        string subject,
        TodoStatus status,
        string[]? blockedBy = null,
        string? activeForm = null
    ) =>
        new()
        {
            Id = id,
            Subject = subject,
            Status = status,
            BlockedBy = blockedBy ?? [],
            ActiveForm = activeForm ?? "",
        };

    [Fact]
    public void FormatHeader_ShowsCounts()
    {
        var items = new[]
        {
            Make(1, "建世界观", TodoStatus.Pending),
            Make(2, "写大纲", TodoStatus.InProgress),
            Make(3, "建角色", TodoStatus.Completed),
        };

        Assert.Equal("[bold]📋 任务 1/3[/]", TodoPanel.FormatHeader(items, agentRunning: false));
    }

    [Fact]
    public void FormatHeader_ShowsCurrentTask_WhenRunning()
    {
        var items = new[]
        {
            Make(1, "建世界观", TodoStatus.Pending),
            Make(2, "写大纲", TodoStatus.InProgress),
        };

        var header = TodoPanel.FormatHeader(items, agentRunning: true);

        Assert.Contains("📋 任务 0/2", header);
        Assert.Contains("🔄 #2 写大纲", header);
    }

    [Fact]
    public void FormatHeader_OmitsCurrentTask_WhenIdle()
    {
        var items = new[]
        {
            Make(1, "建世界观", TodoStatus.Pending),
            Make(2, "写大纲", TodoStatus.InProgress),
        };

        Assert.DoesNotContain("🔄", TodoPanel.FormatHeader(items, agentRunning: false));
    }

    [Fact]
    public void FormatItem_ResolvesBlockedByToSubject()
    {
        var item = Make(2, "写大纲", TodoStatus.Pending, blockedBy: ["1"]);
        var idToSubject = new Dictionary<int, string> { [1] = "建世界观" };

        var line = TodoPanel.FormatItem(item, idToSubject, agentRunning: false);

        Assert.Contains("🚧 #1 建世界观", line);
    }

    [Fact]
    public void FormatItem_FallsBackToBareId_WhenDependencyDeleted()
    {
        var item = Make(2, "写大纲", TodoStatus.Pending, blockedBy: ["5"]);

        var line = TodoPanel.FormatItem(item, new Dictionary<int, string>(), agentRunning: false);

        Assert.Contains("🚧 #5", line);
    }

    [Fact]
    public void FormatItem_HighlightsInProgress_OnlyWhenRunning()
    {
        var item = Make(2, "写大纲", TodoStatus.InProgress, activeForm: "起草中");

        var running = TodoPanel.FormatItem(item, new Dictionary<int, string>(), agentRunning: true);
        var idle = TodoPanel.FormatItem(item, new Dictionary<int, string>(), agentRunning: false);

        Assert.Contains("[bold yellow]写大纲[/]", running);
        Assert.DoesNotContain("yellow", idle);
        Assert.Contains("(起草中)", running);
    }

    [Fact]
    public void FormatItem_DimsCompleted()
    {
        var item = Make(3, "建角色", TodoStatus.Completed);

        var line = TodoPanel.FormatItem(item, new Dictionary<int, string>(), agentRunning: false);

        Assert.Contains("[dim]建角色[/]", line);
    }

    [Fact]
    public void Refresh_AutoCollapses_WhenEmptyOrAllDeleted()
    {
        var store = new TodoStore();
        var deleted = store.Create("幽灵任务");
        store.Update(deleted.Id, status: "Deleted");

        var panel = new TodoPanel(store, PanelThemes.Dark);
        panel.Refresh();

        // 空态：整个区域切换为居中的 EmptyStatus 占位，列表体为空
        Assert.IsType<Center>(panel.VisualContent);
        Assert.Equal("", panel.BodyText);
    }

    [Fact]
    public void Refresh_EmptyState_ShowsCenteredPlaceholder_AndRestoresCollapsibleOnTasks()
    {
        var store = new TodoStore();
        var panel = new TodoPanel(store, PanelThemes.Dark);
        panel.Refresh();

        // 空态：Center 控件（水平+垂直居中的「暂无任务」EmptyStatus）
        Assert.IsType<Center>(panel.VisualContent);

        // 有任务：恢复 Collapsible（标题/列表左对齐，同 SetUpPanel 列表风格）
        store.Create("写大纲");
        panel.Refresh();

        Assert.IsType<Collapsible>(panel.VisualContent);
        Assert.True(panel.IsExpanded);
        Assert.Contains("写大纲", panel.BodyText);

        // 再次清空：回到居中空态
        store.Clear();
        panel.Refresh();

        Assert.IsType<Center>(panel.VisualContent);
    }

    [Fact]
    public void Refresh_AutoExpands_WhenTasksAppear()
    {
        var store = new TodoStore();
        var panel = new TodoPanel(store, PanelThemes.Dark);
        panel.Refresh();
        Assert.False(panel.IsExpanded);

        store.Create("写大纲");
        panel.Refresh();

        Assert.True(panel.IsExpanded);
        Assert.Contains("📋 任务 0/1", panel.HeaderText);
        Assert.Contains("写大纲", panel.BodyText);
    }

    [Fact]
    public void Refresh_KeepsManualCollapse_WhenTasksExist()
    {
        var store = new TodoStore();
        store.Create("写大纲");
        var panel = new TodoPanel(store, PanelThemes.Dark);
        panel.Refresh();
        Assert.True(panel.IsExpanded);

        // 模拟用户手动折叠后，一次任务变更刷新不重新展开
        var collapsible = Assert.IsType<Collapsible>(
            Assert.IsType<BackgroundPanel>(panel.Visual).Content
        );
        collapsible.IsExpanded = false;
        store.Create("建角色");
        panel.Refresh();

        Assert.False(panel.IsExpanded);
        Assert.Contains("📋 任务 0/2", panel.HeaderText);
    }

    [Fact]
    public void Refresh_SinksCompletedToBottom()
    {
        var store = new TodoStore();
        var pending = store.Create("待办");
        var done = store.Create("已完成");
        store.Update(done.Id, status: "Completed");

        var panel = new TodoPanel(store, PanelThemes.Dark);
        panel.Refresh();

        var body = panel.BodyText;
        var pendingIdx = body.IndexOf("待办", StringComparison.Ordinal);
        var doneIdx = body.IndexOf("已完成", StringComparison.Ordinal);
        Assert.True(pendingIdx >= 0 && doneIdx >= 0);
        Assert.True(pendingIdx < doneIdx, "已完成任务应沉底（排在待办之后）");
    }

    [Fact]
    public void Refresh_DoesNotRenderDeletedGhostRows()
    {
        var store = new TodoStore();
        store.Create("正常任务");
        var deleted = store.Create("幽灵任务");
        store.Update(deleted.Id, status: "Deleted");

        var panel = new TodoPanel(store, PanelThemes.Dark);
        panel.Refresh();

        Assert.Contains("正常任务", panel.BodyText);
        Assert.DoesNotContain("幽灵任务", panel.BodyText);
    }
}
