namespace Core.Shared;

/// <summary>
/// 诊断维度键常量 — 所有消费方统一引用。
/// 键值为中文，确保注入 AI 上下文的维度标识可读。
/// 替代此前散布全代码的英文硬编码字符串（"immersion", "satisfaction"等）。
/// </summary>
public static class DimensionKeys
{
    // ── WritingTools 16 诊断维度 ──

    /// <summary>代入感 — 模糊细节 + 贴合读者 + 小缺陷 + 视角一致性</summary>
    public const string Immersion = "代入感";

    /// <summary>爽感 — 打脸/奇遇/升级/宝物/心动/发财</summary>
    public const string Satisfaction = "爽感";

    /// <summary>冲突 — 内心冲突 + 人际对抗 + 环境冲突</summary>
    public const string Conflict = "冲突";

    /// <summary>节奏 — 过山车原理 + 句子长短调控</summary>
    public const string Pacing = "节奏";

    /// <summary>描写 — 五感描写 + 四忌检测</summary>
    public const string Description = "描写";

    /// <summary>对话 — 自然度 + 可信度 + 承载性</summary>
    public const string Dialogue = "对话";

    /// <summary>伏笔 — 埋设 + 回收 + 巧合检测</summary>
    public const string Foreshadowing = "伏笔";

    /// <summary>角色弧 — 起点→驱动力→障碍→转折→终点</summary>
    public const string CharacterArc = "角色弧";

    /// <summary>开篇 — 主角登场 + 悬念 + 脱衣舞原则</summary>
    public const string Opening = "开篇";

    /// <summary>常识错误 — 8种毒性模式检测</summary>
    public const string Mistake = "常识错误";

    /// <summary>高潮 — 兴奋点三层次 + 结局类型</summary>
    public const string Excitement = "高潮";

    /// <summary>读者参与 — 期待感 + 好奇心 + 写作境界</summary>
    public const string ReaderEngagement = "读者参与";

    /// <summary>去AI味 — AI高频词/公式化句式/宣传语/填充短语</summary>
    public const string Humanizer = "去AI味";

    /// <summary>文笔质量 — 句法节拍 + 修辞密度 + Show vs Tell + 呼吸感</summary>
    public const string ProseQuality = "文笔质量";

    /// <summary>章末钩子 — 危机/悬念/渴望/情绪/选择五种钩子</summary>
    public const string ChapterHook = "章末钩子";

    /// <summary>信息差 — 读者知道≠角色知道</summary>
    public const string InfoAsymmetry = "信息差";

    /// <summary>
    /// 从 KeywordTag 位标志获取对应的中文维度键。
    /// </summary>
    public static string FromTag(ulong tag) =>
        tag switch
        {
            1UL << 0 => Immersion,
            1UL << 1 => Satisfaction,
            1UL << 3 => Conflict,
            1UL << 4 => Pacing,
            1UL << 5 => Description,
            1UL << 6 => Dialogue,
            1UL << 7 => Foreshadowing,
            1UL << 8 => CharacterArc,
            1UL << 9 => Opening,
            1UL << 10 => Mistake,
            1UL << 11 => Excitement,
            1UL << 12 => ReaderEngagement,
            1UL << 13 => Humanizer,
            1UL << 14 => ProseQuality,
            1UL << 15 => ChapterHook,
            1UL << 16 => InfoAsymmetry,
            _ => "",
        };
}
