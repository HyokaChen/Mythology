using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 关于/帮助对话框 — 显示快捷键、斜杠命令和关于信息。
/// 按 Ctrl+H 打开此对话框。
/// </summary>
public sealed class AboutDialog
{
    /// <summary>
    /// 显示关于对话框。
    /// </summary>
    public static void Show()
    {
        // 透明表格样式（无边框，仅表头加粗）
        var transparentTableStyle = TableStyle.RoundedGrid with
        {
            CellStyle = Style.None,
            HeaderStyle = Style.None | TextStyle.Bold,
        };

        var shortcutTable = new Table(
            [Cell("快捷键"), Cell("功能")],
            [
                Row("Ctrl+P", "系统设置"),
                Row("Ctrl+O", "小说项目"),
                Row("Ctrl+E", "会话切换"),
                Row("Ctrl+H", "帮助"),
                Row("Ctrl+L", "清屏"),
                Row("Ctrl+S", "中断 Agent"),
                Row("Ctrl+T", "切换主题"),
                Row("Ctrl+Q", "退出"),
                Row("Tab", "输入补全"),
                Row("Enter", "发送消息"),
                Row("Ctrl+J", "换行"),
            ]
        )
        {
            HorizontalAlignment = Align.Stretch,
        };
        shortcutTable.SetStyle(transparentTableStyle);

        var shortcutTab = new Padder
        {
            Content = shortcutTable,
            Padding = new Thickness(1, 1, 1, 1),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var slashTable = new Table(
            [Cell("命令"), Cell("说明")],
            [
                Row("/session", "会话切换"),
                Row("/tools <名称> <JSON>", "手动执行工具"),
                Row("/tools list", "列出所有可用工具"),
                Row("/stats", "查看工具使用统计与断路器触发"),
                Row("/state", "查看 Agent 状态"),
                Row("/history", "查看对话历史"),
                Row("/clear", "清除对话历史"),
                Row("/resume <novel-id>", "恢复会话"),
                Row("/theme <dark|light|toggle>", "切换主题"),
                Row("/help", "显示帮助"),
            ]
        )
        {
            HorizontalAlignment = Align.Stretch,
        };
        slashTable.SetStyle(transparentTableStyle);

        var slashTab = new Padder
        {
            Content = slashTable,
            Padding = new Thickness(1, 1, 1, 1),
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };

        var aboutTab = PanelThemes.CreateTabContent(
            new MarkdownControl(
                """
                # Mythology

                **AI 小说写作助手**

                基于大语言模型的创意写作工具，支持角色、组织、地点、情节线、道具、关系等实体管理，自动追踪变更与审计日志。

                ## 版权信息

                © 2025 奇思妙想科技有限公司

                本软件及所附创作内容的知识产权归奇思妙想科技有限公司所有。
                未经书面授权，任何个人或组织不得擅自复制、分发、修改、反编译、反向工程或以任何形式使用本软件的全部或部分内容。保留所有权利。
                """
            )
        );

        var tabs = PanelThemes.CreateDialogTabs(
            new TabPage("快捷键", shortcutTab),
            new TabPage("命令", slashTab),
            new TabPage("关于", aboutTab)
        );

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title("📖 Mythology")
            .BottomLeftText(new Markup("[#C7B299]← → 切换 Tab · Esc 关闭[/]"))
            .Width(56)
            .MinHeight(22)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(tabs);

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
        dialog.App?.Focus(tabs);
        return;

        // 行工厂：多个单元格组成一行
        static Visual[] Row(params string[] cells) => cells.Select(Cell).ToArray();

        // 单元格工厂：不换行的段落
        static Visual Cell(string text) =>
            new Paragraph(text) { Wrap = false, HorizontalAlignment = Align.Stretch };
    }
}
