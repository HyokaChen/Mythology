using Core.Prose;

namespace NovelTools.Tests;

internal sealed class FakeProseHarness(Func<ProseHarnessRequest, ProseHarnessResult> review)
    : IProseHarness
{
    public int Calls { get; private set; }
    public ProseHarnessRequest? LastRequest { get; private set; }

    public Task<ProseHarnessResult> ReviewAsync(ProseHarnessRequest request, CancellationToken ct)
    {
        Calls++;
        LastRequest = request;
        return Task.FromResult(review(request));
    }

    public static FakeProseHarness Returning(string content)
    {
        return new FakeProseHarness(request =>
        {
            var summary = new ProseHarnessSummary(
                ProseHarnessValidator.CountSeparatorLines(request.Content),
                ProseHarnessValidator.CountSeparatorLines(content)
            );
            return new ProseHarnessResult(
                ProseHarnessStatus.Applied,
                content,
                summary,
                "test harness"
            );
        });
    }

    public static FakeProseHarness Unavailable() =>
        new(request => ProseHarnessResult.Unavailable(request.Content, "test unavailable"));

    public static FakeProseHarness Failed() =>
        new(request => ProseHarnessResult.Failed(request.Content, "test failed"));
}
