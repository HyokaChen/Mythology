using Core.Data;
using Core.Novels;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Tui.Controls;

/// <summary>阅读器单章数据（正文 + 演化记录）：按需从存储读，列表不预载任何正文。</summary>
internal sealed record ChapterReaderData(
    Chapter Chapter,
    IReadOnlyList<AppearanceRecord> Appearances,
    IReadOnlyList<EntityStateChange> Changes
);

/// <summary>
/// 只读章节阅读器（弹窗）：细纲带 + 正文，支持 ←/→（或 n/p）翻章。
///
/// 与 Desktop 的 <c>ChapterReaderWindow</c> 同构——TUI 的 Dialog 就是它的窗口；
/// 长篇列表只给骨架行（序号/标题/状态/字数/细纲首行），**正文在这里按需单章读取**
/// （<see cref="INovelStore.GetChapterByOrder"/>，O(1) 单文件），翻章不预载全书。
/// </summary>
internal sealed class ChapterReaderDialog
{
    private readonly IReadOnlyList<Chapter> _chapters;
    private readonly Func<string, CancellationToken, Task<ChapterReaderData?>> _loadBody;
    private readonly Func<Chapter, Volume?> _volumeResolver;
    private readonly Func<string, string>? _plotlineName;
    private readonly Func<string, string>? _characterName;
    private readonly Func<string, string>? _locationName;

    private readonly VStack _body = new() { Spacing = 0 };
    private readonly State<string?> _positionText = new("");
    private readonly Dialog _dialog;
    private readonly Dictionary<string, ChapterReaderData> _loaded = new(StringComparer.Ordinal);
    private int _index;
    private int _loadToken;

    private ChapterReaderDialog(
        IReadOnlyList<Chapter> chapters,
        int index,
        Func<string, CancellationToken, Task<ChapterReaderData?>> loadBody,
        Func<Chapter, Volume?> volumeResolver,
        Func<string, string>? plotlineName,
        Func<string, string>? characterName,
        Func<string, string>? locationName
    )
    {
        _chapters = chapters;
        _index = index;
        _loadBody = loadBody;
        _volumeResolver = volumeResolver;
        _plotlineName = plotlineName;
        _characterName = characterName;
        _locationName = locationName;

        _dialog = new Dialog()
            .IsResizable(true)
            .IsDraggable(false)
            .IsModal(true)
            .Title("📖 章节阅读器")
            .BottomLeftText(new Markup("[#C7B299]← → / n p 翻章 · ↑↓ PgUp/PgDn 滚动 · Esc 关闭[/]"))
            .Width(100)
            .MinHeight(30)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(
                PanelThemes.CreateScrollableDialogContent(
                    new VStack(new Markup(_positionText), _body).Spacing(0)
                )
            );

        var closeBtn = PanelThemes.CreateDialogCloseButton(_dialog.Close);
        _dialog.TopRightText(closeBtn);
        _dialog.KeyDown(OnKeyDown);
    }

    /// <summary>
    /// 打开阅读器。<paramref name="chapters"/> 需按序号升序（骨架即可，Content 不参与）；
    /// <paramref name="start"/> 为起始章节；<paramref name="loadBody"/> 按章 id 取正文 + 演化记录。
    /// </summary>
    public static async Task ShowAsync(
        IReadOnlyList<Chapter> chapters,
        Chapter start,
        Func<string, CancellationToken, Task<ChapterReaderData?>> loadBody,
        Func<Chapter, Volume?> volumeResolver,
        Func<string, string>? plotlineName = null,
        Func<string, string>? characterName = null,
        Func<string, string>? locationName = null
    )
    {
        var index = 0;
        for (var i = 0; i < chapters.Count; i++)
        {
            if (string.Equals(chapters[i].Id, start.Id, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        var dialog = new ChapterReaderDialog(
            chapters,
            index,
            loadBody,
            volumeResolver,
            plotlineName,
            characterName,
            locationName
        );
        await dialog.RenderCurrentAsync(); // 首章正文就绪后再弹窗，避免空闪
        dialog._dialog.Show();
    }

    /// <summary>当前章位置文案（"第 3 / 120 章 · 进度 12%"）。</summary>
    internal string PositionLine()
    {
        var chapter = Current;
        var percent =
            _chapters.Count == 0 ? 0 : (int)Math.Round((_index + 1) * 100.0 / _chapters.Count);
        return $"[dim]{ChapterLocator.DescribePosition(_chapters, chapter?.Id)} · 进度 {percent}%[/]";
    }

    private Chapter? Current => _index >= 0 && _index < _chapters.Count ? _chapters[_index] : null;

    /// <summary>翻到上一章；false = 已经是第一章（调用方给提示）。</summary>
    internal bool ShowPrevious()
    {
        var target = ChapterLocator.MoveBy(_chapters, Current?.Id, -1);
        if (target is null)
            return false;
        _index = IndexOf(target.Id);
        _ = RenderCurrentAsync();
        return true;
    }

    /// <summary>翻到下一章；false = 已经是最后一章。</summary>
    internal bool ShowNext()
    {
        var target = ChapterLocator.MoveBy(_chapters, Current?.Id, 1);
        if (target is null)
            return false;
        _index = IndexOf(target.Id);
        _ = RenderCurrentAsync();
        return true;
    }

    /// <summary>
    /// 重绘当前章（翻章后替换正文控件；对话框本身不重建）。
    /// 正文按需读取：命中缓存直接用，否则显示「载入中…」并异步取回；过期结果按 token 丢弃。
    /// </summary>
    internal async Task RenderCurrentAsync()
    {
        _positionText.Value = PositionLine();
        _body.Children.Clear();
        if (Current is not { } header)
        {
            _body.Children.Add(new Markup("[dim]（没有可显示的章节）[/]"));
            return;
        }

        var token = ++_loadToken;
        if (!_loaded.TryGetValue(header.Id, out var data))
        {
            _body.Children.Add(new Markup("[dim]载入中…[/]"));
            data = await _loadBody(header.Id, CancellationToken.None);
            if (data is null)
            {
                if (token == _loadToken)
                {
                    _body.Children.Clear();
                    _body.Children.Add(
                        new Markup("[red]（本章读取失败：文件可能已删除或改名）[/]")
                    );
                }
                return;
            }
            _loaded[header.Id] = data;
        }

        if (token != _loadToken)
            return; // 读取期间又翻章 → 丢弃过期结果

        _body.Children.Clear();
        var markdown = ChapterReaderContent.Build(
            data.Chapter,
            _volumeResolver(data.Chapter),
            _plotlineName,
            _characterName,
            data.Appearances,
            data.Changes,
            _locationName
        );
        _body.Children.Add(new MarkdownControl(markdown));
    }

    private int IndexOf(string chapterId)
    {
        for (var i = 0; i < _chapters.Count; i++)
        {
            if (string.Equals(_chapters[i].Id, chapterId, StringComparison.Ordinal))
                return i;
        }
        return _index;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case TerminalKey.Escape:
                _dialog.Close();
                break;
            case TerminalKey.Left:
                ShowPrevious();
                break;
            case TerminalKey.Right:
                ShowNext();
                break;
            default:
                // n / p 翻章（无修饰键时；不抢 Ctrl+N 之类组合键）
                if (!IsPlainKey(e))
                    return;
                if (e.Char is 'n' or 'N')
                    ShowPrevious();
                else if (e.Char is 'p' or 'P')
                    ShowNext();
                else
                    return;
                break;
        }
        e.Handled = true;
    }

    /// <summary>n/p 只在无修饰键时翻章（不抢 Ctrl+N 之类的组合键）。</summary>
    private static bool IsPlainKey(KeyEventArgs e) =>
        e.Modifiers is TerminalModifiers.None or TerminalModifiers.Shift;
}
