namespace Core.Data.Migrations;

/// <summary>
/// 数据迁移接口。每个实现对应一个版本的数据格式转换。
/// 迁移必须幂等——对已转换的数据重复执行不报错。
/// </summary>
public interface IDataMigration
{
    /// <summary>迁移版本号，全局唯一且递增。</summary>
    int Version { get; }

    /// <summary>迁移名称，格式如 "001_SessionSegments"。用于迁移记录追踪。</summary>
    string Name { get; }

    /// <summary>迁移描述，用于日志输出。</summary>
    string Description { get; }

    /// <summary>
    /// 日志输出流。MigrationRunner 在执行迁移前设置此属性，
    /// 迁移实现可通过此流输出详细进度信息。
    /// </summary>
    TextWriter? Log { get; set; }

    /// <summary>
    /// 执行迁移。
    /// </summary>
    /// <param name="mythologyDir">~/.mythology 目录路径。</param>
    /// <param name="novelsRoot">novels 根目录路径。</param>
    /// <param name="ct">取消令牌。</param>
    Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct);
}
