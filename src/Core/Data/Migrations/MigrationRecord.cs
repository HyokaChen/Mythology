namespace Core.Data.Migrations;

/// <summary>
/// 单条迁移应用记录。对应 version.json 中 appliedMigrations 数组的每一项。
/// EF Core 风格的迁移追踪——每次成功执行迁移后追加一条记录，
/// 而非仅更新一个版本号数字。
/// </summary>
public sealed class MigrationRecord
{
    /// <summary>迁移版本号（递增序号）。</summary>
    public int Version { get; init; }

    /// <summary>迁移名称，如 "001_SessionSegments"。</summary>
    public string Name { get; init; } = "";

    /// <summary>迁移描述。</summary>
    public string Description { get; init; } = "";

    /// <summary>迁移应用时间（UTC）。</summary>
    public DateTime AppliedAt { get; init; }

    /// <summary>迁移执行耗时（毫秒）。</summary>
    public long DurationMs { get; init; }

    /// <summary>执行结果（"success" / "failed"）。</summary>
    public string Result { get; init; } = "success";
}

/// <summary>
/// version.json 根结构。
/// </summary>
public sealed class MigrationHistory
{
    /// <summary>已应用的迁移记录列表。</summary>
    public List<MigrationRecord> AppliedMigrations { get; init; } = [];
}
