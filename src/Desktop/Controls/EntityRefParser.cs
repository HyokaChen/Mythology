using System.Text.RegularExpressions;
using Core.Data;

namespace Desktop.Controls;

/// <summary>
/// # 实体引用补全语法（对齐 TUI TuiApp.Completion.cs 与 docs/architecture.md §13）：
/// 一级 `#前缀` → 类型显示名；二级 `#类型:前缀` → 该类型实体名。
/// 纯函数：不碰存储与 UI；补全候选与发送内联共用同一份语法定义。
/// </summary>
public static partial class EntityRefParser
{
    /// <summary>一级补全的类型显示名（顺序与 TUI EntityDisplayHelper.HashDisplayTypes 一致）。</summary>
    public static readonly string[] DisplayTypes =
    [
        "角色",
        "组织",
        "地点",
        "情节",
        "道具",
        "关系",
        "大纲",
    ];

    /// <summary>补全阶段。</summary>
    public enum Stage
    {
        /// <summary>光标前不是 # 引用。</summary>
        None,

        /// <summary>一级：#前缀（类型名）。</summary>
        Type,

        /// <summary>二级：#类型:名称前缀。</summary>
        Name,
    }

    /// <summary>解析结果：Start = '#' 下标，Query = 当前阶段的前缀，TypeDisplay = 类型显示名（仅二级）。</summary>
    public readonly record struct Result(Stage Stage, int Start, string TypeDisplay, string Query)
    {
        /// <summary>未命中。</summary>
        public static readonly Result None = new(Stage.None, 0, "", "");
    }

    /// <summary>发送内联正则（与 TUI TuiApp.Input.cs 的 EntityRefRegex 同款）。</summary>
    [GeneratedRegex(@"#([\p{L}]+):([^\s\]\)]+)")]
    public static partial Regex InlineRegex();

    /// <summary>解析光标前的 # 引用：引用必须紧贴光标结束，且 # 之后不得出现空白。</summary>
    public static Result TryParse(string text, int caretIndex)
    {
        if (caretIndex <= 0 || text.Length == 0)
            return Result.None;

        var before = caretIndex >= text.Length ? text : text[..caretIndex];
        var hash = before.LastIndexOf('#');
        if (hash < 0)
            return Result.None;

        var afterHash = before[(hash + 1)..];
        if (afterHash.Any(char.IsWhiteSpace))
            return Result.None;

        var colon = afterHash.IndexOf(':');
        if (colon < 0)
            // 一级：# 后只能是类型名的一部分；空串 = 列出全部类型
            return afterHash.All(char.IsLetter)
                ? new Result(Stage.Type, hash, "", afterHash)
                : Result.None;

        var typeDisplay = afterHash[..colon];
        var query = afterHash[(colon + 1)..];
        if (typeDisplay.Length == 0 || !typeDisplay.All(char.IsLetter))
            return Result.None; // `#:` / `#1:` 不触发（ARCHITECTURE 13.5）
        if (query.Any(c => c is ']' or ')'))
            return Result.None;
        return new Result(Stage.Name, hash, typeDisplay, query);
    }

    /// <summary>显示名 → 实体类型（7 项，与 TUI EntityDisplayHelper.HashDisplayToEntityTypeMap 一致）。</summary>
    public static EntityType? ToEntityType(string display) =>
        display switch
        {
            "角色" => EntityType.Character,
            "组织" => EntityType.Faction,
            "地点" => EntityType.Location,
            "情节" => EntityType.Plotline,
            "道具" => EntityType.Item,
            "关系" => EntityType.Relationship,
            "大纲" => EntityType.Chapter,
            _ => null,
        };

    /// <summary>一级选中：把 [Start, caret) 的 "#前缀" 换成 "#类型:"（无尾随空格 → 立刻进入二级）。</summary>
    public static (string Text, int Caret) InsertType(
        string text,
        int caretIndex,
        in Result reference,
        string displayType
    )
    {
        var inserted = "#" + displayType + ":";
        return (
            text[..reference.Start] + inserted + text[caretIndex..],
            reference.Start + inserted.Length
        );
    }

    /// <summary>二级选中：把 [Start, caret) 的 "#类型:前缀" 换成 "#类型:名称 "（尾随空格 → 退出补全）。</summary>
    public static (string Text, int Caret) InsertName(
        string text,
        int caretIndex,
        in Result reference,
        string name
    )
    {
        var inserted = "#" + reference.TypeDisplay + ":" + name + " ";
        return (
            text[..reference.Start] + inserted + text[caretIndex..],
            reference.Start + inserted.Length
        );
    }
}
