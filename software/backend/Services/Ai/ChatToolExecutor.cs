using System.Text.Json;
using backend.Controllers;
using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services.Ai;

// Runs the chat assistant's tools (spec §6). Every tool is read-only, scoped to
// the userId passed in by the controller from the JWT — the model only supplies
// a tool name and arguments, never whose data to read (D-5) — and returns
// aggregates only (D-4). Failures come back as {"error": ...} tool results
// instead of exceptions, so the model can recover or explain.
public class ChatToolExecutor
{
    public const string WorkoutSummary = "get_workout_summary";
    public const string CheckInStatus = "get_checkin_status";
    public const string WeeklyGoalProgress = "get_weekly_goal_progress";

    private static readonly string[] Ranges = ["this_week", "last_week", "this_month", "last_month"];

    public static readonly IReadOnlyList<ChatToolDefinition> Definitions =
    [
        new(WorkoutSummary,
            "Get the user's workout totals (count, active days, calories, breakdown by type) for a date range. Weeks start on Monday.",
            Schema(new
            {
                type = "object",
                properties = new { range = new { type = "string", @enum = Ranges } },
                required = new[] { "range" },
                additionalProperties = false,
            })),
        new(CheckInStatus,
            $"Get the user's current daily check-in streak, whether they've checked in today, and how many more consecutive check-ins unlock the reward skin ({CheckInController.RewardSkinStreak}-day streak).",
            Schema(new { type = "object", properties = new { }, additionalProperties = false })),
        new(WeeklyGoalProgress,
            "Get the user's progress toward their weekly calorie goal for the current Monday–Sunday week.",
            Schema(new { type = "object", properties = new { }, additionalProperties = false })),
    ];

    private readonly AppDbContext _db;
    private readonly AiUsageTracker _tracker;

    public ChatToolExecutor(AppDbContext db, AiUsageTracker tracker)
    {
        _db = db;
        _tracker = tracker;
    }

    // `today` is the user's local date, already clamped by the controller's
    // ResolveToday, so tools agree with what the rest of the app shows.
    public async Task<string> ExecuteAsync(int userId, DateOnly today, ChatToolCall call, CancellationToken ct = default)
    {
        _tracker.AddToolCall(call.Name);

        JsonElement args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson).RootElement;
        }
        catch (JsonException)
        {
            return Error($"Arguments for {call.Name} were not valid JSON.");
        }

        object result = call.Name switch
        {
            WorkoutSummary => await GetWorkoutSummary(userId, today, args, ct),
            CheckInStatus => await GetCheckInStatus(userId, today, ct),
            WeeklyGoalProgress => await GetWeeklyGoalProgress(userId, today, ct),
            _ => new { error = $"Unknown tool: {call.Name}" },
        };
        return JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
    }

    // T-1
    private async Task<object> GetWorkoutSummary(int userId, DateOnly today, JsonElement args, CancellationToken ct)
    {
        var range = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;
        if (range is null || !Ranges.Contains(range))
            return new { error = $"range must be one of: {string.Join(", ", Ranges)}" };

        var (from, to) = ResolveRange(range, today);
        var records = await _db.WorkoutRecords
            .Where(w => w.UserId == userId && w.Date >= from && w.Date <= to)
            .Select(w => new { w.WorkoutType, w.Calories, w.Date })
            .ToListAsync(ct);

        return new
        {
            range,
            from,
            to,
            workoutCount = records.Count,
            activeDays = records.Select(w => w.Date).Distinct().Count(),
            totalCalories = records.Sum(w => w.Calories),
            byType = records
                .GroupBy(w => w.WorkoutType)
                .OrderByDescending(g => g.Sum(w => w.Calories))
                .ToDictionary(g => g.Key, g => new { count = g.Count(), calories = g.Sum(w => w.Calories) }),
        };
    }

    // T-2
    private async Task<object> GetCheckInStatus(int userId, DateOnly today, CancellationToken ct)
    {
        var user = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.Streak, u.LastCheckIn })
            .FirstOrDefaultAsync(ct);
        if (user is null) return new { error = "User not found." };

        var checkedInToday = await _db.CheckIns.AnyAsync(c => c.UserId == userId && c.Date == today, ct);
        var ownsRewardSkin = await _db.UserSkins.AnyAsync(us => us.UserId == userId && us.Skin.IsReward, ct);

        // User.Streak is only recalculated on the next check-in, so after missed
        // days it still holds the old run. Treat it as broken unless the last
        // check-in was today or yesterday (same date comparison as CheckInController).
        var lastDate = user.LastCheckIn.HasValue ? DateOnly.FromDateTime(user.LastCheckIn.Value) : (DateOnly?)null;
        var streakAlive = lastDate.HasValue && lastDate.Value >= today.AddDays(-1);
        var streak = streakAlive ? user.Streak : 0;

        return new
        {
            streak,
            checkedInToday,
            rewardSkinStreak = CheckInController.RewardSkinStreak,
            ownsRewardSkin,
            checkInsUntilRewardSkin = ownsRewardSkin ? 0 : Math.Max(0, CheckInController.RewardSkinStreak - streak),
        };
    }

    // T-3
    private async Task<object> GetWeeklyGoalProgress(int userId, DateOnly today, CancellationToken ct)
    {
        var goal = await _db.Users.Where(u => u.Id == userId).Select(u => (int?)u.WeeklyCalorieGoal).FirstOrDefaultAsync(ct);
        if (goal is null) return new { error = "User not found." };

        var weekStart = StartOfWeek(today);
        var caloriesSoFar = await _db.WorkoutRecords
            .Where(w => w.UserId == userId && w.Date >= weekStart && w.Date <= today)
            .SumAsync(w => w.Calories, ct);

        return new
        {
            weekStart,
            goal = goal.Value,
            caloriesSoFar,
            remainingCalories = Math.Max(0, goal.Value - caloriesSoFar),
            percent = goal.Value > 0 ? (int)Math.Round(caloriesSoFar * 100.0 / goal.Value) : 0,
            daysLeftInWeek = 6 - DaysSinceMonday(today),
        };
    }

    internal static (DateOnly from, DateOnly to) ResolveRange(string range, DateOnly today)
    {
        var weekStart = StartOfWeek(today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return range switch
        {
            "this_week" => (weekStart, today),
            "last_week" => (weekStart.AddDays(-7), weekStart.AddDays(-1)),
            "this_month" => (monthStart, today),
            "last_month" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            _ => throw new ArgumentOutOfRangeException(nameof(range)),
        };
    }

    // Monday-start weeks, matching the frontend's WeeklyGoalDonut
    internal static DateOnly StartOfWeek(DateOnly d) => d.AddDays(-DaysSinceMonday(d));

    private static int DaysSinceMonday(DateOnly d) => ((int)d.DayOfWeek + 6) % 7;

    private static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}
