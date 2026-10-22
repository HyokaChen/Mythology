using Xunit;

namespace Tui.Tests;

/// <summary>
/// 终端 UI 测试集合：<c>Terminal.Instance</c> / <c>Dispatcher.Current</c> / <c>TerminalApp</c> 都是
/// 进程级单例，凡是要起真实终端的测试类都必须落进这个集合，保证严格串行。
///
/// <para>
/// 为什么显式声明：串行目前靠 <c>xunit.runner.json</c> 的全局 <c>parallelizeTestCollections:false</c>；
/// 一旦有人打开并行（或新增测试项目）就会踩「上一类的 UI 线程还没退出 → 下一类 Invalid thread access /
/// 迟到 Post 触发进程中止」这两个坑。落在同一 collection（且 DisableParallelization）后，
/// 这个约束不再依赖全局配置。配合 <see cref="TerminalUiFixture"/> 的启动/退出屏障使用。
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TerminalUiCollection
{
    /// <summary>集合名（测试类用 <c>[Collection(TerminalUiCollection.Name)]</c>）。</summary>
    public const string Name = "TerminalUi";
}
