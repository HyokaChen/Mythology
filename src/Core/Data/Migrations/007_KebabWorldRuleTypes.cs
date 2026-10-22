using Core.Shared;

namespace Core.Data.Migrations;

/// <summary>
/// 007：把 <c>world-rules.yaml</c> 的**规则类型值**统一为 kebab-case
/// （`travel_speed` → `travel-speed`、`location_distance` → `location-distance`）。
///
/// 规范键集与 <see cref="WorldRuleTypes.All"/> 一致（规则类型为开放词汇，未知类型原样保留）；
/// 云端 <c>StpChecker</c> 另做归一化比较以兼容旧客户端数据，故本迁移只影响本地文件。
/// 幂等：已是 kebab 时无操作；单文件读写失败跳过并记日志（不阻断启动）。
/// </summary>
public sealed class KebabWorldRuleTypes : IDataMigration
{
    public int Version => 7;
    public string Name => "007_KebabWorldRuleTypes";
    public string Description =>
        "world-rules.yaml 规则类型值统一 kebab-case（travel_speed / location_distance）";

    public TextWriter? Log { get; set; }

    /// <summary>历史值 → 规范键（测试断言其值必须落在 <see cref="WorldRuleTypes.All"/> 内）。</summary>
    internal static readonly Dictionary<string, string> LegacyValueMap = new(StringComparer.Ordinal)
    {
        ["travel_speed"] = "travel-speed",
        ["location_distance"] = "location-distance",
    };

    private static readonly string[] s_fields = ["type"];

    public Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var roots = MigrationWalkers.CollectRoots(mythologyDir, novelsRoot, Log);
        var scanned = 0;
        var rewritten = 0;
        var values = 0;

        foreach (var root in roots)
        {
            foreach (var file in MigrationWalkers.EnumerateDocs(root))
            {
                ct.ThrowIfCancellationRequested();
                if (
                    !Path.GetFileName(file)
                        .Equals("world-rules.yaml", StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                scanned++;
                string text;
                try
                {
                    text = AtomicFile.ReadAllText(file);
                }
                catch (Exception ex)
                {
                    Log?.WriteLine($"  [warn] 读取失败 {file}: {ex.Message}");
                    continue;
                }

                var lines = text.Replace("\r\n", "\n").Split('\n');
                var changed = 0;
                for (var i = 0; i < lines.Length; i++)
                {
                    var (line, n) = LocalKebabKeys.RewriteYamlValue(
                        lines[i],
                        s_fields,
                        LegacyValueMap
                    );
                    if (n > 0)
                    {
                        lines[i] = line;
                        changed += n;
                    }
                }
                if (changed == 0)
                    continue;
                try
                {
                    AtomicFile.WriteAllText(file, string.Join('\n', lines));
                    rewritten++;
                    values += changed;
                }
                catch (Exception ex)
                {
                    Log?.WriteLine($"  [warn] 写入失败 {file}: {ex.Message}");
                }
            }
        }

        Log?.WriteLine(
            $"  世界规则类型：扫描 {scanned} 个文件，改写 {rewritten} 个（值 {values} 处）。"
        );
        return Task.CompletedTask;
    }
}
