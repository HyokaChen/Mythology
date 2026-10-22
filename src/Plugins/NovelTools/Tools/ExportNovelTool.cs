using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Plugins.NovelTools.Tools.Formatters;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 小说导出工具：导出完整小说为 Markdown / TXT / JSON / EPUB 格式
/// </summary>
public sealed class ExportNovelTool(INovelStore store)
    : AgentTool<ExportNovelParams, ExportNovelDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "export_novel";

    /// <summary>工具显示名称</summary>
    public override string Label => "导出小说";
    public override string Description =>
        "导出完整小说为 Markdown / TXT / JSON / EPUB 格式，可选包含设定附录";
    public override ToolCategory Category => ToolCategory.Generic;

    /// <summary>
    /// 执行小说导出
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">导出参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>导出结果</returns>
    public override async Task<AgentToolResult<ExportNovelDetails>> ExecuteAsync(
        string toolCallId,
        ExportNovelParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ExportNovelDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var novel = await store.GetNovel(args.NovelId, ct);
        ThrowIfCancelled(ct);
        if (novel is null)
            return new AgentToolResult<ExportNovelDetails>
            {
                Content = [new TextContent($"小说 {args.NovelId} 不存在")],
                IsError = true,
            };

        var chapters = await store.GetChapters(args.NovelId, ct);
        ThrowIfCancelled(ct);

        // 过滤章节范围
        if (args.ChapterRange is { Count: 2 } range)
        {
            var from = range[0];
            var to = range[1];
            chapters = chapters.Where(c => c.Order >= from && c.Order <= to).ToList();
        }

        // 加载设定附录 — 并行加载附录数据
        List<Character> characters;
        List<Location> locations;
        List<Faction> factions;
        if (args.IncludeAppendix)
        {
            var charactersTask = store.GetCharacters(args.NovelId, ct);
            var locationsTask = store.GetLocations(args.NovelId, ct);
            var factionsTask = store.GetFactions(args.NovelId, ct);
            await Task.WhenAll(charactersTask, locationsTask, factionsTask);
            ThrowIfCancelled(ct);
            characters = charactersTask.Result;
            locations = locationsTask.Result;
            factions = factionsTask.Result;
        }
        else
        {
            characters = [];
            locations = [];
            factions = [];
        }

        var format = args.Format?.ToLowerInvariant() ?? "markdown";
        IFormatter formatter = format switch
        {
            "txt" or "text" => new TxtFormatter(),
            "json" => new JsonFormatter(),
            "epub" => new EpubFormatter(),
            _ => new MarkdownFormatter(),
        };

        var content = formatter.Generate(
            novel,
            chapters,
            new FormatterSettings
            {
                Characters = characters,
                Locations = locations,
                Factions = factions,
                IncludeAppendix = args.IncludeAppendix,
                IncludeMetadata = args.IncludeMetadata,
            }
        );

        var outputPath = args.OutputPath;
        if (!string.IsNullOrEmpty(outputPath))
        {
            await File.WriteAllTextAsync(outputPath, content, ct);
            var fileInfo = new FileInfo(outputPath);
            return new AgentToolResult<ExportNovelDetails>
            {
                Content =
                [
                    new TextContent(
                        $"小说「{novel.Title}」已导出 ({chapters.Count}章, {content.Length}字)\n"
                            + $"格式: {format}\n"
                            + $"路径: {outputPath}\n"
                            + $"大小: {fileInfo.Length} 字节"
                    ),
                ],
                Details = new ExportNovelDetails
                {
                    NovelId = novel.Id,
                    Title = novel.Title,
                    Format = format,
                    ChapterCount = chapters.Count,
                    TotalWords = content.Length,
                    OutputPath = outputPath,
                    FileSizeBytes = (int)fileInfo.Length,
                },
            };
        }

        // 无输出路径，直接返回内容（LLM 会在对话区展示）
        return new AgentToolResult<ExportNovelDetails>
        {
            Content = [new TextContent(content)],
            Details = new ExportNovelDetails
            {
                NovelId = novel.Id,
                Title = novel.Title,
                Format = format,
                ChapterCount = chapters.Count,
                TotalWords = content.Length,
            },
        };
    }
}

public sealed record ExportNovelParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("导出格式：markdown / txt / json / epub")]
    public string? Format { get; init; }

    [Description("可选：指定导出的章节范围，如 [1, 5] 表示导出第1-5章。留空导出全部")]
    public List<int>? ChapterRange { get; init; }

    [Description("可选：导出文件路径。留空则输出到对话区（不写入文件）")]
    public string? OutputPath { get; init; }

    [Description("是否包含设定附录（角色/地点/组织等）。默认 true")]
    public bool IncludeAppendix { get; init; } = true;

    [Description("是否包含章节元数据（标题/状态/字数）。默认 true")]
    public bool IncludeMetadata { get; init; } = true;
}

public sealed record ExportNovelDetails
{
    public string NovelId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Format { get; init; } = "";
    public int ChapterCount { get; init; }
    public int TotalWords { get; init; }
    public string? OutputPath { get; init; }
    public int FileSizeBytes { get; init; }
}

public sealed record FormatterSettings
{
    public List<Character> Characters { get; init; } = [];
    public List<Location> Locations { get; init; } = [];
    public List<Faction> Factions { get; init; } = [];
    public bool IncludeAppendix { get; init; } = true;
    public bool IncludeMetadata { get; init; } = true;
}

internal interface IFormatter
{
    string Generate(Novel novel, List<Chapter> chapters, FormatterSettings settings);
}
