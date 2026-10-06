using System.Diagnostics;
using backend.Data;
using backend.Models;

namespace backend.Services.Ai;

public static class AiFeatures
{
    public const string Chat = "chat";
    public const string WeeklySummary = "weekly_summary";
}

// Scoped (one per HTTP request). UsageTrackingChatModel adds token usage after
// every model call and the tool executor adds tool names; the endpoint then
// calls SaveAsync once, producing a single AiUsageLog row for the request.
public class AiUsageTracker
{
    private const int MaxErrorLength = 500;

    private readonly AppDbContext _db;
    private readonly ILogger<AiUsageTracker> _logger;
    // Started when the scope first resolves the tracker, i.e. near the start
    // of the request — close enough for request-level latency.
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly List<string> _toolsCalled = [];

    public AiUsageTracker(AppDbContext db, ILogger<AiUsageTracker> logger)
    {
        _db = db;
        _logger = logger;
    }

    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int ModelCalls { get; private set; }
    public IReadOnlyList<string> ToolsCalled => _toolsCalled;

    public void AddUsage(ChatUsage usage)
    {
        InputTokens += usage.InputTokens;
        OutputTokens += usage.OutputTokens;
        ModelCalls++;
    }

    public void AddToolCall(string toolName) => _toolsCalled.Add(toolName);

    // Never throws: a failed usage write must not turn a successful AI answer
    // into an error for the user. Deliberately ignores the request's
    // cancellation token so a client disconnecting mid-stream is still logged.
    public async Task SaveAsync(int userId, string feature, bool success, string? error = null)
    {
        _db.AiUsageLogs.Add(new AiUsageLog
        {
            UserId = userId,
            Feature = feature,
            InputTokens = InputTokens,
            OutputTokens = OutputTokens,
            LatencyMs = (int)_stopwatch.ElapsedMilliseconds,
            ToolsCalled = _toolsCalled.Count > 0 ? string.Join(",", _toolsCalled) : null,
            Success = success,
            Error = error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error,
        });

        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write AiUsageLog for user {UserId} ({Feature})", userId, feature);
        }
    }
}
