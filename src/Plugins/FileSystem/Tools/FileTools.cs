using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Core.Shared;
using Core.Tools;

namespace Plugins.FileSystem.Tools;

// ── 读取文件工具 ──

/// <summary>
/// 读取文件工具：支持文本、图片、二进制文件读取，以及目录列表。
/// </summary>
public sealed class ReadFileTool : AgentTool<ReadFileParams, ReadFileDetails>
{
    // 默认最大读取行数
    private const int DefaultMaxLines = 200;

    // 默认最大读取字节数（50KB）
    private const int MaxSizeBytes = 50 * 1024; // 50KB

    // 图片扩展名到 MIME 类型的映射表
    private static readonly Dictionary<string, string> ImageMimes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
    };

    // 工具标识名
    public override string Name => "read";

    // 工具显示标签
    public override string Label => "读取文件";

    // 工具功能描述
    public override string Description =>
        "读取本地文件内容。支持文本和图片。@ 前缀路径从工作目录解析。支持 offset+lines 分页读取（默认200行/50KB截断）。大文件请分批读取";

    // 工具所属分类
    public override ToolCategory Category => ToolCategory.Generic;

    public override async Task<AgentToolResult<ReadFileDetails>> ExecuteAsync(
        string toolCallId,
        ReadFileParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ReadFileDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 解析文件路径（支持 @ 前缀从工作目录解析）
        var path = PathHelper.ResolvePath(args.Path);
        // 如果文件不存在，尝试作为目录处理
        if (!File.Exists(path))
        {
            // 尝试作为目录列表返回
            if (Directory.Exists(path))
            {
                var entries = Directory
                    .GetFileSystemEntries(path)
                    .Select(p =>
                    {
                        var isDir = Directory.Exists(p);
                        var name = Path.GetFileName(p);
                        return isDir ? $"{name}/" : name;
                    })
                    .OrderBy(n => n)
                    .ToArray();
                var listing = string.Join("\n", entries);
                if (entries.Length == 0)
                    listing = "(空目录)";
                return new AgentToolResult<ReadFileDetails>
                {
                    Content = [new TextContent($"📂 {path}\n{listing}")],
                    Details = new ReadFileDetails
                    {
                        Path = path,
                        Type = "directory",
                        EntryCount = entries.Length,
                    },
                };
            }
            return new AgentToolResult<ReadFileDetails>
            {
                Content = [new TextContent($"文件不存在: {path}")],
                IsError = true,
            };
        }

        var ext = Path.GetExtension(path);

        // 图片文件：返回 base64 内联数据（供 LLM 多模态或 TUI 渲染使用）
        if (ImageMimes.TryGetValue(ext, out var mime))
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            // 图片大小限制为 100KB
            if (bytes.Length > MaxSizeBytes * 2) // 100KB limit for images
            {
                return new AgentToolResult<ReadFileDetails>
                {
                    Content =
                    [
                        new TextContent(
                            $"图片文件过大 ({bytes.Length / 1024}KB)，跳过内联显示。路径: {path}"
                        ),
                    ],
                    Details = new ReadFileDetails
                    {
                        Path = path,
                        Type = "image",
                        SizeBytes = bytes.Length,
                    },
                };
            }
            var b64 = Convert.ToBase64String(bytes);
            return new AgentToolResult<ReadFileDetails>
            {
                Content =
                [
                    new TextContent($"📷 {path} ({mime}, {bytes.Length / 1024}KB)"),
                    new ImageContent($"data:{mime};base64,{b64}", Path.GetFileName(path)),
                ],
                Details = new ReadFileDetails
                {
                    Path = path,
                    Type = "image",
                    SizeBytes = bytes.Length,
                },
            };
        }

        // 二进制文件检测：检查前 8KB 是否包含空字节
        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8192
        );
        var probe = new byte[Math.Min(fs.Length, 8192)];
        var probeRead = 0;
        while (probeRead < probe.Length)
        {
            var n = await fs.ReadAsync(probe.AsMemory(probeRead), ct);
            if (n == 0)
                break;
            probeRead += n;
        }
        // 如果包含空字节，则判定为二进制文件
        if (probe.Contains((byte)0))
        {
            return new AgentToolResult<ReadFileDetails>
            {
                Content = [new TextContent($"二进制文件: {path} ({fs.Length / 1024}KB)")],
                Details = new ReadFileDetails
                {
                    Path = path,
                    Type = "binary",
                    SizeBytes = fs.Length,
                },
            };
        }

        // 文本文件：支持按行范围读取（分页）
        fs.Position = 0;
        using var reader = new StreamReader(
            fs,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true
        );

        // 计算起始行号和读取行数限制
        var offset = Math.Max(args.Offset ?? 1, 1);
        var limit = args.Lines ?? DefaultMaxLines;
        var sb = new StringBuilder(4096);
        var lineNum = 0;
        var linesRead = 0;
        var truncated = false;
        var totalSizeBytes = 0;

        // 逐行读取，跳过 offset 之前的行，直到达到限制
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNum++;
            if (lineNum < offset)
                continue;
            if (linesRead >= limit)
            {
                truncated = true;
                break;
            }
            sb.AppendLine(line);
            linesRead++;
            totalSizeBytes += line.Length + 1;

            // 如果超过最大字节数限制，则截断
            if (totalSizeBytes > MaxSizeBytes)
            {
                truncated = true;
                break;
            }
        }

        // 如果 offset > 1，显示上下文头部信息
        var header = offset > 1 ? $"(第 {offset} 行起, 共 {linesRead} 行)\n" : "";
        var footer = truncated
            ? $"\n[截断: 已达 {DefaultMaxLines} 行或 50KB 上限。用 offset={offset + linesRead} lines={DefaultMaxLines} 继续读取剩余内容]"
            : "";

        return new AgentToolResult<ReadFileDetails>
        {
            Content = [new TextContent($"{header}{sb}{footer}")],
            Details = new ReadFileDetails
            {
                Path = path,
                Type = "text",
                LinesRead = linesRead,
                Offset = offset,
                Truncated = truncated,
                SizeBytes = totalSizeBytes,
            },
        };
    }
}

/// <summary>
/// 读取文件工具的参数。
/// </summary>
public sealed record ReadFileParams
{
    [Description(
        "文件路径。@ 前缀表示从工作目录解析（如 @序章.txt → {CWD}/序章.txt）。"
            + "小说章节路径格式：@novels/{书名}/chapters/{ch*}.md（如 @novels/恋爱吧！折花君/chapters/ch001.md）。"
            + "相对路径也从工作目录解析"
    )]
    public string Path { get; init; } = "";

    [Description("起始行号（从 1 开始，默认 1）。用于分页读取大文件")]
    public int? Offset { get; init; }

    [Description(
        "读取行数（默认 200）。大文件应分批读取：先读前 N 行，再用 offset+lines 继续读取后续内容"
    )]
    public int? Lines { get; init; }
}

/// <summary>
/// 读取文件工具的返回详情。
/// </summary>
public sealed record ReadFileDetails
{
    public string Path { get; init; } = "";
    public string Type { get; init; } = ""; // text, image, binary, directory
    public int LinesRead { get; init; }
    public int Offset { get; init; }
    public bool Truncated { get; init; }
    public long SizeBytes { get; init; }
    public int EntryCount { get; init; }
}

// ── 写入文件工具 ──

/// <summary>
/// 写入文件工具：支持自动创建父目录，覆盖已有文件。
/// </summary>
public sealed class WriteFileTool : AgentTool<WriteFileParams, WriteFileDetails>
{
    public override string Name => "write";
    public override string Label => "写入文件";
    public override string Description => "写入文件内容。自动创建父目录。如文件已存在则覆盖";

    public override ToolCategory Category => ToolCategory.FileEdit;
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<WriteFileDetails>> ExecuteAsync(
        string toolCallId,
        WriteFileParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WriteFileDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var path = PathHelper.ResolvePath(args.Path);

        // 如果文件已存在，捕获旧内容用于 diff 渲染
        // 只存储截断的 hunks，不存储完整文件内容
        EditHunk[]? hunks = null;
        if (File.Exists(path))
        {
            var oldContent = await File.ReadAllTextAsync(path, ct);
            ThrowIfCancelled(ct);
            hunks = [EditHunk.FromFullContent(oldContent, args.Content)];
        }

        // 自动创建父目录
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, args.Content, ct);
        ThrowIfCancelled(ct);
        var lines = args.Content.Split('\n').Length;
        return new AgentToolResult<WriteFileDetails>
        {
            Content = [new TextContent($"已写入: {path} ({args.Content.Length} 字, {lines} 行)")],
            Details = new WriteFileDetails
            {
                Path = path,
                BytesWritten = args.Content.Length,
                Lines = lines,
                Hunks = hunks,
            },
        };
    }
}

/// <summary>
/// 写入文件工具的参数。
/// </summary>
public sealed record WriteFileParams
{
    [Description("文件路径。@ 前缀表示从工作目录解析（如 @序章.txt）")]
    public string Path { get; init; } = "";

    [Description("要写入的内容")]
    public string Content { get; init; } = "";
}

/// <summary>
/// 写入文件工具的返回详情。
/// </summary>
public sealed record WriteFileDetails : IDiffDetails
{
    public string Path { get; init; } = "";
    public int BytesWritten { get; init; }
    public int Lines { get; init; }

    /// <summary>
    /// 用于 TUI diff 渲染的 hunk 列表。新建文件时为 null（无旧内容）。
    /// 每个 hunk 的 OldText/NewText 会被截断到 MaxDiffLines，避免内存中存储完整文件内容。
    /// </summary>
    public EditHunk[]? Hunks { get; init; }

    // IDiffDetails.Path — 显式实现，record 的 Path 属性满足接口要求
    string IDiffDetails.Path => Path;
}

// ── 编辑文件工具（搜索/替换） ──

/// <summary>
/// 编辑文件工具：对文件执行精确搜索/替换编辑。
/// oldText 必须唯一匹配文件中的内容，支持多个替换操作。
/// </summary>
public sealed class EditFileTool : AgentTool<EditFileParams, EditFileDetails>
{
    public override string Name => "edit";
    public override string Label => "编辑文件";
    public override string Description =>
        "对文件执行精确搜索/替换编辑。oldText 必须唯一匹配文件中的内容。支持多个替换操作";

    public override ToolCategory Category => ToolCategory.FileEdit;
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<EditFileDetails>> ExecuteAsync(
        string toolCallId,
        EditFileParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<EditFileDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var path = PathHelper.ResolvePath(args.Path);

        // 检查文件是否存在
        if (!File.Exists(path))
        {
            return new AgentToolResult<EditFileDetails>
            {
                Content = [new TextContent($"文件不存在: {path}")],
                IsError = true,
            };
        }

        // 检查是否提供了编辑操作
        if (args.Edits is null || args.Edits.Length == 0)
        {
            return new AgentToolResult<EditFileDetails>
            {
                Content = [new TextContent("未提供编辑操作")],
                IsError = true,
            };
        }

        var rawContent = await File.ReadAllTextAsync(path, ct);
        ThrowIfCancelled(ct);

        // 行尾归一化：LLM 提供的 oldText/newText 通常使用 \n，而文件（尤其 Windows）常为 \r\n，
        // Ordinal 精确匹配会因此永远失败（read 显示时不体现 \r，造成"明明存在却匹配不到"）。
        // 匹配与替换统一在 LF 空间进行，写回时按文件原有行尾风格恢复。
        var usesCrlf = rawContent.Contains("\r\n", StringComparison.Ordinal);
        var content = usesCrlf ? rawContent.Replace("\r\n", "\n") : rawContent;

        var changes = new List<string>();
        var applied = 0;
        var hunks = new List<EditHunk>();

        // 逐个处理编辑操作
        foreach (var edit in args.Edits)
        {
            if (string.IsNullOrEmpty(edit.OldText))
            {
                changes.Add("❌ oldText 为空，跳过");
                continue;
            }

            // 检查唯一性：oldText 必须精确匹配一次
            var idx = content.IndexOf(edit.OldText, StringComparison.Ordinal);
            if (idx < 0)
            {
                changes.Add($"❌ 未找到匹配: {Truncate(edit.OldText, 40)}");
                continue;
            }

            // 检查是否有重复匹配
            var secondIdx = content.IndexOf(edit.OldText, idx + 1, StringComparison.Ordinal);
            if (secondIdx >= 0)
            {
                changes.Add($"❌ 匹配不唯一（出现多次）: {Truncate(edit.OldText, 40)}");
                continue;
            }

            // 计算替换前的起始行号（1-based）
            var oldStartLine = content[..idx].Count(c => c == '\n') + 1;

            var newText = edit.NewText.Replace("\r\n", "\n");

            // 记录 hunk 用于 TUI diff 渲染
            hunks.Add(
                new EditHunk
                {
                    OldStartLine = oldStartLine,
                    OldText = edit.OldText,
                    NewText = newText,
                }
            );

            // 执行替换
            content = string.Concat(
                content.AsSpan(0, idx),
                newText,
                content.AsSpan(idx + edit.OldText.Length)
            );

            changes.Add($"✅ {Truncate(edit.OldText, 30)} → {Truncate(newText, 30)}");
            applied++;
        }

        // 如果有编辑被应用，则写回文件（恢复原行尾风格）
        if (applied > 0)
        {
            var output = usesCrlf ? content.Replace("\n", "\r\n") : content;
            await File.WriteAllTextAsync(path, output, ct);
        }

        var summary =
            applied > 0
                ? $"已应用 {applied}/{args.Edits.Length} 个编辑到 {path}:\n{string.Join("\n", changes)}"
                : $"未应用任何编辑到 {path}:\n{string.Join("\n", changes)}";

        return new AgentToolResult<EditFileDetails>
        {
            Content = [new TextContent(summary)],
            Details = new EditFileDetails
            {
                Path = path,
                AppliedCount = applied,
                TotalCount = args.Edits.Length,
                Hunks = hunks.Count > 0 ? [.. hunks] : null,
            },
        };
    }

    /// <summary>
    /// 截断字符串，超过 max 长度时添加省略号（使用统一的 StringHelper.Truncate）。
    /// </summary>
    private static string Truncate(string s, int max) => s.Truncate(max);
}

/// <summary>
/// 编辑文件工具的参数。
/// </summary>
public sealed record EditFileParams
{
    [Description("文件路径。@ 前缀表示从工作目录解析（如 @序章.txt）")]
    public string Path { get; init; } = "";

    [Description("编辑操作列表（搜索/替换对）")]
    public EditOperation[] Edits { get; init; } = [];
}

/// <summary>
/// 单个编辑操作：搜索/替换对。
/// </summary>
public sealed record EditOperation
{
    [Description("要搜索的原始文本（必须唯一匹配文件中的一处内容）")]
    public string OldText { get; init; } = "";

    [Description("替换后的新文本")]
    public string NewText { get; init; } = "";
}

/// <summary>
/// 编辑文件工具的返回详情。
/// </summary>
public sealed record EditFileDetails : IDiffDetails
{
    public string Path { get; init; } = "";
    public int AppliedCount { get; init; }
    public int TotalCount { get; init; }

    /// <summary>
    /// 每个已应用编辑操作的结构化 diff 数据。
    /// 未应用任何编辑或为兼容旧版本时为 null。
    /// </summary>
    public EditHunk[]? Hunks { get; init; }

    // IDiffDetails.Path — 显式实现，record 的 Path 属性满足接口要求
    string IDiffDetails.Path => Path;
}

// ── Bash 命令执行工具 ──

/// <summary>
/// Bash 命令执行工具：在本地 shell 执行命令。
/// Unix 使用 bash，Windows 使用 PowerShell，支持超时和输出截断。
/// </summary>
public sealed class BashTool : AgentTool<BashParams, BashDetails>
{
    // 默认超时时间（毫秒）
    private const int DefaultTimeoutMs = 30_000;

    // 最大输出字符数
    private const int MaxOutputChars = 50_000;

    public override string Name => "bash";
    public override string Label => "执行命令";
    public override string Description =>
        """
            执行本地 shell 命令。仅在 read/write/edit 等专用工具无法满足需求时才使用。

            ⚠️ Windows 上实际执行 PowerShell 5.x（不是 bash！）：
            - bash 语法不可用：ls -la / && / 单引号字符串 / grep 管道 / cd x && cmd
            - 用 PowerShell 语法：Get-ChildItem（或 dir）列目录、; 分隔命令、双引号字符串
            - 常见替代：列目录用 read 工具（支持目录路径）、读文件用 read、写文件用 write、编辑用 edit

            禁止用本工具复现其他工具已有能力（列目录、读文件、写文件、诊断等），
            禁止用本工具调用 CLI 代替专用工具（如 npx mythology diagnose_writing）。

            支持超时和输出截断。
            """;

    public override ToolCategory Category => ToolCategory.BashTool;
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 可能写任意文件：强制串行防并发写冲突

    public override async Task<AgentToolResult<BashDetails>> ExecuteAsync(
        string toolCallId,
        BashParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BashDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 设置超时
        var timeoutMs = args.TimeoutMs ?? DefaultTimeoutMs;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        // 根据操作系统选择 shell
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // 配置进程启动信息
        // StandardOutputEncoding/StandardErrorEncoding = UTF-8：Windows PowerShell 5.x
        // 默认用系统代码页（GBK 等）输出，与 .NET 读取端不一致会导致中文乱码，
        // LLM 无法阅读错误信息。统一 UTF-8 保证跨平台输出一致。
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = isWindows ? "powershell.exe" : "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(args.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : PathHelper.ResolvePath(args.WorkingDirectory),
        };

        // 使用 ArgumentList 传参，避免 Arguments 字符串被 OS 按空格拆分。
        // bash -c "echo hello" 需要把 "echo hello" 作为 -c 的单个参数，
        // 如果用 Arguments="-c echo hello"，Unix 会拆成 ["-c","echo","hello"]，
        // bash 只执行 echo（无参数），hello 被忽略。
        if (isWindows)
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            // PowerShell 5.x 默认向管道输出使用系统 OEM 代码页（GBK），
            // 与读取端 UTF-8 不匹配 → 中文乱码。先强制输出编码为 UTF-8。
            // 用 ; 拼接命令（PowerShell 5.x 不支持 bash 的 &&）。
            startInfo.ArgumentList.Add(
                $"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; {args.Command}"
            );
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(args.Command);
        }

        // 如果提供了环境变量，则设置
        if (args.Env is not null)
        {
            foreach (var kv in args.Env)
                startInfo.Environment[kv.Key] = kv.Value;
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = startInfo;
            process.Start();

            // 并发读取 stdout 和 stderr
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            // 等待进程退出（带超时）
            var exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                // 超时处理：终止进程树
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch { }
                return new AgentToolResult<BashDetails>
                {
                    Content =
                    [
                        new TextContent(
                            $"命令超时 ({timeoutMs}ms): {TruncateCommand(args.Command)}"
                        ),
                    ],
                    IsError = true,
                    Details = new BashDetails { ExitCode = -1, TimedOut = true },
                };
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // 构建输出结果
            var output = new StringBuilder();
            if (!string.IsNullOrEmpty(stdout))
            {
                if (stdout.Length > MaxOutputChars)
                    output.Append(stdout[..MaxOutputChars]).Append("\n[截断: 输出超过 50KB]");
                else
                    output.Append(stdout);
            }
            if (!string.IsNullOrEmpty(stderr))
            {
                output.Append("\n--- stderr ---\n");
                if (stderr.Length > MaxOutputChars)
                    output.Append(stderr[..MaxOutputChars]).Append("\n[截断]");
                else
                    output.Append(stderr);
            }

            var exitCode = process.ExitCode;
            var isError = exitCode != 0;
            var resultText = string.IsNullOrEmpty(output.ToString())
                ? $"(命令完成, exit code: {exitCode})"
                : output.ToString();

            return new AgentToolResult<BashDetails>
            {
                Content = [new TextContent(resultText)],
                IsError = isError,
                Details = new BashDetails
                {
                    ExitCode = exitCode,
                    WorkingDirectory = startInfo.WorkingDirectory,
                    Shell = isWindows ? "powershell" : "bash",
                    TimedOut = false,
                },
            };
        }
        catch (OperationCanceledException)
        {
            return new AgentToolResult<BashDetails>
            {
                Content = [new TextContent("命令已取消")],
                IsError = true,
                Details = new BashDetails { ExitCode = -1, TimedOut = true },
            };
        }
        catch (Exception ex)
        {
            return new AgentToolResult<BashDetails>
            {
                Content = [new TextContent($"执行失败: {ex.Message}")],
                IsError = true,
                Details = new BashDetails { ExitCode = -1 },
            };
        }
    }

    /// <summary>
    /// 截断命令字符串，超过 max 长度时添加省略号（使用统一的 StringHelper.Truncate）。
    /// </summary>
    private static string TruncateCommand(string cmd, int max = 80) => cmd.Truncate(max);
}

/// <summary>
/// Bash 命令执行工具的参数。
/// </summary>
public sealed record BashParams
{
    [Description(
        "要执行的命令。⚠️ Windows 下是 PowerShell 5.x 语法而非 bash：ls -la 用 Get-ChildItem、&& 用 ; 分隔、单引号用双引号。列目录/读文件请用 read 工具"
    )]
    public string Command { get; init; } = "";

    [Description("工作目录（默认当前目录）。@ 前缀从工作目录解析")]
    public string? WorkingDirectory { get; init; }

    [Description("超时毫秒数（默认 30000）")]
    public int? TimeoutMs { get; init; }

    [Description("环境变量（键值对）")]
    public Dictionary<string, string>? Env { get; init; }
}

/// <summary>
/// Bash 命令执行工具的返回详情。
/// </summary>
public sealed record BashDetails
{
    public int ExitCode { get; init; }
    public string WorkingDirectory { get; init; } = "";
    public string Shell { get; init; } = "";
    public bool TimedOut { get; init; }
}
