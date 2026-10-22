using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>临时探针：string → Visual 隐式转换产生什么控件、是否解析标记（验证后删除）。</summary>
[Collection(TerminalUiCollection.Name)]
public sealed class ScratchStringVisualTests
{
    [Fact]
    public async Task Probe()
    {
        Visual fromString = "a [bold]B[/] c";
        var typeName = fromString.GetType().Name;
        var backend = new InMemoryTerminalBackend(new TerminalSize(40, 6), null);
        Terminal.Initialize(backend, options: null, force: true);
        var root = new VStack(new Markup("X"), fromString);
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() =>
            XenoAtom.Terminal.Terminal.Instance.RunAsync(
                root,
                () => TerminalLoopResult.Continue,
                cts.Token
            )
        );
        await Task.Delay(1200);
        await cts.CancelAsync();
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // 取消是预期退出路径
        }
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "string-visual.txt"),
            $"type={typeName}\n{backend.GetOutText()}"
        );
        Terminal.Close();
    }
}
