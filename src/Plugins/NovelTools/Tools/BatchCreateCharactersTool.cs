using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 批量创建角色（一次 tool call 创建多个角色）。
/// </summary>
public sealed class BatchCreateCharactersTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<BatchCreateCharactersParams, BatchCreateCharactersDetails>
{
    public override string Name => "batch_create_characters";
    public override string[] Aliases => ["批量创建角色", "多个角色"];
    public override string[] ExampleQueries => ["一次创建五个配角"]; // 工具唯一标识
    public override string Label => "批量创建角色"; // 工具显示名称
    public override string Description =>
        "一次创建多个角色。每个角色只需提供核心字段，系统会自动补全 ID 并索引。适合批量建立角色池"
        + BatchToolHints.PreferBatch;
    public override ToolCategory Category => ToolCategory.EntityCrud; // 分类：实体CRUD
    public override string EntityType => "character"; // 实体类型：角色
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<BatchCreateCharactersDetails>> ExecuteAsync(
        string toolCallId,
        BatchCreateCharactersParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchCreateCharactersDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 参数校验：至少提供一个角色
        if (args.Characters is null || args.Characters.Count == 0)
        {
            return new AgentToolResult<BatchCreateCharactersDetails>
            {
                Content = [new TextContent("请提供至少一个角色")],
                IsError = true,
            };
        }

        // 初始化结果收集器
        var createdIds = new List<string>(args.Characters.Count);
        var createdNames = new List<string>(args.Characters.Count);
        var failed = new List<string>();

        // 遍历每个角色草稿，逐个创建
        foreach (var c in args.Characters)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                failed.Add("(空名称)");
                continue;
            }

            // 构建 Character 实体，自动补全默认值
            var character = new Character
            {
                NovelId = args.NovelId,
                Name = c.Name.Trim(),
                Gender = c.Gender ?? "",
                Age = c.Age ?? "",
                Role = Enum.TryParse<CharacterRole>(c.Role, ignoreCase: true, out var cr)
                    ? cr
                    : CharacterRole.Supporting, // 默认配角
                Personality = c.Personality ?? "",
                Appearance = c.Appearance ?? "",
                Background = c.Background ?? "",
                Motivation = c.Motivation ?? "",
                Habits = c.Habits ?? "",
                Weakness = c.Weakness ?? "",
                FactionId = c.FactionId,
            };

            try
            {
                // 持久化角色并建立向量索引
                await store.SaveCharacter(character, ct);
                ThrowIfCancelled(ct);
                if (kernel is not null)
                {
                    var (doc, chunks) = kernel.Builder.BuildCharacter(character);
                    await kernel.IndexDocumentAsync(doc, chunks, ct);
                }

                createdIds.Add(character.Id);
                createdNames.Add(character.Name);
            }
            catch (Exception ex)
            {
                failed.Add($"{c.Name.Trim()}: {ex.Message}");
            }
        }

        // 构建操作结果摘要
        var summary =
            $"已创建 {createdIds.Count} 个角色"
            + (createdNames.Count > 0 ? $": {string.Join("、", createdNames)}" : "")
            + (failed.Count > 0 ? $"\n失败 {failed.Count} 个: {string.Join("；", failed)}" : "");

        return new AgentToolResult<BatchCreateCharactersDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchCreateCharactersDetails
            {
                CreatedCount = createdIds.Count,
                CreatedIds = createdIds.ToArray(),
                FailedCount = failed.Count,
            },
        };
    }
}

public sealed record BatchCreateCharactersParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要创建的角色列表（每个角色至少提供 name）")]
    public List<CharacterDraft>? Characters { get; init; }
}

/// <summary>
/// 角色草稿 — 用于批量创建时的输入数据结构。
/// </summary>
public sealed record CharacterDraft
{
    [Description("角色姓名（必填）")]
    public string? Name { get; init; } // 角色姓名（必填）

    [Description("性别：男/女/其他。影响代词和关系逻辑")]
    public string? Gender { get; init; } // 性别

    [Description("年龄或年龄段，如「25」「少年」「中年」")]
    public string? Age { get; init; } // 年龄

    [Description(
        "角色定位：protagonist（主角）/ supporting（配角）/ minor（龙套）/ antagonist（反派）"
    )]
    public string? Role { get; init; } // 角色定位

    [Description("性格描述，如「内向敏感、外冷内热」")]
    public string? Personality { get; init; } // 性格

    [Description("外貌描述，如「黑发碧眼、身材高挑」")]
    public string? Appearance { get; init; } // 外貌

    [Description("背景故事")]
    public string? Background { get; init; } // 背景

    [Description("核心动机/目标")]
    public string? Motivation { get; init; } // 动机

    [Description("习惯/行为特点，如「晨练、喜好喝茶」")]
    public string? Habits { get; init; } // 习惯

    [Description("弱点/缺陷，如「固执、过于自信」")]
    public string? Weakness { get; init; } // 弱点

    [Description("所属组织/门派ID")]
    public string? FactionId { get; init; } // 所属组织
}

/// <summary>
/// 批量创建角色结果详情。
/// </summary>
public sealed record BatchCreateCharactersDetails
{
    public int CreatedCount { get; init; } // 成功创建数量
    public string[] CreatedIds { get; init; } = []; // 成功创建的ID列表
    public int FailedCount { get; init; } // 失败数量
}
