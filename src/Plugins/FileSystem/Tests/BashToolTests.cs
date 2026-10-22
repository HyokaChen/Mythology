using System.Runtime.InteropServices;
using Core.Tools;
using Plugins.FileSystem.Tools;
using Xunit;

namespace FileSystem.Tests;

public class BashToolTests
{
    private readonly BashTool _tool = new();

    /// <summary>当前是否为 Windows 平台（决定 shell 是 PowerShell 还是 bash）。</summary>
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public async Task ExecuteAsync_SimpleCommand_ReturnsOutput()
    {
        // echo 在 bash 和 PowerShell 中语法一致
        var args = new BashParams { Command = "echo hello" };

        var result = await _tool.ExecuteAsync(
            "call_1",
            args,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("hello", text);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroExitCode_NoError()
    {
        var args = new BashParams { Command = "echo ok" };

        var result = await _tool.ExecuteAsync(
            "call_1",
            args,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_IsError()
    {
        // bash: exit 1 | PowerShell: exit 1 — 语法一致
        var args = new BashParams { Command = "exit 1" };

        var result = await _tool.ExecuteAsync(
            "call_1",
            args,
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsError()
    {
        // 按平台选择 sleep 命令：bash 用 sleep，PowerShell 用 Start-Sleep
        var sleepCmd = IsWindows ? "Start-Sleep -Seconds 10" : "sleep 10";
        var args = new BashParams { Command = sleepCmd, TimeoutMs = 500 };

        var result = await _tool.ExecuteAsync(
            "call_1",
            args,
            TestContext.Current.CancellationToken
        );

        // 超时命令必须返回 IsError=true（不论走 WaitForExit 超时分支还是取消分支）
        Assert.True(result.IsError, $"Timeout command should report error, got: {GetText(result)}");
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkingDirectory_UsesIt()
    {
        var tempDir = Path.GetTempPath();
        // 按平台选择打印工作目录的命令：bash 用 pwd，PowerShell 也用 pwd（Get-Location 别名）
        var pwdCmd = "pwd";
        var args = new BashParams { Command = pwdCmd, WorkingDirectory = tempDir };

        var result = await _tool.ExecuteAsync(
            "call_1",
            args,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains(tempDir.TrimEnd(Path.DirectorySeparatorChar), text.TrimEnd());
    }

    [Fact]
    public async Task ExecuteAsync_Utf8ChineseOutput_NotMojibake()
    {
        // 回归：Windows PowerShell 5.x 默认用系统代码页（GBK）输出，若不显式设置
        // StandardOutputEncoding=UTF8 + 注入 [Console]::OutputEncoding，中文输出会乱码
        // （LLM 无法阅读错误 → 反复失败）。此测试在 Linux 上同样跑（输出已是 UTF-8）。
        var args = new BashParams { Command = "echo 中文输出测试" };

        var result = await _tool.ExecuteAsync(
            "call_1",
            args,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError, GetText(result));
        var text = GetText(result);
        Assert.Contains("中文输出测试", text);
        // 乱码特征：替换符 U+FFFD 或 GBK 解码产物不得出现
        Assert.DoesNotContain('\uFFFD', text);
    }

    private static string GetText(AgentToolResult result) =>
        result.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
