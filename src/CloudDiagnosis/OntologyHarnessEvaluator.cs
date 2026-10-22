using Core.Data;
using Core.Evolution;

namespace CloudDiagnosis;

/// <summary>本体 harness 的存储加载入口：把小说状态装成策略输入。</summary>
public static class OntologyHarnessEvaluator
{
    public static async Task<OntologyHarnessResult> EvaluateAsync(
        INovelStore store,
        Novel novel,
        Chapter chapter,
        CloudDiagnosisOptions options,
        CancellationToken ct = default
    )
    {
        var scenes = await store.GetScenesByChapter(chapter.Id, ct);
        var rules = (await store.GetWorldRules(novel.Id, ct))
            .Where(r => r.EntryKind == WorldRuleEntryKind.Rule)
            .ToList();
        var view = await EntityTimelineView.BuildAsync(store, novel.Id, ct);
        var actions = await store.GetOntologyActions(novel.Id, ct);
        return OntologyHarnessPolicy.Evaluate(
            novel,
            chapter,
            scenes,
            rules,
            view,
            actions,
            new OntologyHarnessRules(
                options.OntologyHarnessMaxSignals,
                options.OntologyHarnessStallWindow,
                options.OntologyHarnessGateCloud
            )
        );
    }
}
