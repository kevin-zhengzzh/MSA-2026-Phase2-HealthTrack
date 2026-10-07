namespace backend.Models;

// Cached AI weekly summary (spec §5.2, §8): generated at most once per user
// and Monday-start week, so reopening the Dashboard is free.
public class WeeklySummary
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateOnly WeekStart { get; set; }

    // Always "en" for now; kept in the unique key so more languages can be
    // added later without a migration
    public string Language { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
