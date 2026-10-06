using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace backend.Tests.Services;

public class ChatToolExecutorTests
{
    // Wednesday — this week is Mon 2026-10-05 .. today; last week is 09-28 .. 10-04
    private static readonly DateOnly Today = new(2026, 10, 7);
    private const int Me = 1;
    private const int SomeoneElse = 2;

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Users.AddRange(
            new User { Id = Me, Username = "me", WeeklyCalorieGoal = 2000 },
            new User { Id = SomeoneElse, Username = "other", WeeklyCalorieGoal = 3000 });
        db.SaveChanges();
        return db;
    }

    private static (ChatToolExecutor executor, AiUsageTracker tracker) NewExecutor(AppDbContext db)
    {
        var tracker = new AiUsageTracker(db, NullLogger<AiUsageTracker>.Instance);
        return (new ChatToolExecutor(db, tracker), tracker);
    }

    private static WorkoutRecord Workout(int userId, DateOnly date, int calories, string type = "Running") =>
        new() { UserId = userId, Date = date, Calories = calories, WorkoutType = type };

    private static async Task<JsonElement> Run(ChatToolExecutor executor, string tool, string args = "{}", int userId = Me)
    {
        var json = await executor.ExecuteAsync(userId, Today, new ChatToolCall("call_1", tool, args));
        return JsonDocument.Parse(json).RootElement;
    }

    // ---- NF-1: per-user isolation ----

    [Fact]
    public async Task WorkoutSummary_OnlyReturnsTheCallingUsersData_EvenIfArgsNameAnotherUser()
    {
        var db = NewDb();
        db.WorkoutRecords.AddRange(
            Workout(Me, Today, 300),
            Workout(SomeoneElse, Today, 900),
            Workout(SomeoneElse, Today.AddDays(-1), 900));
        await db.SaveChangesAsync();
        var (executor, _) = NewExecutor(db);

        // A model coaxed by prompt injection might add a userId argument; it must be ignored
        var result = await Run(executor, ChatToolExecutor.WorkoutSummary, """{"range":"this_week","userId":2}""");

        Assert.Equal(1, result.GetProperty("workoutCount").GetInt32());
        Assert.Equal(300, result.GetProperty("totalCalories").GetInt32());
    }

    [Fact]
    public async Task CheckInStatus_IgnoresOtherUsersCheckIns()
    {
        var db = NewDb();
        db.CheckIns.Add(new CheckIn { UserId = SomeoneElse, Date = Today });
        await db.SaveChangesAsync();
        var (executor, _) = NewExecutor(db);

        var result = await Run(executor, ChatToolExecutor.CheckInStatus);

        Assert.False(result.GetProperty("checkedInToday").GetBoolean());
    }

    [Fact]
    public async Task WeeklyGoalProgress_UsesOnlyTheCallingUsersGoalAndWorkouts()
    {
        var db = NewDb();
        db.WorkoutRecords.AddRange(
            Workout(Me, new DateOnly(2026, 10, 5), 500),
            Workout(Me, Today, 700, "Gym"),
            Workout(Me, new DateOnly(2026, 10, 4), 999),   // last Sunday — previous week
            Workout(SomeoneElse, Today, 2500));
        await db.SaveChangesAsync();
        var (executor, _) = NewExecutor(db);

        var result = await Run(executor, ChatToolExecutor.WeeklyGoalProgress);

        Assert.Equal("2026-10-05", result.GetProperty("weekStart").GetString());
        Assert.Equal(2000, result.GetProperty("goal").GetInt32());
        Assert.Equal(1200, result.GetProperty("caloriesSoFar").GetInt32());
        Assert.Equal(800, result.GetProperty("remainingCalories").GetInt32());
        Assert.Equal(60, result.GetProperty("percent").GetInt32());
        Assert.Equal(4, result.GetProperty("daysLeftInWeek").GetInt32());   // Thu..Sun
    }

    // ---- T-1 get_workout_summary ----

    [Theory]
    [InlineData("this_week", "2026-10-05", "2026-10-07")]
    [InlineData("last_week", "2026-09-28", "2026-10-04")]
    [InlineData("this_month", "2026-10-01", "2026-10-07")]
    [InlineData("last_month", "2026-09-01", "2026-09-30")]
    public void ResolveRange_UsesMondayWeeksAndCalendarMonths(string range, string from, string to)
    {
        var (f, t) = ChatToolExecutor.ResolveRange(range, Today);

        Assert.Equal(DateOnly.Parse(from), f);
        Assert.Equal(DateOnly.Parse(to), t);
    }

    [Fact]
    public async Task WorkoutSummary_AggregatesByTypeAndActiveDays()
    {
        var db = NewDb();
        db.WorkoutRecords.AddRange(
            Workout(Me, new DateOnly(2026, 9, 29), 400, "Running"),
            Workout(Me, new DateOnly(2026, 9, 29), 200, "Yoga"),
            Workout(Me, new DateOnly(2026, 10, 2), 500, "Running"),
            Workout(Me, Today, 999));   // this week, excluded from last_week
        await db.SaveChangesAsync();
        var (executor, _) = NewExecutor(db);

        var result = await Run(executor, ChatToolExecutor.WorkoutSummary, """{"range":"last_week"}""");

        Assert.Equal(3, result.GetProperty("workoutCount").GetInt32());
        Assert.Equal(2, result.GetProperty("activeDays").GetInt32());
        Assert.Equal(1100, result.GetProperty("totalCalories").GetInt32());
        var running = result.GetProperty("byType").GetProperty("Running");
        Assert.Equal(2, running.GetProperty("count").GetInt32());
        Assert.Equal(900, running.GetProperty("calories").GetInt32());
    }

    // ---- T-2 get_checkin_status ----

    [Fact]
    public async Task CheckInStatus_StreakIsAliveWhenLastCheckInWasYesterday()
    {
        var db = NewDb();
        var me = await db.Users.FindAsync(Me);
        me!.Streak = 5;
        // A New Zealand morning check-in "yesterday" (10-06 local) is 10-05 in
        // UTC — the streak must still count as alive, judged by the local date
        me.LastCheckIn = new DateTime(2026, 10, 5, 20, 0, 0, DateTimeKind.Utc);
        db.CheckIns.Add(new CheckIn { UserId = Me, Date = Today.AddDays(-1) });
        await db.SaveChangesAsync();
        var (executor, _) = NewExecutor(db);

        var result = await Run(executor, ChatToolExecutor.CheckInStatus);

        Assert.Equal(5, result.GetProperty("streak").GetInt32());
        Assert.False(result.GetProperty("checkedInToday").GetBoolean());
        Assert.Equal(2, result.GetProperty("checkInsUntilRewardSkin").GetInt32());
    }

    [Fact]
    public async Task CheckInStatus_StaleStoredStreakIsReportedAsBroken()
    {
        // User.Streak only resets on the next check-in, so it can be stale
        var db = NewDb();
        var me = await db.Users.FindAsync(Me);
        me!.Streak = 5;
        db.CheckIns.Add(new CheckIn { UserId = Me, Date = Today.AddDays(-3) });
        await db.SaveChangesAsync();
        var (executor, _) = NewExecutor(db);

        var result = await Run(executor, ChatToolExecutor.CheckInStatus);

        Assert.Equal(0, result.GetProperty("streak").GetInt32());
        Assert.Equal(7, result.GetProperty("checkInsUntilRewardSkin").GetInt32());
    }

    [Fact]
    public async Task CheckInStatus_OwnedRewardSkinMeansNothingLeftToUnlock()
    {
        var db = NewDb();
        db.Skins.Add(new Skin { Id = 5, Name = "Dark", IsReward = true });
        db.UserSkins.Add(new UserSkin { UserId = Me, SkinId = 5 });
        db.CheckIns.Add(new CheckIn { UserId = Me, Date = Today });
        await db.SaveChangesAsync();
        var (executor, _) = NewExecutor(db);

        var result = await Run(executor, ChatToolExecutor.CheckInStatus);

        Assert.True(result.GetProperty("ownsRewardSkin").GetBoolean());
        Assert.True(result.GetProperty("checkedInToday").GetBoolean());
        Assert.Equal(0, result.GetProperty("checkInsUntilRewardSkin").GetInt32());
    }

    // ---- Failure handling: errors are tool results, never exceptions ----

    [Theory]
    [InlineData(ChatToolExecutor.WorkoutSummary, """{"range":"forever"}""")]
    [InlineData(ChatToolExecutor.WorkoutSummary, "{}")]
    [InlineData(ChatToolExecutor.WorkoutSummary, "not json")]
    [InlineData("delete_all_users", "{}")]
    public async Task BadCalls_ReturnAnErrorResultInsteadOfThrowing(string tool, string args)
    {
        var (executor, _) = NewExecutor(NewDb());

        var result = await Run(executor, tool, args);

        Assert.True(result.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task EveryCall_IsRecordedInTheUsageTracker()
    {
        var (executor, tracker) = NewExecutor(NewDb());

        await Run(executor, ChatToolExecutor.CheckInStatus);
        await Run(executor, ChatToolExecutor.WeeklyGoalProgress);

        Assert.Equal([ChatToolExecutor.CheckInStatus, ChatToolExecutor.WeeklyGoalProgress], tracker.ToolsCalled);
    }

    [Fact]
    public void Definitions_CoverAllFiveTools()
    {
        Assert.Equal(
            [ChatToolExecutor.WorkoutSummary, ChatToolExecutor.CheckInStatus, ChatToolExecutor.WeeklyGoalProgress,
             ChatToolExecutor.Rank, ChatToolExecutor.PointsSummary],
            ChatToolExecutor.Definitions.Select(d => d.Name));
    }

    [Fact]
    public void DefinitionsFor_NullMeansAllToolsAndAListNarrowsThem()
    {
        Assert.Same(ChatToolExecutor.Definitions, ChatToolExecutor.DefinitionsFor(null));
        Assert.Equal([ChatToolExecutor.Rank], ChatToolExecutor.DefinitionsFor([ChatToolExecutor.Rank]).Select(d => d.Name));
    }

    // ---- T-4 get_rank ----

    [Fact]
    public async Task Rank_MatchesLeaderboardOrderingAndReturnsOnlyMyPosition()
    {
        var db = NewDb();
        var me = await db.Users.FindAsync(Me);
        me!.Points = 120;
        me.Streak = 2;
        var other = await db.Users.FindAsync(SomeoneElse);
        other!.Points = 150;
        other.Streak = 2;   // tie on streak: lower Id ranks first, like LeaderboardController
        db.WorkoutRecords.AddRange(Workout(Me, Today, 400), Workout(SomeoneElse, Today, 300));
        db.CheckIns.AddRange(
            new CheckIn { UserId = SomeoneElse, Date = Today, CreatedAt = new DateTime(2026, 10, 6, 19, 0, 0, DateTimeKind.Utc) },
            new CheckIn { UserId = Me, Date = Today, CreatedAt = new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        var (executor, _) = NewExecutor(db);

        var result = await Run(executor, ChatToolExecutor.Rank);

        Assert.Equal(2, result.GetProperty("totalUsers").GetInt32());
        var points = result.GetProperty("points");
        Assert.Equal(2, points.GetProperty("rank").GetInt32());
        Assert.Equal(30, points.GetProperty("behindNextRank").GetInt32());
        var streak = result.GetProperty("streak");
        Assert.Equal(1, streak.GetProperty("rank").GetInt32());
        Assert.Equal(JsonValueKind.Null, streak.GetProperty("behindNextRank").ValueKind);
        Assert.Equal(1, result.GetProperty("caloriesToday").GetProperty("rank").GetInt32());
        Assert.Equal(2, result.GetProperty("earliestCheckInToday").GetProperty("rank").GetInt32());
        // Nothing identifying the other user leaks into the result (D-4)
        Assert.DoesNotContain("other", result.GetRawText());
    }

    [Fact]
    public async Task Rank_NotCheckedInToday_HasNoCheckInRank()
    {
        var (executor, _) = NewExecutor(NewDb());

        var result = await Run(executor, ChatToolExecutor.Rank);

        var checkIn = result.GetProperty("earliestCheckInToday");
        Assert.False(checkIn.GetProperty("checkedInToday").GetBoolean());
        Assert.Equal(JsonValueKind.Null, checkIn.GetProperty("rank").ValueKind);
    }

    // ---- T-5 get_points_summary ----

    [Fact]
    public async Task PointsSummary_ReportsBalanceRecentActivityAndUnclaimedRewards()
    {
        var db = NewDb();
        var me = await db.Users.FindAsync(Me);
        me!.Points = 230;
        db.PointTransactions.AddRange(
            new PointTransaction { UserId = Me, Amount = 20, Reason = "Daily check-in", CreatedAt = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc) },
            new PointTransaction { UserId = Me, Amount = -100, Reason = "Purchased Ocean", CreatedAt = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc) },
            new PointTransaction { UserId = Me, Amount = 50, Reason = "Daily check-in", CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc) },  // older than 7 days
            new PointTransaction { UserId = SomeoneElse, Amount = 999, Reason = "Daily check-in", CreatedAt = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc) });
        db.CheckIns.Add(new CheckIn { UserId = Me, Date = Today, PointsEarned = 14, Claimed = false });
        db.WorkoutRecords.Add(new WorkoutRecord { UserId = Me, Date = Today, Calories = 300, WorkoutType = "Gym", PointsEarned = 10, Claimed = true });
        await db.SaveChangesAsync();
        var (executor, _) = NewExecutor(db);

        var result = await Run(executor, ChatToolExecutor.PointsSummary);

        Assert.Equal(230, result.GetProperty("balance").GetInt32());
        Assert.Equal(20, result.GetProperty("earnedLast7Days").GetInt32());
        Assert.Equal(100, result.GetProperty("spentLast7Days").GetInt32());
        var unclaimed = result.GetProperty("unclaimedToday");
        Assert.Equal(14, unclaimed.GetProperty("checkIn").GetInt32());
        Assert.Equal(0, unclaimed.GetProperty("workout").GetInt32());   // already claimed
        Assert.Equal(14, unclaimed.GetProperty("total").GetInt32());
        var recent = result.GetProperty("recentTransactions");
        Assert.Equal(3, recent.GetArrayLength());   // only my transactions
        Assert.Equal("Daily check-in", recent[0].GetProperty("reason").GetString());
    }
}
