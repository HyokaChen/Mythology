using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Plugins;
using Core.Shared;
using Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Plugins.WritingTools.Enhance;

// 权限声明 — 编译进 DLL
[assembly: PluginPermission(PluginPermissions.NovelRead)]
[assembly: PluginPermission(PluginPermissions.NovelWrite)]
[assembly: PluginPermission(PluginPermissions.CloudHarness)]

namespace Plugins.WritingTools;

/// <summary>
/// 写作增强工具插件：提供复合诊断和维度增强工具集。
/// CHANGE_07：诊断全部走云端（IFictionHarnessClient），本地保留增强/写回/快照。
/// </summary>
public sealed class WritingToolsPlugin : IToolPlugin
{
    private INovelStore _store = null!;
    private Agent? _agent;
    private IFictionHarnessClient? _cloudHarness;

    /// <summary>
    /// 初始化插件，从 DI 容器获取 INovelStore、可选的 Agent 和 IFictionHarnessClient。
    /// </summary>
    public void Initialize(IServiceProvider sp)
    {
        _store = sp.GetRequiredService<INovelStore>();
        _agent = sp.GetService<Agent>();
        _cloudHarness = sp.GetService<IFictionHarnessClient>();
    }

    /// <summary>
    /// 获取插件提供的所有工具列表。
    /// CHANGE_07：所有诊断/增强工具注入 IFictionHarnessClient。
    /// </summary>
    public IEnumerable<AgentTool> GetTools()
    {
        yield return new DiagnoseWritingTool(_store, _agent, _cloudHarness);

        if (_agent is not null && _cloudHarness is not null)
        {
            foreach (var cfg in DimensionConfigs)
                yield return new EnhanceDimensionTool(
                    _store,
                    _agent,
                    _cloudHarness,
                    cfg.Name,
                    cfg.Dimension,
                    cfg.DescriptionDetail,
                    cfg.DiagnosingMessage,
                    cfg.NoIssueMessage
                );
        }
    }

    private static readonly DimensionConfig[] DimensionConfigs =
    [
        new(
            "enhance_immersion",
            DimensionKeys.Immersion,
            "修复精确数字、作者介入、行为缺乏铺垫等问题",
            "正在诊断代入感问题…",
            "代入感评分良好，无需增强"
        ),
        new(
            "enhance_satisfaction",
            DimensionKeys.Satisfaction,
            "完善伏笔回收、情绪释放、结局补偿等",
            "正在诊断满意度问题…",
            "满意度评分良好，无需增强"
        ),
        new(
            "enhance_pacing",
            DimensionKeys.Pacing,
            "优化节奏密度、句子长短、段落节奏",
            "正在诊断节奏问题…",
            "节奏评分良好，无需增强"
        ),
        new(
            "enhance_description",
            DimensionKeys.Description,
            "增强环境描写、感官细节、氛围营造",
            "正在诊断描写问题…",
            "描写评分良好，无需增强"
        ),
        new(
            "enhance_dialogue",
            DimensionKeys.Dialogue,
            "优化对话自然度、个性化、潜台词",
            "正在诊断对话问题…",
            "对话评分良好，无需增强"
        ),
        new(
            "enhance_foreshadowing",
            DimensionKeys.Foreshadowing,
            "增强伏笔布局、线索规划、回顾呼应",
            "正在诊断伏笔问题…",
            "伏笔评分良好，无需增强"
        ),
        new(
            "enhance_humanizer",
            DimensionKeys.Humanizer,
            "替换AI高频词/打破公式化句式/去宣传语/删除填充短语/注入个性",
            "正在检测机器化写作风险…",
            "未检测到机器化写作风险，无需增强"
        ),
        new(
            "enhance_prose_quality",
            DimensionKeys.ProseQuality,
            "句调整/Show替Tell/修辞精简/呼吸感优化",
            "正在诊断文笔质量问题…",
            "文笔质量评分良好，无需增强"
        ),
        new(
            "enhance_chapter_hook",
            DimensionKeys.ChapterHook,
            "章末植钩子(危机/悬念/渴望/情绪/选择)",
            "正在检测章末钩子…",
            "章末钩子评分良好，无需增强"
        ),
    ];

    private sealed record DimensionConfig(
        string Name,
        string Dimension,
        string DescriptionDetail,
        string DiagnosingMessage,
        string NoIssueMessage
    );
}
