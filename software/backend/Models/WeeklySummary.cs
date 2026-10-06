namespace backend.Models;

// Cached AI weekly summary (spec §5.2, §8): generated at most once per user,
// per Monday-start week, per language, so reopening the Dashboard is free.
public class WeeklySummary
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateOnly WeekStart { get; set; }

    // Normalized primary language tag: "en" or "zh"
    public string Language { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
