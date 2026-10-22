using Core.Data;
using Core.Shared;

namespace Core.Prompts;

/// <summary>
/// 增强生成 Prompt — 每个维度有专用生成指令。
/// </summary>
public static class EnhancementPrompt
{
    /// <summary>
    /// 构建增强生成 Prompt。
    /// </summary>
    /// <param name="dimension">维度名称</param>
    /// <param name="originalText">原始段落文本</param>
    /// <param name="beforeContext">前文上下文</param>
    /// <param name="afterContext">后文上下文</param>
    /// <param name="issueDescription">问题描述</param>
    /// <param name="profile">写作配置</param>
    public static string Build(
        string dimension,
        string originalText,
        string beforeContext,
        string afterContext,
        string issueDescription,
        WritingProfile profile
    )
    {
        var dimensionGuide = GetDimensionGuide(dimension);
        var profileGuide = GetProfileGuide(profile.EnhancementProfile);

        return $"""
你是一个小说写作增强助手。请根据以下信息，对指定段落进行改写增强。

## 上下文
**前文**: {Truncate(beforeContext, 200)}
**原文**: {originalText}
**后文**: {Truncate(afterContext, 200)}

## 问题
{issueDescription}

## 维度指导：{dimension}
{dimensionGuide}

## 配置指导
{profileGuide}

## 要求
1. 生成两个变体，分别用【保守版】和【适度版】标记
2. 保守版：最小改动，仅修复问题，保留原文风格和节奏
3. 适度版：适度增强，在修复问题的基础上提升整体效果
4. 保持情节不变、角色声音一致、与周围文本风格统一
5. 只输出改写后的段落，不要输出分析或说明
6. 每个变体必须是完整的段落，可直接替换原文
7. 除非用户明确要大幅度改写，默认推荐【保守版】（最小改动，保留原文）
""";
    }

    private static string GetDimensionGuide(string dimension) =>
        dimension switch
        {
            DimensionKeys.Immersion => """
- 将精确数字改为模糊描述（175cm→一人高，25岁→青年）
- 删除作者介入式表达（众所周知、显而易见→删除或改为角色视角）
- 补充可代入行为（下意识、忍不住、本能反应）
- 添加小缺陷（路痴、胆小、脸红）
- 角色行为必须有铺垫，删除"毫无理由""莫名其妙地"等
""",
            DimensionKeys.Satisfaction => """
- 增加打脸/反杀/揭穿类爽点（先抑后扬）
- 爽点密度：每千字至少1个爽感信号
- 递减收益：同类爽点不要连续出现，间隔至少500字
- 打脸比例：在所有爽点中占比不低于15%
- 奇遇/突破类爽点需要铺垫，不能凭空出现
""",
            DimensionKeys.Conflict => """
- 添加内心矛盾（犹豫、纠结、两难抉择）
- 增加人际对抗（争吵、对峙、威胁）
- 场景五步法：目标→障碍→努力→转折→结果
- 冲突升级：从言语→行动→生死
- 内心冲突 > 人际冲突 > 环境冲突（优先级）
""",
            DimensionKeys.Pacing => """
- 紧张段：短句、动作词、快节奏
- 平静段：长句、描写、慢节奏
- 张弛有度：紧张与平静交替，比例约6:4
- 高潮前必须有铺垫段
- 过渡段可以放慢，但不超过800字
""",
            DimensionKeys.Description => """
- 五感描写：至少覆盖3种感官（视觉+听觉+触觉/内心/嗅觉）
- 删除四忌：叙事中断、无关环境、无关细节、模糊空泛
- 具体化：用具体细节替代抽象形容词
- 角色视角：描写必须通过角色感知，不是旁白
- 环境描写必须与情节/情绪相关
""",
            DimensionKeys.Dialogue => """
- 删除说教式对话，改为潜台词和行动
- 信息倾倒拆分为多个短对话，融入冲突
- 区分角色语言：正式/口语/方言/口头禅
- 对话推动剧情：每段对话至少有一个"不""为什么""凭什么"
- 潜台词：用行动和暗示替代直接表白
""",
            DimensionKeys.Foreshadowing => """
- 添加伏笔暗示（物品、对话、环境细节）
- 伏笔必须与后续情节呼应
- 伏笔密度：每章至少1个新伏笔
- 伏笔类型：物品伏笔、对话伏笔、环境伏笔、角色行为伏笔
- 伏笔要自然，不能太明显也不能太隐晦
""",
            DimensionKeys.Humanizer => """
- 替换所有 AI 高频词为自然等价表达
- 打破公式化句式（二元对比→直接陈述，三项列举→两项/四项）
- 去除宣传语风格，改为具体描写或人物反应
- 删除填充短语，压缩冗余限定
- 删除谄媚语气和协作交流痕迹
- 保守版：仅替换/删除问题词，不改变句式结构
- 适度版：重写为人物化/场景化表达，注入个性
- 绝对不可引入新的 AI 高频词！改写后文本必须比原文更像人写的
""",
            DimensionKeys.ProseQuality => """
- 长短句机械交替→自然节奏(紧张=短句,舒缓=长句)
- Tell过多→Show(动作/表情/沉默)
- 修辞堆砌(>5次/百字)→精选1-2处
- 电报体(通篇≤5字)→补连贯长句
- 论文体(通篇>40字)→拆为短中交替
- 保守版：仅调整问题句式，保留原文内容
- 适度版：优化整体节奏和文笔质感
""",
            DimensionKeys.ChapterHook => """
- 章末无钩子→最后200字植悬念/危机/选择/渴望/情绪钩
- 题材适配: 悬疑→悬念钩, 言情→情绪钩, 爽文→渴望钩
- 保守版：仅追加1-2句钩子句
- 适度版：调整章末2-3段叙事，自然形成钩子
""",
            DimensionKeys.CharacterArc => """
- 五段闭环：起点→驱动力→障碍→转折→终点，缺哪段补哪段
- 角色行为必须符合动机链：外部目标 vs 内心需求
- 转折要有代价：角色的成长必须付出真实成本
- 保守版：仅强化缺失的弧线段
- 适度版：整体重排角色弧线节奏
""",
            DimensionKeys.Opening => """
- 主角三章内登场，五章内展现性格小高潮
- 定时炸弹：第一页埋悬念（滴答声），不急着引爆
- 脱衣舞原则：过程比结果重要，逐步揭示不一次给完
- 开局信息量：一页内交代「谁、在哪、要干嘛、有什么异常」
- 保守版：仅修复开篇缺失要素
- 适度版：重构开篇节奏与悬念布局
""",
            DimensionKeys.Mistake => """
- 修复常识错误：时间穿越、物理错误、身份矛盾、科技错位、职业常识、地理方位、时代背景、逻辑悖论
- 每处错误给出修正建议，不擅自更改情节
- 保守版：仅修正事实错误，不改句式
- 适度版：修正错误并平滑融入上下文
""",
            DimensionKeys.Excitement => """
- 兴奋点三层次：情绪唤起→认知张力→情感高潮
- 高潮前必须有铺垫（蓄力），高潮后留余波（消化）
- 结局类型与情绪目标匹配：爆发式/回味式/反转型
- 保守版：仅强化高潮段落的情绪张力
- 适度版：重排铺垫→爆发→余波的节奏
""",
            DimensionKeys.ReaderEngagement => """
- 期待感：章末钩子前置、目标明确、读者想翻页
- 好奇心：信息差驱动，读者想知道「然后呢」
- 写作境界：克制留白，不把话说尽
- 保守版：仅修复读者流失风险点
- 适度版：整体提升章节的阅读驱动力
""",
            DimensionKeys.InfoAsymmetry => """
- 信息差三信号：隐瞒（角色知道读者不知道）、误解（角色以为错了）、发现（信息翻转时刻）
- 读者知道≠角色知道：利用视角控制制造张力
- 信息差的代价：每个隐瞒要有揭示时机，不无限拖延
- 保守版：仅补充缺失的信息差信号
- 适度版：重排信息揭示的时机与方式
""",
            _ => "- 修复指定问题，保持原文风格",
        };

    private static string GetProfileGuide(string profile) =>
        profile switch
        {
            "commercial" => "商业向：大胆改写，优先读者体验，爽感优先，节奏紧凑",
            "literary" => "文学向：保守改写，保留原文韵味，注重细节和深度",
            "balanced" => "均衡向：适度改写，兼顾可读性和文学性",
            _ => "适度改写，保持原文风格",
        };

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return "（无）";
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
