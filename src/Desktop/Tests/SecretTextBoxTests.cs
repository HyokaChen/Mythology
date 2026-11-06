using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Desktop.Controls;
using Desktop.Services;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 密钥输入的统一契约：默认只显示末 4 位，聚焦后才显示完整值，失焦恢复遮蔽。
/// </summary>
public sealed class SecretTextBoxTests
{
    [Theory]
    [InlineData("sk-1234567890abcd", "••••••••abcd")]
    [InlineData("abcd", "••••")]
    [InlineData("abc", "•••")]
    public void SecretMask_KeepsOnlyLastFour(string secret, string expected)
    {
        Assert.Equal(expected, SecretMask.Format(secret));
    }

    [Fact]
    public void SecretMask_EmptyUsesProvidedText()
    {
        Assert.Equal("未填写密钥", SecretMask.Format("", "未填写密钥"));
    }

    [Fact]
    public void Focus_RevealsFullValue_AndBlurRestoresMask()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var input = new SecretTextBox
            {
                Text = "sk-1234567890abcd",
                PlaceholderText = "API Key",
            };
            var next = new TextBox();
            var window = new Window
            {
                Content = new StackPanel { Spacing = 8, Children = { input, next } },
                Width = 360,
                Height = 200,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var textBoxes = input.GetVisualDescendants().OfType<TextBox>().ToList();
            var masked = textBoxes.Single(t => t.IsReadOnly);
            var editor = textBoxes.Single(t => !t.IsReadOnly);

            Assert.True(masked.IsVisible);
            Assert.False(editor.IsVisible);
            Assert.Equal("••••••••abcd", masked.Text);

            masked.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.False(masked.IsVisible);
            Assert.True(editor.IsVisible);
            Assert.True(editor.IsFocused);
            Assert.Equal("sk-1234567890abcd", editor.Text);

            next.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.True(masked.IsVisible);
            Assert.False(editor.IsVisible);
        });
    }

    [Fact]
    public void Editing_FlowsBackToText_AndBlurRemasksLatestValue()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var input = new SecretTextBox { Text = "sk-old-0000", PlaceholderText = "API Key" };
            var next = new TextBox();
            var window = new Window
            {
                Content = new StackPanel { Spacing = 8, Children = { input, next } },
                Width = 360,
                Height = 200,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var textBoxes = input.GetVisualDescendants().OfType<TextBox>().ToList();
            var masked = textBoxes.Single(t => t.IsReadOnly);
            var editor = textBoxes.Single(t => !t.IsReadOnly);

            masked.Focus();
            Dispatcher.UIThread.RunJobs();

            editor.Text = "sk-new-1234";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("sk-new-1234", input.Text);
            Assert.Equal("••••••••1234", masked.Text);

            next.Focus();
            Dispatcher.UIThread.RunJobs();

            Assert.True(masked.IsVisible);
            Assert.False(editor.IsVisible);
            Assert.Equal("••••••••1234", masked.Text);
        });
    }
}
