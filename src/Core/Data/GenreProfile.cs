using System.Collections.Frozen;

namespace Core.Data;

/// <summary>
/// 题材身份配置 — CHANGE_07 裁剪版：只保留 Id/Name/Description 身份字段。
/// 完整配置（爽点/钩子/微兑现/节奏红线/压扬比例）已迁移到 云端私有仓库（云端私有引擎/Profiles/GenreProfileData）。
/// </summary>
public sealed record GenreProfile
{
    /// <summary>题材标识（一级分类中文名，如"玄幻"）</summary>
    public string Id { get; init; } = "";

    /// <summary>题材中文名</summary>
    public string Name { get; init; } = "";

    /// <summary>题材描述</summary>
    public string Description { get; init; } = "";

    // ── 静态注册表（身份字段 only） ──

    private static readonly FrozenDictionary<string, GenreProfile> s_profiles = BuildProfiles();

    /// <summary>
    /// 按 ID（一级分类中文名）查找题材配置。找不到返回 null。
    /// </summary>
    public static GenreProfile? FindById(string id) => s_profiles.GetValueOrDefault(id);

    /// <summary>
    /// 获取所有内置题材配置。
    /// </summary>
    public static IReadOnlyCollection<GenreProfile> All => s_profiles.Values;

    /// <summary>
    /// 从 Novel.Genre 字段解析题材配置。
    /// Genre 存储二级分类名（如"东方玄幻"），本方法查找其一级分类再匹配 profile。
    /// 无法识别时返回中性配置（无题材 delta）。
    /// </summary>
    public static GenreProfile FromGenre(string genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return s_profiles["通用"];

        // 精确匹配一级分类名（profile key）
        if (s_profiles.TryGetValue(genre, out var exact))
            return exact;

        // 二级分类名 → 查找一级分类 → 匹配 profile
        var parent = GenreDefinitions.FindParent(genre);
        if (parent is not null && s_profiles.TryGetValue(parent.Name, out var profile))
            return profile;

        // 模糊关键词匹配（兜底）
        return genre switch
        {
            not null when genre.Contains('仙', StringComparison.Ordinal) => s_profiles["仙侠"],
            not null when genre.Contains('玄', StringComparison.Ordinal) => s_profiles["玄幻"],
            not null when genre.Contains('修', StringComparison.Ordinal) => s_profiles["仙侠"],
            not null when genre.Contains('悬', StringComparison.Ordinal) => s_profiles["悬疑"],
            not null when genre.Contains('推', StringComparison.Ordinal) => s_profiles["悬疑"],
            not null when genre.Contains('言', StringComparison.Ordinal) => s_profiles["言情"],
            not null when genre.Contains('甜', StringComparison.Ordinal) => s_profiles["言情"],
            not null when genre.Contains('虐', StringComparison.Ordinal) => s_profiles["言情"],
            not null when genre.Contains('都', StringComparison.Ordinal) => s_profiles["都市"],
            not null when genre.Contains('电', StringComparison.Ordinal) => s_profiles["游戏"],
            not null when genre.Contains('穿', StringComparison.Ordinal) => s_profiles["历史"],
            not null when genre.Contains('游', StringComparison.Ordinal) => s_profiles["游戏"],
            not null when genre.Contains('克', StringComparison.Ordinal) => s_profiles["悬疑"],
            not null when genre.Contains('规', StringComparison.Ordinal) => s_profiles["悬疑"],
            not null when genre.Contains('怪', StringComparison.Ordinal) => s_profiles["悬疑"],
            not null when genre.Contains('爽', StringComparison.Ordinal) => s_profiles["系统流"],
            not null when genre.Contains('系', StringComparison.Ordinal) => s_profiles["系统流"],
            not null when genre.Contains('种', StringComparison.Ordinal) => s_profiles["种田文"],
            not null when genre.Contains('异', StringComparison.Ordinal) => s_profiles["二次元"],
            not null when genre.Contains('学', StringComparison.Ordinal) => s_profiles["二次元"],
            not null when genre.Contains('科', StringComparison.Ordinal) => s_profiles["科幻"],
            not null when genre.Contains('末', StringComparison.Ordinal) => s_profiles["科幻"],
            not null when genre.Contains('武', StringComparison.Ordinal) => s_profiles["武侠"],
            not null when genre.Contains('奇', StringComparison.Ordinal) => s_profiles["奇幻"],
            not null when genre.Contains('史', StringComparison.Ordinal) => s_profiles["历史"],
            _ => s_profiles["通用"], // 无法识别时返回中性配置
        };
    }

    private static FrozenDictionary<string, GenreProfile> BuildProfiles() =>
        new Dictionary<string, GenreProfile>
        {
            ["通用"] = new()
            {
                Id = "通用",
                Name = "通用",
                Description = "无题材差异化",
            },
            ["玄幻"] = new()
            {
                Id = "玄幻",
                Name = "玄幻",
                Description = "越级反杀+扮猪吃虎，每15章里程碑",
            },
            ["奇幻"] = new()
            {
                Id = "奇幻",
                Name = "奇幻",
                Description = "世界观探索+魔法体系，偏西方幻想",
            },
            ["武侠"] = new()
            {
                Id = "武侠",
                Name = "武侠",
                Description = "江湖恩怨+武学突破，传统侠义",
            },
            ["仙侠"] = new()
            {
                Id = "仙侠",
                Name = "仙侠",
                Description = "修真悟道+越级反杀，仙侠特色节奏",
            },
            ["都市"] = new()
            {
                Id = "都市",
                Name = "都市",
                Description = "扮猪吃虎+装逼打脸，都市背景下的爽感",
            },
            ["现实"] = new()
            {
                Id = "现实",
                Name = "现实",
                Description = "现实题材，偏文学性，节奏慢",
            },
            ["军事"] = new()
            {
                Id = "军事",
                Name = "军事",
                Description = "热血战斗+战略博弈，硬核军事",
            },
            ["历史"] = new()
            {
                Id = "历史",
                Name = "历史",
                Description = "打脸权威+扮猪吃虎，知识差是核心优势",
            },
            ["游戏"] = new()
            {
                Id = "游戏",
                Name = "游戏",
                Description = "高频爽点+系统反馈，升级即爽感",
            },
            ["体育"] = new()
            {
                Id = "体育",
                Name = "体育",
                Description = "竞技热血+逆袭夺冠",
            },
            ["科幻"] = new()
            {
                Id = "科幻",
                Name = "科幻",
                Description = "科技探索+星际冒险，偏硬核",
            },
            ["悬疑"] = new()
            {
                Id = "悬疑",
                Name = "悬疑",
                Description = "低爽点高悬念，信息差是核心驱动力",
            },
            ["言情"] = new()
            {
                Id = "言情",
                Name = "言情",
                Description = "甜蜜超预期+身份掉马，情绪驱动",
            },
            ["轻小说"] = new()
            {
                Id = "轻小说",
                Name = "轻小说",
                Description = "日系风格，轻松日常+萌元素",
            },
            ["二次元"] = new()
            {
                Id = "二次元",
                Name = "二次元",
                Description = "异世界+学园+转生，日系网文",
            },
            ["无限流"] = new()
            {
                Id = "无限流",
                Name = "无限流",
                Description = "副本穿梭+团队协作，不断变强",
            },
            ["系统流"] = new()
            {
                Id = "系统流",
                Name = "系统流",
                Description = "高频爽点，读者追求即时满足",
            },
            ["种田文"] = new()
            {
                Id = "种田文",
                Name = "种田文",
                Description = "经营建设为主，慢节奏满足感",
            },
            ["耽美"] = new()
            {
                Id = "耽美",
                Name = "耽美",
                Description = "高压低释放，身份掉马是核心爽点",
            },
            ["百合"] = new()
            {
                Id = "百合",
                Name = "百合",
                Description = "女性情感，细腻情绪驱动",
            },
        }.ToFrozenDictionary();
}
