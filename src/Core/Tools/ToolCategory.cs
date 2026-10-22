namespace Core.Tools;

/// <summary>
/// 工具分类枚举。
/// 用于对工具进行逻辑分组，便于管理和展示。
/// </summary>
public enum ToolCategory
{
    /// <summary>通用工具，不属于任何特定分类。</summary>
    Generic,

    /// <summary>文件编辑工具，用于修改文件内容。</summary>
    FileEdit,

    /// <summary>实体增删改查工具，用于管理小说中的实体（角色、地点等）。</summary>
    EntityCrud,

    /// <summary>章节管理工具，用于管理小说章节。</summary>
    ChapterManagement,

    /// <summary>实体读取工具，用于查询和读取实体信息。</summary>
    EntityRead,

    /// <summary>搜索工具，用于搜索和查询。</summary>
    SearchTool,

    /// <summary>Bash 命令工具，用于执行系统命令。</summary>
    BashTool,
}
