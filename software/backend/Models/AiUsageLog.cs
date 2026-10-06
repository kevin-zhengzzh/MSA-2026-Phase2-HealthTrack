namespace backend.Models;

// One row per user-facing AI request (not per model call — an agent loop can
// call the model several times for one chat message). Holds metadata only;
// message content is never stored (spec §8, NF-2). Also the source for the
// daily chat quota.
public class AiUsageLog
{
    public int Id { get; set; }
    public int UserId { get; set; }

    // AiFeatures.Chat or AiFeatures.WeeklySummary
    public string Feature { get; set; } = string.Empty;

    // Summed across every model call made for this request
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }

    public int LatencyMs { get; set; }

    // Comma-separated tool names in call order, e.g. "get_workout_summary,get_checkin_status"
    public string? ToolsCalled { get; set; }

    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
