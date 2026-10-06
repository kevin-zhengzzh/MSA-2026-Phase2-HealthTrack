using System.Text.Json;
using backend.Data;
using backend.Models;
using backend.Services.Ai;
using backend.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace backend.Tests.Services;

public class WeeklySummaryServiceTests
{
    // Wednesday — "last week" is Mon 2026-09-28 .. Sun 2026-10-04
    private static readonly DateOnly Today = new(2026, 10, 7);
    private static readonly DateOnly LastMonday = new(2026, 9, 28);
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
            new User { Id = SomeoneElse, Username = "other" });
        db.SaveChanges();
        return db;
    }

    private static WorkoutRecord Workout(int userId, DateOnly date, int calories, string type = "Running") =>
        new() { UserId = userId, Date = date, Calories = calories, WorkoutType = type };

    private static AppDbContext DbWithLastWeekWorkouts()
    {
        var db = NewDb();
        db.WorkoutRecords.AddRange(
            Workout(Me, LastMonday, 420),
            Workout(Me, LastMonday.AddDays(3), 520, "Cycling"),
            Workout(Me, LastMonday.AddDays(6), 450),            // Sunday — still last week
            Workout(Me, LastMonday.AddDays(-3), 300, "Gym"),    // the week before
            Workout(Me, LastMonday.AddDays(7), 999),            // this week — excluded
            Workout(SomeoneElse, LastMonday, 5000));            // another user — excluded
        db.CheckIns.AddRange(
            new CheckIn { UserId = Me, Date = LastMonday },
            new CheckIn { UserId = Me, Date = LastMonday.AddDays(1) });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task NoWorkoutsLastWeek_ReturnsFallbackWithoutCallingTheModel()
    {
        var model = new FakeChatModel();   // throws if called
        var service = new WeeklySummaryService(NewDb(), model);

        var result = await service.GetAsync(Me, Today, "en-NZ");

        Assert.Equal(WeeklySummaryService.SourceFallback, result.Source);
        Assert.Equal(LastMonday, result.WeekStart);
        Assert.Equal(LastMonday.AddDays(6), result.WeekEnd);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task GeneratesFromPrecomputedStatsForTheLastCompleteWeek()
    {
        var model = new FakeChatModel().Returns(FakeChatModel.Text("Great week!"));
        var service = new WeeklySummaryService(DbWithLastWeekWorkouts(), model);

        var result = await service.GetAsync(Me, Today, "en-US");

        Assert.Equal(WeeklySummaryService.SourceAi, result.Source);
        Assert.Equal("Great week!", result.Summary);

        var request = Assert.Single(model.Requests);
        Assert.Null(request.Tools);
        Assert.Contains("Write in English", request.Messages[0].Content);
        using var stats = JsonDocument.Parse(request.Messages[1].Content!);
        var s = stats.RootElement;
        Assert.Equal("2026-09-28", s.GetProperty("weekStart").GetString());
        Assert.Equal(3, s.GetProperty("workoutCount").GetInt32());
        Assert.Equal(3, s.GetProperty("activeDays").GetInt32());
        Assert.Equal(1390, s.GetProperty("totalCalories").GetInt32());
        Assert.Equal(2000, s.GetProperty("weeklyCalorieGoal").GetInt32());
        Assert.Equal(70, s.GetProperty("goalPercent").GetInt32());
        Assert.Equal(300, s.GetProperty("previousWeekCalories").GetInt32());
        Assert.Equal(2, s.GetProperty("checkInDays").GetInt32());
        Assert.Equal(2, s.GetProperty("byType").GetProperty("Running").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task SecondRequestForTheSameWeek_IsServedFromCache()
    {
        var db = DbWithLastWeekWorkouts();
        var model = new FakeChatModel().Returns(FakeChatModel.Text("Great week!"));

        await new WeeklySummaryService(db, model).GetAsync(Me, Today, "en");
        var second = await new WeeklySummaryService(db, model).GetAsync(Me, Today.AddDays(2), "en-GB");

        Assert.Equal(WeeklySummaryService.SourceCache, second.Source);
        Assert.Equal("Great week!", second.Summary);
        Assert.Single(model.Requests);
        Assert.Single(db.WeeklySummaries);
    }

    [Fact]
    public async Task EachLanguageIsGeneratedAndCachedSeparately()
    {
        var db = DbWithLastWeekWorkouts();
        var model = new FakeChatModel()
            .Returns(FakeChatModel.Text("Great week!"))
            .Returns(FakeChatModel.Text("这周很棒！"));
        var service = new WeeklySummaryService(db, model);

        await service.GetAsync(Me, Today, "en");
        var zh = await service.GetAsync(Me, Today, "zh-CN");

        Assert.Equal("这周很棒！", zh.Summary);
        Assert.Contains("Write in Simplified Chinese", model.Requests[1].Messages[0].Content);
        Assert.Equal(2, db.WeeklySummaries.Count());
    }

    [Theory]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh_TW", "zh")]
    [InlineData("ZH", "zh")]
    [InlineData("en-NZ", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void NormalizeLanguage_MapsToSupportedLanguagesOnly(string? input, string expected)
    {
        Assert.Equal(expected, WeeklySummaryService.NormalizeLanguage(input));
    }

    [Fact]
    public async Task ChineseFallbackWhenNoWorkouts()
    {
        var result = await new WeeklySummaryService(NewDb(), new FakeChatModel()).GetAsync(Me, Today, "zh-CN");

        Assert.Equal(WeeklySummaryService.SourceFallback, result.Source);
        Assert.Contains("上周", result.Summary);
    }

    [Fact]
    public async Task ModelFailure_PropagatesAndCachesNothing()
    {
        var db = DbWithLastWeekWorkouts();
        var model = new FakeChatModel().Throws(new ChatModelException("DeepSeek returned 503", 503));

        await Assert.ThrowsAsync<ChatModelException>(() => new WeeklySummaryService(db, model).GetAsync(Me, Today, "en"));

        Assert.Empty(db.WeeklySummaries);
    }

    [Fact]
    public async Task EmptyModelReply_IsTreatedAsAFailure()
    {
        var db = DbWithLastWeekWorkouts();
        var model = new FakeChatModel().Returns(FakeChatModel.Text("   "));

        await Assert.ThrowsAsync<ChatModelException>(() => new WeeklySummaryService(db, model).GetAsync(Me, Today, "en"));

        Assert.Empty(db.WeeklySummaries);
    }
}
