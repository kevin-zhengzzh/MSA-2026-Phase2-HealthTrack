using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace backend.Services.Ai;

// Decorator around the real IChatModel: feeds every call's token usage into
// the request's AiUsageTracker and logs per-call metrics, so neither the
// provider client nor the endpoints contain any tracking code.
public class UsageTrackingChatModel : IChatModel
{
    private readonly IChatModel _inner;
    private readonly AiUsageTracker _tracker;
    private readonly ILogger<UsageTrackingChatModel> _logger;

    public UsageTrackingChatModel(IChatModel inner, AiUsageTracker tracker, ILogger<UsageTrackingChatModel> logger)
    {
        _inner = inner;
        _tracker = tracker;
        _logger = logger;
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await _inner.CompleteAsync(request, ct);
            Record(response, stopwatch);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AI model call failed after {LatencyMs} ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var events = _inner.StreamAsync(request, ct).GetAsyncEnumerator(ct);

        while (true)
        {
            // C# forbids yield inside try/catch, so only the MoveNext call is
            // wrapped — that's where the inner stream's exceptions surface.
            ChatStreamEvent current;
            try
            {
                if (!await events.MoveNextAsync()) break;
                current = events.Current;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "AI model stream failed after {LatencyMs} ms", stopwatch.ElapsedMilliseconds);
                throw;
            }

            if (current is StreamCompleted completed) Record(completed.Response, stopwatch);
            yield return current;
        }
    }

    private void Record(ChatResponse response, Stopwatch stopwatch)
    {
        _tracker.AddUsage(response.Usage);
        _logger.LogInformation(
            "AI model call: {LatencyMs} ms, {InputTokens} in / {OutputTokens} out, finish={FinishReason}, toolCalls={ToolCallCount}",
            stopwatch.ElapsedMilliseconds, response.Usage.InputTokens, response.Usage.OutputTokens,
            response.FinishReason, response.ToolCalls.Count);
    }
}
