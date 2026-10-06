using backend.Data;
using backend.Models;
using backend.Services.Ai;
using Microsoft.EntityFrameworkCore;

namespace backend.Tests.Services;

public class ChatQuotaServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTime Now = new(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc);

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ChatQuotaService NewService(AppDbContext db) =>
        new(db, new FixedTimeProvider(new DateTimeOffset(Now)));

    private static AiUsageLog Log(int userId, string feature = AiFeatures.Chat, bool success = true, DateTime? createdAt = null) =>
        new() { UserId = userId, Feature = feature, Success = success, CreatedAt = createdAt ?? Now.AddHours(-1) };

    [Fact]
    public async Task NoUsage_HasFullQuota()
    {
        var quota = await NewService(NewDb()).GetAsync(userId: 1);

        Assert.Equal(new ChatQuota(0, ChatQuotaService.DailyLimit, ChatQuotaService.DailyLimit), quota);
        Assert.False(quota.Exhausted);
    }

    [Fact]
    public async Task CountsOnlyTodaysSuccessfulChatRowsForThisUser()
    {
        var db = NewDb();
        db.AiUsageLogs.AddRange(
            Log(userId: 1),                                         // counts
            Log(userId: 1, createdAt: Now.Date),                    // counts: exactly 00:00 UTC
            Log(userId: 2),                                         // other user
            Log(userId: 1, success: false),                         // failed requests don't count
            Log(userId: 1, feature: AiFeatures.WeeklySummary),      // different feature
            Log(userId: 1, createdAt: Now.Date.AddTicks(-1)));      // yesterday
        await db.SaveChangesAsync();

        var quota = await NewService(db).GetAsync(userId: 1);

        Assert.Equal(2, quota.Used);
        Assert.Equal(ChatQuotaService.DailyLimit - 2, quota.Remaining);
    }

    [Fact]
    public async Task OverLimit_RemainingNeverGoesNegative()
    {
        var db = NewDb();
        // Concurrent requests can slip past the pre-check, so used may exceed the limit
        db.AiUsageLogs.AddRange(Enumerable.Range(0, ChatQuotaService.DailyLimit + 2).Select(_ => Log(userId: 1)));
        await db.SaveChangesAsync();

        var quota = await NewService(db).GetAsync(userId: 1);

        Assert.Equal(ChatQuotaService.DailyLimit + 2, quota.Used);
        Assert.Equal(0, quota.Remaining);
        Assert.True(quota.Exhausted);
    }
}
