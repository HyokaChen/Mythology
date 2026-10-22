// 功能：微信 ↔ Agent 桥接 —— 入站长轮询驱动 Agent，Agent 事件聚合后回微信
//
// 为什么需要"turn 门闩"：宿主把 Agent 的**全局**事件流（agent.OnEvent）转到这里，
// 而同一个 Agent 也在服务终端里的对话。不加门闩，微信会把用户自己在终端打的那轮
// 内容也发到手机上。门闩 = 只有"我们这一轮"的事件才进缓冲。
//
// settle 窗：Agent 的流式事件非常碎（每个 token 一个 StreamChunk），逐条发会刷屏并撞
// 微信风控。攒到停顿 SettleMs 再发一条；消息结束/回合结束立即冲刷，不等窗口。

using System.Text;
using Core.Agent;
using Core.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Channels.WeChat;

/// <summary>微信 ↔ Agent 桥接。宿主负责把 Agent 事件转进来（见 <see cref="OnAgentEvent"/>）。</summary>
public sealed class WeChatAgentChannel : IAsyncDisposable
{
    /// <summary>两轮长轮询之间的最小间隔：防服务端不再挂起时忙转（见 RunAsync 尾部注释）。</summary>
    private const int MinimumPollIntervalMs = 500;

    private readonly WeChatChannelService _channel;
    private readonly Func<string, CancellationToken, Task> _runTurn;
    private readonly Func<bool>? _isAgentBusy;
    private readonly UserQuestionStore? _questionStore;
    private readonly ILogger<WeChatAgentChannel> _logger;
    private readonly object _gate = new();
    private readonly System.Text.StringBuilder _buffer = new();

    /// <summary>出站串行队列：并发直接发会乱序（Agent 事件来自后台线程）。</summary>
    private readonly System.Threading.Channels.Channel<string> _outbox =
        System.Threading.Channels.Channel.CreateUnbounded<string>();

    private readonly Timer _settle;
    private readonly CancellationTokenSource _lifetime = new();

    private Task? _writer;
    private Task? _typing;

    /// <summary>当前是否有微信发起的回合在跑（门闩）。</summary>
    private bool _turnActive;

    /// <summary>当前回合任务（独立于长轮询，见 HandleInboundAsync 的说明）。</summary>
    private Task? _turnTask;

    private readonly List<string> _renderedQuestionIds = [];

    /// <summary>被 -14 拒掉的待补发文本（用户下次发消息时补发）。只留最后一条，避免堆积。</summary>
    private string? _pendingText;

    private bool _disposed;

    public WeChatAgentChannel(
        WeChatChannelService channel,
        Func<string, CancellationToken, Task> runTurn,
        Func<bool>? isAgentBusy = null,
        UserQuestionStore? questionStore = null,
        ILogger<WeChatAgentChannel>? logger = null
    )
    {
        _channel = channel;
        _runTurn = runTurn;
        _isAgentBusy = isAgentBusy;
        _questionStore = questionStore;
        _logger = logger ?? NullLogger<WeChatAgentChannel>.Instance;
        _settle = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>是否有微信发起的回合在跑（宿主据此抑制自己的裁决弹窗，避免两边抢答）。</summary>
    public bool IsTurnActive
    {
        get
        {
            lock (_gate)
            {
                return _turnActive;
            }
        }
    }

    /// <summary>
    /// 宿主把 <c>agent.OnEvent</c> 转到这里。非本轮的 chunk 会被门闩丢掉。
    /// </summary>
    public void OnAgentEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case StreamChunk { ContentType: StreamContentType.Text } chunk:
                // 思考内容（ContentType.Thinking）不上屏：微信里发推理过程只会淹掉正文
                lock (_gate)
                {
                    if (!_turnActive)
                        return;
                    _buffer.Append(chunk.Delta);
                }
                _settle.Change(WeChatConstants.SettleMs, Timeout.Infinite);
                break;

            case MessageEnd:
                // 一条 assistant 消息结束 = 一个完整气泡，立即冲刷
                Flush();
                break;

            case AgentError error:
                AppendToBuffer($"\n⚠️ {error.Message}");
                Flush();
                break;

            case AgentEnd:
                Flush();
                break;
        }
    }

    /// <summary>长轮询主循环。取消时干净退出（这是循环终止信号，不是异常路径）。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _writer = WriteLoopAsync(_lifetime.Token);
        _typing = TypingLoopAsync(_lifetime.Token);

        if (_questionStore is not null)
            _questionStore.PendingQuestionsCreated += OnQuestionsCreated;

        var longPollMs = WeChatConstants.DefaultLongPollTimeoutMs;
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = Environment.TickCount64;
            WeChatPollResult poll;
            try
            {
                poll = await _channel.PollAsync(longPollMs, cancellationToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var delay =
                    consecutiveFailures >= WeChatConstants.MaxConsecutiveFailures
                        ? WeChatConstants.BackoffDelayMs
                        : WeChatConstants.RetryDelayMs;
                _logger.LogWarning(
                    ex,
                    "微信长轮询失败（连续 {Count} 次），{Delay}ms 后重试",
                    consecutiveFailures,
                    delay
                );
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            if (poll.BindingExpired)
            {
                _channel.ReportBindingExpired();
                return;
            }

            if (poll.SuggestedLongPollMs > 0)
                longPollMs = poll.SuggestedLongPollMs;

            foreach (var message in poll.Messages)
            {
                try
                {
                    await HandleInboundAsync(message, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }

            // 服务端本该挂起 ~30s；若它立刻返回（自身异常 / 中间代理吞掉长轮询），
            // 这个循环会变成 100% CPU 的忙转——await 同步完成时不会自然让出线程。
            // 所以给每轮设一个最小间隔。
            var elapsed = Environment.TickCount64 - startedAt;
            if (elapsed < MinimumPollIntervalMs)
            {
                try
                {
                    await Task.Delay((int)(MinimumPollIntervalMs - elapsed), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>处理一条入站消息：忙守卫 → 补发 → 驱动 Agent → 冲刷。</summary>
    private async Task HandleInboundAsync(
        WeChatInboundMessage message,
        CancellationToken cancellationToken
    )
    {
        if (!message.HasText)
        {
            if (message.HasUnsupportedMedia)
                await SafeSend("（暂不支持图片/文件/语音以外的内容，请发文字）", cancellationToken);
            return;
        }

        // 裁决回路优先于忙守卫：Agent 正阻塞在 WaitForAnswerAsync 里等这个答案，
        // 此时 _turnActive 仍为 true，放到后面会被"正忙"挡掉，永远答不了。
        if (TryAnswerQuestion(message.Text))
            return;

        bool busy;
        string? pending;
        lock (_gate)
        {
            // 真正会"忙"的场景有两个：终端里正在跑，或上一个微信回合还没结束。
            // （长轮询本身串行，但回合已是独立任务，所以这个分支真会触发。）
            busy = _turnActive || (_isAgentBusy?.Invoke() ?? false);
            if (!busy)
            {
                _turnActive = true;
                _buffer.Clear();
            }

            // 忙的时候不消费待补发文本：留给下一个真正能跑的回合
            pending = busy ? null : _pendingText;
            if (!busy)
                _pendingText = null;
        }

        if (busy)
        {
            // 不排队：排队要处理崩溃丢弃、超时、重复投递，而用户就在手机上，再发一次最省事
            _logger.LogInformation("微信入站消息被拒：Agent 正忙");
            await SafeSend("桌面正忙，请稍后再发。", cancellationToken);
            return;
        }

        try
        {
            if (pending is not null)
                await SafeSend(pending, cancellationToken);

            await _channel.SendTypingAsync(true, cancellationToken);

            // ★ 回合必须是独立任务，不能在这里 await。
            // Agent 的 ask_user 会阻塞在回合内部等用户拍板，而答案只能从长轮询来——
            // 若在此处 await，长轮询被回合堵住，答案永远进不来，自己把自己锁死。
            _turnTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await _runTurn(message.Text, cancellationToken);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        // 取消：不当作失败上报
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "微信回合执行失败");
                        AppendToBuffer($"⚠️ 处理失败：{ex.Message}");
                    }
                    finally
                    {
                        Flush();
                        lock (_gate)
                        {
                            _turnActive = false;
                            // 回合结束 = 期间创建的问题都已处置；残留 id 会让下一条消息被误当答案
                            _renderedQuestionIds.Clear();
                        }
                        _settle.Change(Timeout.Infinite, Timeout.Infinite);
                        await _channel.SendTypingAsync(false, CancellationToken.None);
                    }
                },
                CancellationToken.None
            );
        }
        finally
        {
            // 收尾已移入回合任务的 finally：此处不阻塞长轮询
        }
    }

    /// <summary>
    /// 回合期间创建的问题（ask_user 工具 + 云端裁决 Hook 都走这里）转成手机消息。
    /// 非本回合创建的不接管：那是终端里的对话，宿主自己的弹窗会处理。
    /// </summary>
    private void OnQuestionsCreated(IReadOnlyList<PendingQuestion> questions)
    {
        if (questions.Count == 0)
            return;

        lock (_gate)
        {
            if (!_turnActive)
                return;
            foreach (var question in questions)
                if (question.Id is { Length: > 0 } id && !_renderedQuestionIds.Contains(id))
                    _renderedQuestionIds.Add(id);
        }

        foreach (var question in questions)
            _outbox.Writer.TryWrite(RenderQuestion(question));
    }

    /// <summary>把待答问题渲染成微信可读文本。</summary>
    private static string RenderQuestion(PendingQuestion question)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【需要你拍板】" + question.Header);
        if (question.Question is { Length: > 0 } text)
            sb.AppendLine(text);

        if (question.Options is { Length: > 0 })
        {
            for (var i = 0; i < question.Options.Length; i++)
                sb.AppendLine($"{i + 1}) {question.Options[i]}");

            sb.Append(question.MultiSelect ? "回复编号，多选用逗号分隔（如 1,3）" : "回复编号");
        }
        else
        {
            sb.Append("直接回复文字即可");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 尝试把一条入站文本当作某个待答问题的答案。
    /// 返回 true = 这条消息已被问题回路消费（无论解析成败，都不该再当作 prompt）。
    /// </summary>
    private bool TryAnswerQuestion(string reply)
    {
        if (_questionStore is null)
            return false;

        List<PendingQuestion> ours;
        lock (_gate)
        {
            ours = _renderedQuestionIds
                .Select(id => _questionStore.Get(id))
                .Where(q => q is { IsAnswered: false })
                .Select(q => q!)
                .ToList();
        }

        if (ours.Count == 0)
            return false;

        var question = ours[0];
        if (!TryParseAnswer(question, reply, out var selected))
        {
            // 解析失败：重发题目而不是静默丢弃（用户会以为消息丢了）
            _outbox.Writer.TryWrite(RenderQuestion(question));
            return true;
        }

        lock (_gate)
        {
            _renderedQuestionIds.Remove(question.Id);
        }

        _questionStore.Answer(question.Id, selected);

        // 一组问题有多条：答完一条接着发下一条
        if (ours.Count > 1)
            _outbox.Writer.TryWrite(RenderQuestion(ours[1]));

        return true;
    }

    /// <summary>解析编号回复。无选项的问题接受任意文字。</summary>
    private static bool TryParseAnswer(
        PendingQuestion question,
        string reply,
        out string[] selected
    )
    {
        selected = [];
        var trimmed = reply.Trim();

        if (question.Options is not { Length: > 0 })
        {
            if (trimmed.Length == 0)
                return false;
            selected = [trimmed];
            return true;
        }

        var tokens = trimmed.Split(
            [',', '，', '、', ' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (tokens.Length == 0)
            return false;

        var picked = new List<string>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!int.TryParse(token, out var index) || index < 1 || index > question.Options.Length)
                return false;

            var option = question.Options[index - 1];
            if (!picked.Contains(option, StringComparer.Ordinal))
                picked.Add(option);

            // 单选却给了多个编号：拒绝，让用户重选（静默取第一个会猜错意图）
            if (!question.MultiSelect && picked.Count > 1)
                return false;
        }

        selected = [.. picked];
        return true;
    }

    /// <summary>把缓冲区当前内容交给出站队列。</summary>
    private void Flush()
    {
        _settle.Change(Timeout.Infinite, Timeout.Infinite);

        string? text;
        lock (_gate)
        {
            if (_buffer.Length == 0)
                return;
            text = _buffer.ToString();
            _buffer.Clear();
        }

        if (!_outbox.Writer.TryWrite(text))
            _logger.LogDebug("微信出站队列已关闭，丢弃一条文本");
    }

    private void AppendToBuffer(string text)
    {
        lock (_gate)
        {
            _buffer.Append(text);
        }
    }

    /// <summary>串行出站：一条发完再发下一条，保证手机上的顺序与生成顺序一致。</summary>
    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (
                var text in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)
            )
            {
                try
                {
                    var outcome = await _channel.SendTextAsync(text, cancellationToken);
                    if (outcome == WeChatSendOutcome.WindowClosed)
                    {
                        // 不静默丢弃：等用户下次发消息时补发（否则用户永远看不到这条回复）
                        lock (_gate)
                        {
                            _pendingText = text;
                        }
                        _logger.LogInformation("微信回复窗口已关闭，暂存待补发");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "微信出站发送失败");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    /// <summary>回合进行中每 5s 重发一次 typing（微信的指示器几秒后会自动消失）。</summary>
    private async Task TypingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(WeChatConstants.TypingRefreshMs)
        );
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                bool active;
                lock (_gate)
                {
                    active = _turnActive;
                }
                if (active)
                    await _channel.SendTypingAsync(true, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    /// <summary>发送但不上抛：桥接层的提示文案失败不该打断回合。</summary>
    private async Task SafeSend(string text, CancellationToken cancellationToken)
    {
        try
        {
            await _channel.SendTextAsync(text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "微信提示文案发送失败");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _settle.Change(Timeout.Infinite, Timeout.Infinite);
        _settle.Dispose();
        await _lifetime.CancelAsync();
        _outbox.Writer.TryComplete();

        if (_questionStore is not null)
            _questionStore.PendingQuestionsCreated -= OnQuestionsCreated;

        // 回合任务是独立任务：取消由宿主的 ct 负责，这里最多等 2 秒，不等它跑完
        if (_turnTask is { IsCompleted: false })
        {
            try
            {
                await _turnTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // 仍在跑：进程即将退出，Agent.Abort 会走宿主的取消路径
            }
        }

        foreach (var task in new[] { _writer, _typing })
        {
            if (task is null)
                continue;
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }

        _lifetime.Dispose();
    }
}
