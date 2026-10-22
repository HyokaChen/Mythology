using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// TerminalTitleSpinner 测试：注入 Action&lt;string&gt; 捕获标题序列，
/// tickAction 注入 no-op 禁用真实 Timer，用 TickForTest 手动驱动帧推进
/// （不做真实时间断言，避免 Timer 并发竞态）。
/// </summary>
public sealed class TerminalTitleSpinnerTests
{
    private static TerminalTitleSpinner Create(out List<string> titles)
    {
        titles = [];
        return new TerminalTitleSpinner(titles.Add, tickAction: () => { });
    }

    [Fact]
    public void SetBusy_ThenTicks_WritesFramePrefixTitles()
    {
        using var spinner = Create(out var titles);
        spinner.SetBaseTitle("Mythology");
        titles.Clear();

        spinner.SetBusy("思考中");

        // 进入 busy 立即写出第一帧
        Assert.Contains("⠋ 思考中…｜Mythology", titles);

        // 帧随推进轮换
        spinner.TickForTest();
        Assert.Equal("⠙ 思考中…｜Mythology", titles[^1]);
        spinner.TickForTest();
        Assert.Equal("⠹ 思考中…｜Mythology", titles[^1]);
    }

    [Fact]
    public void SetBusyNull_RestoresBaseTitle()
    {
        using var spinner = Create(out var titles);
        spinner.SetBaseTitle("Mythology");
        spinner.SetBusy("思考中");
        titles.Clear();

        spinner.SetBusy(null);

        var last = Assert.Single(titles);
        Assert.Equal("Mythology", last);
    }

    [Fact]
    public void SetBaseTitle_DuringBusy_RewritesWithFrame()
    {
        using var spinner = Create(out var titles);
        spinner.SetBusy("思考中"); // 进入 busy 已消耗一帧（index→1）
        titles.Clear();

        spinner.SetBaseTitle("恋爱吧！折花君");

        // busy 期间更新基础标题 → 当前帧 + 新基础标题
        Assert.Equal("⠙ 思考中…｜恋爱吧！折花君", titles[^1]);
    }

    [Fact]
    public void Dispose_StopsAnimation_RestoresTitle()
    {
        var titles = new List<string>();
        var spinner = new TerminalTitleSpinner(titles.Add, tickAction: () => { });
        spinner.SetBaseTitle("Mythology");
        spinner.SetBusy("压缩中");
        titles.Clear();

        spinner.Dispose();
        spinner.TickForTest(); // Dispose 后帧推进不再写标题

        var last = Assert.Single(titles);
        Assert.Equal("Mythology", last);
    }

    [Fact]
    public void TickForTest_AfterIdle_WritesNothing()
    {
        using var spinner = Create(out var titles);
        spinner.SetBaseTitle("Mythology");
        titles.Clear();

        spinner.TickForTest(); // 从未 busy，不应写任何标题

        Assert.Empty(titles);
    }
}
