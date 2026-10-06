using System.Security.Claims;
using backend.Data;
using backend.DTOs;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CheckInController : ControllerBase
{
    // Consecutive check-ins that unlock the reward-only Dark skin. Shared with
    // the AI chat tools so both report the same milestone.
    public const int RewardSkinStreak = 7;

    private readonly AppDbContext _db;

    public CheckInController(AppDbContext db) => _db = db;

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Trusts the client's local date, clamped to ±1 day of server UTC —
    // real-world timezone offsets never diverge from UTC by more than a day either way.
    internal static DateOnly ResolveToday(string? localDate)
    {
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        if (localDate is not null && DateOnly.TryParse(localDate, out var parsed))
        {
            var diff = parsed.DayNumber - utcToday.DayNumber;
            if (diff is >= -1 and <= 1) return parsed;
        }
        return utcToday;
    }

    [HttpGet("today")]
    public async Task<IActionResult> GetToday([FromQuery] string? localDate = null)
    {
        var today = ResolveToday(localDate);
        var checkIn = await _db.CheckIns
            .FirstOrDefaultAsync(c => c.UserId == UserId && c.Date == today);

        if (checkIn is null)
            return Ok(new { checkedIn = false, result = (CheckInResult?)null });

        var user = await _db.Users.FindAsync(UserId);
        var percentSurpassed = await ComputePercentSurpassed(today, checkIn.CreatedAt);
        var result = new CheckInResult(checkIn.Id, today, checkIn.PointsEarned, user!.Points, user.Streak, checkIn.CreatedAt, percentSurpassed);
        return Ok(new { checkedIn = true, result });
    }

    // Percentile among ALL other users — not just today's check-ins. Anyone
    // who checked in later than us today, or hasn't checked in at all yet,
    // counts as surpassed; only users who checked in earlier today don't.
    private async Task<int> ComputePercentSurpassed(DateOnly date, DateTime createdAt)
    {
        var totalOtherUsers = await _db.Users.CountAsync(u => u.Id != UserId);
        if (totalOtherUsers == 0) return 100;

        var earlierCount = await _db.CheckIns
            .CountAsync(c => c.Date == date && c.UserId != UserId && c.CreatedAt < createdAt);

        var surpassed = totalOtherUsers - earlierCount;
        return (int)Math.Round(surpassed * 100.0 / totalOtherUsers);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory()
    {
        var history = await _db.CheckIns
            .Where(c => c.UserId == UserId)
            .OrderByDescending(c => c.Date)
            .Select(c => new { c.Id, c.Date, c.Note, c.CreatedAt })
            .ToListAsync();
        return Ok(history);
    }

    // Unlocks the reward-only Dark skin the first time a user's streak
    // reaches 7 days — idempotent (checks ownership first) so it's safe to
    // call on every check-in once the threshold has been passed.
    private async Task GrantStreakRewardSkin(int streak)
    {
        if (streak < RewardSkinStreak) return;

        var alreadyOwnsReward = await _db.UserSkins
            .AnyAsync(us => us.UserId == UserId && us.Skin.IsReward);
        if (alreadyOwnsReward) return;

        var rewardSkin = await _db.Skins.FirstOrDefaultAsync(s => s.IsReward);
        if (rewardSkin is null) return;

        _db.UserSkins.Add(new UserSkin
        {
            UserId = UserId,
            SkinId = rewardSkin.Id,
            UnlockedAt = DateTime.UtcNow,
            Seen = false
        });
    }

    [HttpPost]
    public async Task<IActionResult> CheckIn([FromBody] string? note = null, [FromQuery] string? localDate = null)
    {
        var today = ResolveToday(localDate);
        var alreadyIn = await _db.CheckIns
            .AnyAsync(c => c.UserId == UserId && c.Date == today);

        if (alreadyIn)
            return Conflict(new { message = "Already checked in today. Come back tomorrow!" });

        var user = await _db.Users.FindAsync(UserId);
        if (user is null) return NotFound();

        // Update streak. Continuation is judged by whether a check-in exists for
        // the user's local yesterday — not by LastCheckIn, which is a UTC instant:
        // in UTC+12/13 a morning check-in falls on the previous UTC date, which
        // used to reset the streak the next morning.
        var yesterday = today.AddDays(-1);
        var checkedInYesterday = await _db.CheckIns
            .AnyAsync(c => c.UserId == UserId && c.Date == yesterday);
        user.Streak = checkedInYesterday ? user.Streak + 1 : 1;
        user.LastCheckIn = DateTime.UtcNow;

        // Points: 10 base + (streak × 2) bonus, capped at 50 bonus.
        // Computed now (while the streak is known) but only credited to the
        // user once claimed via POST /api/rewards/claim.
        var bonus = Math.Min(user.Streak * 2, 50);
        var earned = 10 + bonus;

        var checkIn = new CheckIn
        {
            UserId = UserId,
            Date = today,
            Note = note,
            CreatedAt = DateTime.UtcNow,
            PointsEarned = earned,
            Claimed = false
        };
        _db.CheckIns.Add(checkIn);

        await GrantStreakRewardSkin(user.Streak);

        await _db.SaveChangesAsync();

        var percentSurpassed = await ComputePercentSurpassed(today, checkIn.CreatedAt);

        return Ok(new CheckInResult(checkIn.Id, today, earned, user.Points, user.Streak, checkIn.CreatedAt, percentSurpassed));
    }
}
