using StabilityHarness.Retrieval;

namespace StabilityHarness;

/// <summary>
/// 命令行入口（默认无参数时打印用法）。
///
/// <code>
/// dotnet run --project src/RetrievalEval -c Release -- retrieval-eval \
///     --db ~/.mythology/vectors.db --novel 2c2410d54e08 [--gold strict|loose] [--candidate 100] [--verbose]
/// </code>
///
/// 只读打开数据库（mode=ro），不会修改库文件。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        return args[0] switch
        {
            "retrieval-eval" => await RetrievalEvalCommandAsync(args[1..]),
            _ => Unknown(args[0]),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine(
            "  dotnet run --project src/RetrievalEval -c Release -- retrieval-eval "
                + "--db <vectors.db> --novel <novelId> [--gold strict|loose] [--candidate 100] [--verbose]"
        );
        Console.WriteLine();
        Console.WriteLine("retrieval-eval 输出三个方案在 R@5 / R@10 / AE@5 / MRR 上的对比：");
        Console.WriteLine("  A 基线           默认词典 + 最佳路径分词");
        Console.WriteLine("  B 现行           实体名注册 + 索引侧 2/3 字片段");
        Console.WriteLine(
            "  C 现行 + 二轮扩展 首轮 BM25 挖词项 → 二轮查询 → 交错合并（SAG2 式，不加 LLM）"
        );
    }

    private static async Task<int> RetrievalEvalCommandAsync(string[] args)
    {
        string? db = null;
        string? novel = null;
        var gold = GoldMode.Strict;
        var candidateK = 100;
        var verbose = false;
        string? jevUrl = null;
        var jevKey = "local";
        var jevModel = "d1-omni";
        var jevCandidates = 12;
        var jevMaxState = 1200;
        var jevWeight = 0.5;
        string? embUrl = null;
        var embKey = "";
        var embModel = "wemm-embedding";
        var jevCriteria = "relevant";

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--db":
                    db = args[++i];
                    break;
                case "--novel":
                    novel = args[++i];
                    break;
                case "--gold":
                    gold = args[++i] switch
                    {
                        "strict" => GoldMode.Strict,
                        "loose" => GoldMode.Loose,
                        var other => throw new ArgumentException($"未知 gold 口径：{other}"),
                    };
                    break;
                case "--candidate":
                    candidateK = int.Parse(args[++i]);
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--jev-url":
                    jevUrl = args[++i];
                    break;
                case "--jev-key":
                    jevKey = args[++i];
                    break;
                case "--jev-model":
                    jevModel = args[++i];
                    break;
                case "--jev-candidates":
                    jevCandidates = int.Parse(args[++i]);
                    break;
                case "--jev-maxstate":
                    jevMaxState = int.Parse(args[++i]);
                    break;
                case "--jev-weight":
                    jevWeight = double.Parse(args[++i]);
                    break;
                case "--jev-criteria":
                    jevCriteria = args[++i];
                    break;
                case "--emb-url":
                    embUrl = args[++i];
                    break;
                case "--emb-key":
                    embKey = args[++i];
                    break;
                case "--emb-model":
                    embModel = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"未知参数：{args[i]}");
                    return 1;
            }

        if (string.IsNullOrWhiteSpace(db) || string.IsNullOrWhiteSpace(novel))
        {
            Console.Error.WriteLine("缺少 --db 或 --novel");
            PrintUsage();
            return 1;
        }

        try
        {
            var jev = string.IsNullOrWhiteSpace(jevUrl)
                ? null
                : new JevOptions(
                    jevUrl,
                    jevKey,
                    jevModel,
                    MaxStateChars: jevMaxState,
                    MaxCandidates: jevCandidates,
                    Weight: jevWeight,
                    Criteria: jevCriteria
                );
            var emb = string.IsNullOrWhiteSpace(embUrl)
                ? null
                : new EmbeddingOptions(embUrl, embKey, embModel);
            var (metrics, details) = await RetrievalEval.RunAsync(
                db,
                novel,
                gold,
                candidateK,
                jev,
                emb
            );

            Console.WriteLine($"语料：{db}  novel={novel}  gold={gold}  candidateK={candidateK}");
            Console.WriteLine();
            Console.WriteLine(
                "方案".PadRight(38)
                    + "查询数".PadRight(8)
                    + "R@5".PadRight(8)
                    + "R@10".PadRight(8)
                    + "AE@5".PadRight(8)
                    + "MRR"
            );
            foreach (var m in metrics)
                Console.WriteLine(
                    m.Arm.PadRight(38)
                        + m.Queries.ToString().PadRight(8)
                        + m.Recall5.ToString("P0").PadRight(8)
                        + m.Recall10.ToString("P0").PadRight(8)
                        + m.AllAt5.ToString("P0").PadRight(8)
                        + m.Mrr.ToString("F3")
                );

            if (verbose)
            {
                Console.WriteLine();
                Console.WriteLine("逐条（A/B/C 为 first-gold 排名，0 表示未命中）：");
                foreach (var line in details)
                    Console.WriteLine("  " + line);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"评测失败：{ex.Message}");
            return 2;
        }
    }
}
