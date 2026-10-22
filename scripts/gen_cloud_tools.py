#!/usr/bin/env python3
import re, os

BASE = "/Users/krp/Documents/GithubProjects/Mythology/src/Plugins/NovelTools/Tools"
CAPMAP = {
    'EvaluateSatisfactionTool.cs':('fiction.context.retrieve','爽感'),
    'TrackCharacterArcTool.cs':('fiction.character.consistency','角色弧'),
    'EvaluateConflictTool.cs':('fiction.context.retrieve','冲突'),
    'EvaluateOpeningTool.cs':('fiction.context.retrieve','开篇'),
    'AnalyzePacingTool.cs':('fiction.context.retrieve','节奏'),
    'DetectDescriptionQualityTool.cs':('fiction.context.retrieve','描写'),
    'AnalyzeSupportingCharacterTool.cs':('fiction.character.consistency','配角'),
    'DetectAvoidableMistakesTool.cs':('fiction.context.retrieve','常识错误'),
    'CheckNamingQualityTool.cs':('fiction.context.retrieve','命名'),
    'EvaluatePowerSystemTool.cs':('fiction.plotline.check','力量体系'),
    'AnalyzeDialogueQualityTool.cs':('fiction.context.retrieve','对话'),
    'DetectContentPaddingTool.cs':('fiction.context.retrieve','注水'),
    'SuggestForeshadowingTool.cs':('fiction.foreshadowing.check','伏笔'),
    'EvaluatePlotStructureTool.cs':('fiction.plotline.check','结构'),
    'CheckCharacterCompletenessTool.cs':('fiction.character.consistency','角色完整性'),
    'EvaluateExcitementCurveTool.cs':('fiction.context.retrieve','高潮'),
    'EvaluateReaderEngagementTool.cs':('fiction.context.retrieve','读者参与'),
    'AnalyzeGeniusCharacterTool.cs':('fiction.character.consistency','天才角色'),
    'DetectSelfIndulgenceTool.cs':('fiction.context.retrieve','自我感动'),
    'DetectPlotHolesTool.cs':('fiction.plotline.check','情节漏洞'),
    'AnalyzeStyleTool.cs':('fiction.context.retrieve','风格'),
}

TEMPLATE = '''using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Mythology.ClientPlatform;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// {label} — {desc}
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class {cls}(INovelStore store, IFictionHarnessClient? cloudHarness = null)
    : CloudAnalysisTool<{ptype}, {dtype}>(store, cloudHarness)
{{
    public override string Name => "{name}";
    public override string Label => "{label}";
    public override string Description => "{desc}";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => "{cap}";
    protected override string Dimension => "{dim}";

    public override async Task<AgentToolResult<{dtype}>> ExecuteAsync(
        string toolCallId, {ptype} args, CancellationToken ct,
        IProgress<AgentToolResult<{dtype}>>? onUpdate = null)
    {{
        ThrowIfCancelled(ct);
        var (chapters, mergedText, novel) = await LoadChaptersAsync(args.NovelId, {chaparg}, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<{dtype}>{{ Content = [new TextContent("暂无章节内容可分析")], IsError = true }};
        var cloudResult = await ExecuteCloudAnalysisAsync(mergedText, chapters[0], novel!, ct);
        if (cloudResult is null) return CloudUnavailableResult();
        var overallScore = ParseOverallScore(cloudResult.Value);
        var dimScores = ParseDimensionScores(cloudResult.Value);
        var cloudIssues = ParseCloudIssues(cloudResult.Value);
        var details = MapCloudResult(cloudResult.Value, overallScore, dimScores, cloudIssues);
        var report = BuildReportText(details);
        return new AgentToolResult<{dtype}>{{ Content = [new TextContent(report)], Details = details }};
    }}

    private static {dtype} MapCloudResult(JsonElement element, double overallScore, Dictionary<string, double> dimScores, List<CloudIssue> cloudIssues)
    {{
        return new {dtype}{{ OverallScore = overallScore }};
    }}

    private static string BuildReportText({dtype} d)
    {{
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 {label}报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{{d.OverallScore:F1}}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }}
}}

{records}'''

for fn, (cap, dim) in CAPMAP.items():
    fp = os.path.join(BASE, fn)
    if not os.path.exists(fp):
        continue
    with open(fp, 'r') as f:
        c = f.read()
    ls = c.split('\n')
    cn = tn = lb = desc = pt = dt = None
    for l in ls:
        m = re.search(r'public sealed class (\w+)', l)
        if m and not cn: cn = m.group(1)
        m = re.search(r'public override string Name => "([^"]+)"', l)
        if m and not tn: tn = m.group(1)
        m = re.search(r'public override string Label => "([^"]+)"', l)
        if m and not lb: lb = m.group(1)
        m = re.search(r'public override string Description\s*=>\s*"([^"]+)"', l)
        if m and not desc: desc = m.group(1)
        m = re.search(r': AgentTool<(\w+),\s*(\w+)>', l)
        if m and not pt: pt = m.group(1); dt = m.group(2)
    rs = None
    bc = 0
    cs = False
    for i, l in enumerate(ls):
        if f'class {cn}' in l:
            cs = True
            bc = 0
        if cs:
            bc += l.count('{') - l.count('}')
            if bc <= 0 and i > 5:
                rs = i + 1
                break
    if rs is None:
        for i, l in enumerate(ls):
            if i > 10 and 'public sealed record ' in l:
                rs = i
                break
    rt = '\n'.join(ls[rs:]) if rs else ''
    hci = 'ChapterIds' in rt
    ci = 'args.ChapterIds' if hci else 'null'
    nc = TEMPLATE.format(cls=cn, name=tn, label=lb, desc=desc, cap=cap, dim=dim,
                         ptype=pt, dtype=dt, chaparg=ci, records=rt)
    with open(fp, 'w') as f:
        f.write(nc)
    print(f'OK: {fn}')
print('Done!')
