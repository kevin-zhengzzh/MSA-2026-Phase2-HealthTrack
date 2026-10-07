using System.Text.Json;
using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services.Ai;

public record WeeklySummaryResult(DateOnly WeekStart, DateOnly WeekEnd, string Summary, string Source);

// AI weekly summary (spec §5.2). Summarizes the last complete Monday–Sunday
// week: every number is computed here (D-6) and the model only writes prose.
// Results are cached per user and week (WS-4); a week with no workouts gets a
// fixed message without calling the model at all (WS-5). Always English, to
// match the rest of the UI (D-10).
public class WeeklySummaryService
{
    public const string SourceAi = "ai";
    public const string SourceCache = "cache";
    public const string SourceFallback = "fallback";

    // The table keeps a Language column (part of the unique key) so other
    // languages can be added later without a migration; today it's always this.
    private const string Language = "en";

    private const string FallbackMessage =
        "No workouts were logged last week — a fresh week is the perfect time to start. Even one short session counts toward your goal!";

    // §9 — kept in sync with the spec
    private const string SystemPrompt =
        "You are an encouraging fitness coach. Write a 3–4 sentence summary of the user's previous week (the " +
        "Monday–Sunday range in the JSON stats provided). The user reads this during the following week, so " +
        "always call it \"last week\" (never \"this week\"). Use only the numbers given; never invent data. " +
        "End with one concrete, achievable suggestion for next week. Write in English. No medical advice. " +
        "Plain text only — no Markdown, headings or lists.";

    private const int MaxSummaryTokens = 512;

    private readonly AppDbContext _db;
    private readonly IChatModel _model;

    public WeeklySummaryService(AppDbContext db, IChatModel model)
    {
        _db = db;
        _model = model;
    }

    public async Task<WeeklySummaryResult> GetAsync(int userId, DateOnly today, CancellationToken ct = default)
    {
        var weekStart = ChatToolExecutor.StartOfWeek(today).AddDays(-7);
        var weekEnd = weekStart.AddDays(6);

        var cached = await FindCached(userId, weekStart, ct);
        if (cached is not null) return new(weekStart, weekEnd, cached, SourceCache);

        var stats = await BuildStats(userId, weekStart, weekEnd, ct);
        if (stats is null) return new(weekStart, weekEnd, FallbackMessage, SourceFallback);

        var response = await _model.CompleteAsync(new ChatRequest(
            [
                ChatMessage.System(SystemPrompt),
                ChatMessage.User(JsonSerializer.Serialize(stats, JsonSerializerOptions.Web)),
            ],
            MaxTokens: MaxSummaryTokens), ct);

        var summary = response.Content?.Trim();
        if (string.IsNullOrEmpty(summary))
            throw new ChatModelException("The model returned an empty summary.");

        var entity = new WeeklySummary { UserId = userId, WeekStart = weekStart, Language = Language, Content = summary };
        _db.WeeklySummaries.Add(entity);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two requests for the same week raced and the other one saved first
            // (unique index). Drop ours and serve the stored copy.
            _db.Entry(entity).State = EntityState.Detached;
            var winner = await FindCached(userId, weekStart, ct);
            if (winner is null) throw;
            return new(weekStart, weekEnd, winner, SourceCache);
        }

        return new(weekStart, weekEnd, summary, SourceAi);
    }

    private Task<string?> FindCached(int userId, DateOnly weekStart, CancellationToken ct) =>
        _db.WeeklySummaries
            .Where(s => s.UserId == userId && s.WeekStart == weekStart && s.Language == Language)
            .Select(s => s.Content)
            .FirstOrDefaultAsync(ct);

    // Returns null when the week has no workouts. Aggregates only — no names,
    // emails or notes leave the system (D-4).
    private async Task<object?> BuildStats(int userId, DateOnly weekStart, DateOnly weekEnd, CancellationToken ct)
    {
        var previousWeekStart = weekStart.AddDays(-7);
        var records = await _db.WorkoutRecords
            .Where(w => w.UserId == userId && w.Date >= previousWeekStart && w.Date <= weekEnd)
            .Select(w => new { w.Date, w.WorkoutType, w.Calories })
            .ToListAsync(ct);

        var week = records.Where(w => w.Date >= weekStart).ToList();
        if (week.Count == 0) return null;

        var goal = await _db.Users.Where(u => u.Id == userId).Select(u => u.WeeklyCalorieGoal).FirstOrDefaultAsync(ct);
        var checkInDays = await _db.CheckIns.CountAsync(c => c.UserId == userId && c.Date >= weekStart && c.Date <= weekEnd, ct);
        var totalCalories = week.Sum(w => w.Calories);

        return new
        {
            weekStart,
            weekEnd,
            workoutCount = week.Count,
            activeDays = week.Select(w => w.Date).Distinct().Count(),
            totalCalories,
            weeklyCalorieGoal = goal,
            goalPercent = goal > 0 ? (int)Math.Round(totalCalories * 100.0 / goal) : 0,
            previousWeekCalories = records.Where(w => w.Date < weekStart).Sum(w => w.Calories),
            checkInDays,
            byType = week
                .GroupBy(w => w.WorkoutType)
                .OrderByDescending(g => g.Sum(w => w.Calories))
                .ToDictionary(g => g.Key, g => new { count = g.Count(), calories = g.Sum(w => w.Calories) }),
        };
    }
}
