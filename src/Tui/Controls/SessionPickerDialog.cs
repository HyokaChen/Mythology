using Core.Data;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// 会话选择器对话框 — 显示当前小说的会话列表。
/// 按 Ctrl+E 直接打开。
/// </summary>
public sealed class SessionPickerDialog(
    INovelStore novelStore, // 小说存储服务
    FileSessionStore sessionStore, // 会话存储服务
    Action<string, string?>? onOpenNovel = null, // 打开小说回调（参数：novelId, sessionId）
    string? novelId = null, // 当前小说 ID（null 时跳转到项目选择器）
    string? novelTitle = null // 当前小说标题
)
{
    /// <summary>
    /// 显示会话选择器对话框。
    /// </summary>
    public void Show()
    {
        if (novelId is null)
        {
            new NovelProjectDialog(novelStore, sessionStore, onOpenNovel).Show();
            return;
        }

        _ = ShowAsync();
    }

    /// <summary>
    /// 异步显示会话选择器对话框。
    /// </summary>
    private async Task ShowAsync()
    {
        var nid = novelId!;
        try
        {
            var sessions = await sessionStore.GetSessions(nid);

            // 即使没有 session 也弹窗（显示"新建会话"选项）
            ShowSessionList(nid, novelTitle ?? "未命名", sessions);
        }
        catch (Exception ex)
        {
            ToastService.Error($"加载会话失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 显示会话列表。
    /// </summary>
    /// <param name="tNovelId">小说 ID。</param>
    /// <param name="tNovelTitle">小说标题。</param>
    /// <param name="sessions">会话列表。</param>
    private void ShowSessionList(string tNovelId, string tNovelTitle, List<AgentSession> sessions)
    {
        // 首项为"新建会话"，后续为已有会话（最多 20 条）
        var items = new List<SessionItem> { new(null, "新建会话", "") };

        foreach (var s in sessions.Take(20))
        {
            var time = s.UpdatedAt.ToString("MM-dd HH:mm");
            items.Add(new SessionItem(s.Id, s.Title, time));
        }

        var list = new OptionList<SessionItem>()
            .Items(items)
            .ItemSearchText(item => item.Title)
            .ItemTemplate(
                new DataTemplate<SessionItem>(
                    Display: (value, in _) =>
                    {
                        var item = value.GetValue();
                        if (item.SessionId is null)
                        {
                            return new Markup("[bold success]✨ 新建会话[/]");
                        }
                        return new HStack(
                            new Markup($"[bold]{item.Title}[/]"),
                            new Markup($"[#C7B299]{item.Time}[/]")
                        ).Spacing(1);
                    },
                    Editor: null
                )
            )
            .ActivateOnClick(true);
        list.HorizontalAlignment = Align.Stretch;
        list.AutoFocus = true;

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title($"📖 {ChromeText.ForTitle(tNovelTitle, 30)} · 会话列表")
            .BottomLeftText(new Markup("[#C7B299]↑↓ 导航 · Enter 确认 · Esc 关闭[/]"))
            .Width(60)
            .MinHeight(12)
            .Padding(new Thickness(2, 1, 2, 1));

        list.ItemActivated(OnActivated);

        // 统一走 DesertContentFill（整体一色篝火岩）——此前硬编码 charcoal 内容区导致
        // 本 dialog 呈现「新壳 + 老内部」的旧组合，与其余 dialog 不一致
        dialog.Content(PanelThemes.CreateDialogContent(list));

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        dialog.TopRightText(closeBtn);
        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );
        dialog.Show();
        dialog.App?.Focus(list);
        return;

        void OnActivated(object? _, ItemActivatedEventArgs e)
        {
            if (e.Index < 0 || e.Index >= items.Count)
                return;
            var selected = items[e.Index];
            dialog.Close();
            onOpenNovel?.Invoke(tNovelId, selected.SessionId);
        }
    }

    /// <summary>会话列表项（用于 OptionList 展示）。</summary>
    private sealed record SessionItem(string? SessionId, string Title, string Time);
}
