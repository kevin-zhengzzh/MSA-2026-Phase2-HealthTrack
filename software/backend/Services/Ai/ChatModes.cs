namespace backend.Services.Ai;

// A topic the user picked from the pills above the chat input (spec §5.3).
// Each mode adds focused instructions to the system prompt and narrows the
// tool list to what that topic needs, so the model has fewer wrong choices.
public sealed record ChatMode(string Id, string Instructions, IReadOnlyList<string> Tools);

public static class ChatModes
{
    public static readonly IReadOnlyDictionary<string, ChatMode> All = new Dictionary<string, ChatMode>
    {
        ["review"] = new("review",
            "Weekly review: summarize how the user's current week is going — workouts, calories against the " +
            "weekly goal and the check-in streak — and compare it with last week.",
            [ChatToolExecutor.WorkoutSummary, ChatToolExecutor.WeeklyGoalProgress, ChatToolExecutor.CheckInStatus]),

        ["advice"] = new("advice",
            "Workout advice: first look at the user's recent workouts, then give 2–3 specific, practical " +
            "suggestions grounded in that data (variety, rest days, consistency, a realistic next session). " +
            "General fitness guidance only — no medical, injury or diet advice; if the user mentions pain, an " +
            "injury or a health condition, recommend seeing a professional.",
            [ChatToolExecutor.WorkoutSummary, ChatToolExecutor.WeeklyGoalProgress]),

        ["rank"] = new("rank",
            "Leaderboards: report where the user stands on each leaderboard and how far they are from the next " +
            "rank. Never mention other users.",
            [ChatToolExecutor.Rank]),

        ["points"] = new("points",
            "Points and rewards: explain the user's balance, where recent points came from, and whether today's " +
            "rewards are still waiting to be claimed (they're claimed from the Daily Tasks menu).",
            [ChatToolExecutor.PointsSummary]),

        ["goal"] = new("goal",
            "Weekly goal: report progress toward the weekly calorie goal and what it would take to reach it in " +
            "the days left this week.",
            [ChatToolExecutor.WeeklyGoalProgress, ChatToolExecutor.WorkoutSummary]),
    };

    public static ChatMode? Find(string? id) =>
        id is not null && All.TryGetValue(id, out var mode) ? mode : null;
}
