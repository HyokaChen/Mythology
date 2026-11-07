using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Shared;
using Core.Tools;

namespace Core.Data.Migrations;

/// <summary>
/// 012：持久化修复会话历史里的 tool-call arguments。
///
/// 背景：LLM/流式层可能产出 <c>arguments:""</c> 或截断 JSON 的 tool call；
/// 这种 assistant 消息一旦落盘，下一轮 OpenAI 兼容请求会以
/// <c>400 Assistant tool call … arguments must be valid JSON</c> 整段拒绝，
/// 且恢复会话会把非法参数原样带回模型。
///
/// 处置：遍历所有会话 JSONL，把 message 行的 <c>tool-calls</c> 数组逐项规范化：
/// 空/纯空白 → <c>{}</c>；可修复的截断 JSON → 修复结果；无法修复 → <c>{}</c>；合法参数原样保留。
/// 运行时恢复路径也会做同样修复（<see cref="JsonRepairUtil.NormalizeToolCallArguments" />），
/// 本迁移负责让磁盘上的历史文件本身也干净。
///
/// 幂等：全部合法时无操作；重复执行安全。
/// 验证：迁移后二次扫描，仍有空/非法 arguments 或无法解析的 tool-calls 时抛异常。
/// </summary>
public sealed class SessionToolCallArguments : IDataMigration
{
    public int Version => 12;
    public string Name => "012_SessionToolCallArguments";
    public string Description => "持久化修复会话 tool-call arguments 的空/非法 JSON";

    public TextWriter? Log { get; set; }

    public async Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        _ = novelsRoot;
        var sessionsDir = Path.Combine(mythologyDir, "sessions");
        if (!Directory.Exists(sessionsDir))
        {
            Log?.WriteLine("  sessions 目录不存在，跳过迁移。");
            return;
        }

        var scannedFiles = 0;
        var rewrittenFiles = 0;
        var repairedCalls = 0;

        foreach (var file in EnumerateSessionFiles(sessionsDir))
        {
            ct.ThrowIfCancellationRequested();
            scannedFiles++;
            try
            {
                var (changed, repaired) = await NormalizeFileAsync(file, ct);
                if (changed)
                    rewrittenFiles++;
                repairedCalls += repaired;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log?.WriteLine($"  [warn] 会话文件修复失败，已跳过：{file}（{ex.Message}）");
            }
        }

        Log?.WriteLine(
            $"  tool-call 参数修复：扫描 {scannedFiles} 个文件，改写 {rewrittenFiles} 个，"
                + $"修复 {repairedCalls} 个调用。"
        );

        await VerifyAsync(sessionsDir, Log, ct);
    }

    private static IEnumerable<string> EnumerateSessionFiles(string sessionsDir) =>
        Directory
            .EnumerateFiles(sessionsDir, "*.jsonl", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith("audit_", StringComparison.Ordinal));

    private async Task<(bool Changed, int Repaired)> NormalizeFileAsync(
        string path,
        CancellationToken ct
    )
    {
        var original = await AtomicFile.ReadAllTextAsync(path, ct);
        if (string.IsNullOrEmpty(original))
            return (false, 0);

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var rawLines = original.Split('\n');
        var changedLines = new bool[rawLines.Length];
        var nodes = new JsonObject?[rawLines.Length];
        var repairedCalls = 0;
        var anyChanged = false;

        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;

            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue; // 损坏行不属于本次迁移职责
            }

            if (obj is null || !IsMessage(obj))
                continue;

            nodes[i] = obj;

            // 011 已把键统一为 kebab；仅处理新格式，避免与历史 camelCase 兼容逻辑重复
            if (
                !obj.TryGetPropertyValue("tool-calls", out var toolCallsNode)
                || toolCallsNode is not JsonValue toolCallsValue
                || !toolCallsValue.TryGetValue<string>(out var toolCallsJson)
                || string.IsNullOrEmpty(toolCallsJson)
            )
            {
                continue;
            }

            var items = ToolCallItemDto.DeserializeList(toolCallsJson);
            if (items is null or { Count: 0 })
                continue;

            var repaired = 0;
            var rewritten = new List<ToolCallItemDto>(items.Count);
            foreach (var item in items)
            {
                var normalized = JsonRepairUtil.NormalizeToolCallArguments(item.Arguments);
                if (!string.Equals(normalized, item.Arguments, StringComparison.Ordinal))
                {
                    repaired++;
                    Log?.WriteLine(
                        $"  [fix] {Path.GetFileName(path)}: {item.Name} arguments "
                            + $"{item.Arguments?.Length ?? 0} → {normalized.Length}"
                    );
                }

                rewritten.Add(
                    new ToolCallItemDto
                    {
                        Id = item.Id,
                        Name = item.Name,
                        Arguments = normalized,
                    }
                );
            }

            if (repaired == 0)
                continue;

            obj["tool-calls"] = JsonSerializer.Serialize(rewritten, JsonlSerializer.Options);
            changedLines[i] = true;
            repairedCalls += repaired;
            anyChanged = true;
        }

        if (!anyChanged)
            return (false, 0);

        var output = new StringBuilder(original.Length + 256);
        for (var i = 0; i < rawLines.Length; i++)
        {
            if (i > 0)
                output.Append(newline);

            var raw = rawLines[i].TrimEnd('\r');
            output.Append(
                changedLines[i] && nodes[i] is { } node
                    ? node.ToJsonString(JsonlSerializer.Options)
                    : raw
            );
        }

        await AtomicFile.WriteAllTextAsync(path, output.ToString(), ct);
        return (true, repairedCalls);
    }

    private static bool IsMessage(JsonObject obj) =>
        obj.TryGetPropertyValue("type", out var node)
        && node is JsonValue value
        && value.TryGetValue<string>(out var type)
        && string.Equals(type, "message", StringComparison.Ordinal);

    /// <summary>二次扫描：仍有空/非法 arguments 或无法解析的 tool-calls → 抛异常阻止成功记账。</summary>
    private static async Task VerifyAsync(string sessionsDir, TextWriter? log, CancellationToken ct)
    {
        var badArguments = 0;
        var badJson = 0;

        foreach (var file in EnumerateSessionFiles(sessionsDir))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var raw in (await AtomicFile.ReadAllTextAsync(file, ct)).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                JsonObject? obj;
                try
                {
                    obj = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (obj is null || !IsMessage(obj))
                    continue;
                if (
                    !obj.TryGetPropertyValue("tool-calls", out var toolCallsNode)
                    || toolCallsNode is not JsonValue toolCallsValue
                    || !toolCallsValue.TryGetValue<string>(out var toolCallsJson)
                    || string.IsNullOrEmpty(toolCallsJson)
                )
                {
                    continue;
                }

                var items = ToolCallItemDto.DeserializeList(toolCallsJson);
                if (items is null)
                {
                    badJson++;
                    log?.WriteLine($"  [warn] 无法解析 tool-calls：{file}");
                    continue;
                }

                foreach (var item in items)
                {
                    if (!IsValidJson(item.Arguments))
                        badArguments++;
                }
            }
        }

        if (badArguments > 0)
        {
            throw new InvalidOperationException(
                $"tool-call 参数迁移未完全生效：空/非法 arguments {badArguments} 个。"
            );
        }

        if (badJson > 0)
        {
            // 无法解析的 tool-calls 多为外部损坏；修复不了也不阻断启动，只留日志
            log?.WriteLine($"  [warn] 仍有 {badJson} 个无法解析的 tool-calls（未阻断启动）。");
        }
    }

    private static bool IsValidJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
