namespace Core.Data;

/// <summary>
/// 题材定义中心 — 统一管理所有题材的中文名和层级关系。
/// 全项目唯一真相源（Single Source of Truth）。
/// Novel.Genre 存储二级分类中文名（如"东方玄幻"）。
/// </summary>
public static class GenreDefinitions
{
    /// <summary>一级题材分类。</summary>
    public sealed class Category
    {
        /// <summary>一级分类中文名（如"玄幻"）。</summary>
        public string Name { get; }

        /// <summary>该一级分类下的所有二级分类。</summary>
        public Subcategory[] Subcategories { get; }

        public Category(string name, Subcategory[] subs)
        {
            Name = name;
            Subcategories = subs;
            foreach (var sub in subs)
                sub.Parent = this;
        }
    }

    /// <summary>二级题材分类。</summary>
    public sealed class Subcategory(string name)
    {
        /// <summary>二级分类中文名（如"东方玄幻"）。</summary>
        public string Name { get; } = name;

        /// <summary>所属一级分类。</summary>
        public Category Parent { get; internal set; } = null!;
    }

    // ── 一级分类定义 ──

    public static readonly Category Xuanhuan = new(
        "玄幻",
        [
            new Subcategory("东方玄幻"),
            new Subcategory("异世大陆"),
            new Subcategory("王朝争霸"),
            new Subcategory("高武世界"),
        ]
    );

    public static readonly Category Qihuan = new(
        "奇幻",
        [
            new Subcategory("现代魔法"),
            new Subcategory("剑与魔法"),
            new Subcategory("史诗奇幻"),
            new Subcategory("黑暗幻想"),
        ]
    );

    public static readonly Category Wuxia = new(
        "武侠",
        [new Subcategory("传统武侠"), new Subcategory("武侠幻想"), new Subcategory("国术无双")]
    );

    public static readonly Category Xianxia = new(
        "仙侠",
        [
            new Subcategory("修真文明"),
            new Subcategory("幻想修仙"),
            new Subcategory("现代修真"),
            new Subcategory("神话修真"),
        ]
    );

    public static readonly Category Dushi = new(
        "都市",
        [
            new Subcategory("都市生活"),
            new Subcategory("都市异能"),
            new Subcategory("异术超能"),
            new Subcategory("青春校园"),
            new Subcategory("娱乐明星"),
            new Subcategory("商战职场"),
        ]
    );

    public static readonly Category Xianshi = new(
        "现实",
        [new Subcategory("社会乡土"), new Subcategory("生活随笔"), new Subcategory("成功励志")]
    );

    public static readonly Category Junshi = new(
        "军事",
        [
            new Subcategory("军旅生涯"),
            new Subcategory("战争幻想"),
            new Subcategory("抗战烽火"),
            new Subcategory("谍战特工"),
        ]
    );

    public static readonly Category Lishi = new(
        "历史",
        [
            new Subcategory("架空历史"),
            new Subcategory("秦汉三国"),
            new Subcategory("两晋隋唐"),
            new Subcategory("两宋元明"),
            new Subcategory("历史穿越"),
        ]
    );

    public static readonly Category Youxi = new(
        "游戏",
        [new Subcategory("电子竞技"), new Subcategory("虚拟网游"), new Subcategory("游戏系统")]
    );

    public static readonly Category Tiyu = new(
        "体育",
        [new Subcategory("篮球运动"), new Subcategory("足球运动"), new Subcategory("竞技赛事")]
    );

    public static readonly Category Kehuan = new(
        "科幻",
        [
            new Subcategory("星际文明"),
            new Subcategory("时空穿梭"),
            new Subcategory("进化变异"),
            new Subcategory("末世危机"),
            new Subcategory("超级科技"),
        ]
    );

    public static readonly Category Xuanyi = new(
        "悬疑",
        [
            new Subcategory("诡秘悬疑"),
            new Subcategory("侦探推理"),
            new Subcategory("探险生存"),
            new Subcategory("灵异未知"),
        ]
    );

    public static readonly Category Yanqing = new(
        "言情",
        [
            new Subcategory("古代言情"),
            new Subcategory("现代言情"),
            new Subcategory("幻想言情"),
            new Subcategory("甜宠"),
            new Subcategory("虐文"),
        ]
    );

    public static readonly Category QingXiaoshuo = new(
        "轻小说",
        [
            new Subcategory("原生幻想"),
            new Subcategory("青春日常"),
            new Subcategory("衍生同人"),
            new Subcategory("搞笑吐槽"),
        ]
    );

    public static readonly Category Erciyuan = new(
        "二次元",
        [
            new Subcategory("异世界"),
            new Subcategory("学园"),
            new Subcategory("恋爱喜剧"),
            new Subcategory("后宫"),
            new Subcategory("转生"),
        ]
    );

    public static readonly Category WuxianLiu = new(
        "无限流",
        [new Subcategory("无限流"), new Subcategory("诸天流"), new Subcategory("快穿")]
    );

    public static readonly Category XitongLiu = new(
        "系统流",
        [new Subcategory("签到流"), new Subcategory("任务流"), new Subcategory("商城流")]
    );

    public static readonly Category Zhongtian = new(
        "种田文",
        [new Subcategory("异界种田"), new Subcategory("古代种田"), new Subcategory("美食文")]
    );

    public static readonly Category Danmei = new(
        "耽美",
        [new Subcategory("现代耽美"), new Subcategory("古代耽美"), new Subcategory("玄幻耽美")]
    );

    public static readonly Category Baihe = new("百合", [new Subcategory("百合")]);

    /// <summary>所有一级分类（有序，用于 UI 下拉列表）。</summary>
    public static readonly Category[] All =
    [
        Xuanhuan,
        Qihuan,
        Wuxia,
        Xianxia,
        Dushi,
        Xianshi,
        Junshi,
        Lishi,
        Youxi,
        Tiyu,
        Kehuan,
        Xuanyi,
        Yanqing,
        QingXiaoshuo,
        Erciyuan,
        WuxianLiu,
        XitongLiu,
        Zhongtian,
        Danmei,
        Baihe,
    ];

    /// <summary>所有一级分类中文名列表（用于 UI Select）。</summary>
    public static string[] CategoryNames => [.. All.Select(c => c.Name)];

    /// <summary>根据一级分类名查找。</summary>
    public static Category? FindCategory(string name) => All.FirstOrDefault(c => c.Name == name);

    /// <summary>根据二级分类名查找其所属一级分类。</summary>
    public static Category? FindParent(string subcategoryName)
    {
        return All.FirstOrDefault(cat => cat.Subcategories.Any(sub => sub.Name == subcategoryName));
    }

    /// <summary>根据二级分类名查找对象。</summary>
    public static Subcategory? FindSubcategory(string name)
    {
        return All.SelectMany(cat => cat.Subcategories).FirstOrDefault(sub => sub.Name == name);
    }
}
