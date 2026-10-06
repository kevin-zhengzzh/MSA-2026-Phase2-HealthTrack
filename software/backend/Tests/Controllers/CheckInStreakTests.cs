using System.Security.Claims;
using backend.Controllers;
using backend.Data;
using backend.DTOs;
using backend.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Tests.Controllers;

// Streak continuation must be judged in the user's local calendar (the
// CheckIns.Date they checked in on), not by the UTC date of LastCheckIn —
// otherwise a morning check-in in UTC+12/13 lands on the previous UTC day
// and the next morning's check-in wrongly resets the streak.
public class CheckInStreakTests
{
    private const int Me = 1;
    private static readonly DateOnly Today = CheckInController.ResolveToday(null);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static CheckInController NewController(AppDbContext db) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Me.ToString())], "test")),
            },
        },
    };

    private static async Task<CheckInResult> CheckIn(AppDbContext db)
    {
        var result = await NewController(db).CheckIn(localDate: Today.ToString("yyyy-MM-dd"));
        return Assert.IsType<CheckInResult>(Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task CheckedInYesterdayLocally_ContinuesStreak_EvenWhenLastCheckInIsAnEarlierUtcDay()
    {
        var db = NewDb();
        // A New Zealand user who checked in at 9am local "yesterday": that
        // instant is the evening of the day before in UTC.
        db.Users.Add(new User
        {
            Id = Me,
            Username = "nz_user",
            Streak = 3,
            LastCheckIn = Yesterday.AddDays(-1).ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc),
        });
        db.CheckIns.Add(new CheckIn { UserId = Me, Date = Yesterday });
        await db.SaveChangesAsync();

        var result = await CheckIn(db);

        Assert.Equal(4, result.Streak);
    }

    [Fact]
    public async Task NoCheckInYesterday_ResetsStreakToOne_EvenWhenLastCheckInLooksRecentInUtc()
    {
        var db = NewDb();
        // Last check-in was two local days ago, even though its UTC date is yesterday
        db.Users.Add(new User
        {
            Id = Me,
            Username = "user",
            Streak = 3,
            LastCheckIn = Yesterday.ToDateTime(new TimeOnly(1, 0), DateTimeKind.Utc),
        });
        db.CheckIns.Add(new CheckIn { UserId = Me, Date = Today.AddDays(-2) });
        await db.SaveChangesAsync();

        var result = await CheckIn(db);

        Assert.Equal(1, result.Streak);
    }

    [Fact]
    public async Task FirstEverCheckIn_StartsStreakAtOne()
    {
        var db = NewDb();
        db.Users.Add(new User { Id = Me, Username = "new_user" });
        await db.SaveChangesAsync();

        var result = await CheckIn(db);

        Assert.Equal(1, result.Streak);
    }
}
