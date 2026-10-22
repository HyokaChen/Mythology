using Core.Data;
using Core.Novels;
using Core.Vector;
using LlmTornado.Code;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Notes;

/// <summary>
/// 会话随手记捕获器：从用户消息里识别「记一下 / 备忘 / 我想到 …」这类句子，落成
/// <see cref="NoteStatus.Inbox"/> 候选笔记（**不参与远期推演**，需 UI 或 manage_note confirm）。
///
/// 只做确定性规则（触发词 + 分句 + 长度 + Jaccard 去重），不调模型：
/// 候选噪一点没关系（人一审就过），但静默改作者设定不行。
/// </summary>
public sealed class NoteCaptureScanner(INovelStore store, RetrievalKernel? kernel = null)
{
    /// <summary>触发词（命中即候选）。</summary>
    private static readonly string[] Triggers =
    [
        "记一下",
        "记一笔",
        "记下来",
        "记下",
        "备注",
        "备忘",
        "突然想到",
        "我想到",
        "有个想法",
        "有个点子",
        "以后可以写",
        "可以写",
        "留个伏笔",
        "别忘了",
        "灵感",
    ];

    /// <summary>单次扫描最多新增条数。</summary>
    private const int MaxPerScan = 3;

    /// <summary>单条消息最多取句数。</summary>
    private const int MaxSentencesPerMessage = 2;

    /// <summary>句子有效长度区间（太短是口头语，太长是正文粘贴）。</summary>
    private const int MinSentenceChars = 8;
    private const int MaxSentenceChars = 300;

    /// <summary>正文上限。</summary>
    private const int MaxBodyChars = 300;

    /// <summary>与已有笔记的 token Jaccard 阈值（≥ 视为重复）。</summary>
    private const double DuplicateJaccard = 0.6;

    /// <summary>当前写作进度：已有正文章节的最大序号（无章 → null）。</summary>
    public static int? MaxWrittenOrderOf(NovelStructure? structure)
    {
        var written = structure?.Chapters.Where(c => c.CharCount > 0).Select(c => (int?)c.Order);
        return written is null ? null : written.Max();
    }

    /// <summary>
    /// 扫描 <paramref name="messages"/> 从 <paramref name="startIndex"/> 起的新消息，落候选笔记。
    /// 返回新增条数；异常吞掉返回 0（捕获是旁路，绝不阻塞对话），取消上抛。
    /// </summary>
    public async Task<int> ScanAsync(
        string novelId,
        string sessionId,
        IReadOnlyList<LlmChatMessage> messages,
        int startIndex,
        int? capturedChapterOrder,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(novelId) || messages.Count == 0)
            return 0;

        try
        {
            var existing = await store.GetNotes(novelId, ct);
            var existingTokenSets = existing
                .Select(n => Tokenize($"{n.Title} {n.Body}"))
                .Where(set => set.Count > 0)
                .ToList();

            var added = 0;
            for (var i = Math.Max(0, startIndex); i < messages.Count && added < MaxPerScan; i++)
            {
                ct.ThrowIfCancellationRequested();
                var message = messages[i];
                if (message.Role != ChatMessageRoles.User)
                    continue;
                if (string.IsNullOrWhiteSpace(message.Content))
                    continue;
                // 系统注入块（[远期推演] / [联想记忆] / [云端诊断] …）不是作者原话
                if (message.Content.TrimStart().StartsWith('['))
                    continue;

                var body = ExtractBody(message.Content);
                if (body is null)
                    continue;
                if (IsDuplicate(body, existingTokenSets))
                    continue;

                var note = new AuthorNote
                {
                    NovelId = novelId,
                    Title = body[..Math.Min(16, body.Length)],
                    Body = body,
                    Status = NoteStatus.Inbox,
                    Source = AuditSource.Ai,
                    SessionId = sessionId,
                    CapturedChapterOrder = capturedChapterOrder,
                };
                await store.SaveNote(note, ct);
                existingTokenSets.Add(Tokenize($"{note.Title} {note.Body}"));

                if (kernel is not null)
                {
                    var (doc, chunks) = kernel.Builder.BuildAuthorNote(note);
                    await kernel.IndexDocumentAsync(doc, chunks, ct);
                }
                added++;
            }
            return added;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>取触发词命中、长度合规的前 N 句拼成正文；没有命中返回 null。</summary>
    private static string? ExtractBody(string content)
    {
        var sentences = content
            .Split(['。', '！', '？', '；', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length is >= MinSentenceChars and <= MaxSentenceChars)
            .Where(s => Triggers.Any(s.Contains))
            .Take(MaxSentencesPerMessage)
            .ToList();

        if (sentences.Count == 0)
            return null;

        var body = string.Join('。', sentences);
        return body.Length <= MaxBodyChars ? body : body[..MaxBodyChars];
    }

    /// <summary>
    /// 分词：优先 jieba（与记忆检索同口径）；jieba 词典资源缺失时（打包/测试输出没有
    /// Resources/ 的宿主）退回字符二元组——去重只需稳定分词，不能因为分词初始化失败就整轮放弃捕获。
    /// 同步去重只用它算 Jaccard，不参与索引。
    /// </summary>
    private static HashSet<string> Tokenize(string text)
    {
        try
        {
            return AgentMemoryBm25.Tokenize(text).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return Bigrams(text);
        }
    }

    private static HashSet<string> Bigrams(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var flat = string.Concat(
            (text ?? "").Where(ch => !char.IsPunctuation(ch) && !char.IsWhiteSpace(ch))
        );
        if (flat.Length == 0)
            return tokens;
        if (flat.Length == 1)
        {
            tokens.Add(flat);
            return tokens;
        }
        for (var i = 0; i + 1 < flat.Length; i++)
            tokens.Add(flat.Substring(i, 2));
        return tokens;
    }

    private static bool IsDuplicate(string body, List<HashSet<string>> existingTokenSets)
    {
        var tokens = Tokenize(body);
        if (tokens.Count == 0)
            return false;

        foreach (var other in existingTokenSets)
        {
            var union = new HashSet<string>(tokens, StringComparer.Ordinal);
            union.UnionWith(other);
            var intersection = tokens.Count(other.Contains);
            if (union.Count > 0 && (double)intersection / union.Count >= DuplicateJaccard)
                return true;
        }
        return false;
    }
}
