using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services.Ai;

public record ChatQuota(int Used, int Limit, int Remaining)
{
    public bool Exhausted => Remaining == 0;
}

// Daily chat limit (spec NF-4). Derived from AiUsageLog rather than a separate
// counter, so there's one source of truth for "how many messages today".
public class ChatQuotaService
{
    public const int DailyLimit = 30;

    private readonly AppDbContext _db;
    private readonly TimeProvider _time;

    public ChatQuotaService(AppDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    // "Today" is the UTC day. Only successful chat requests count, so a
    // DeepSeek outage doesn't eat into the user's allowance.
    public async Task<ChatQuota> GetAsync(int userId, CancellationToken ct = default)
    {
        var startOfUtcDay = _time.GetUtcNow().UtcDateTime.Date;
        var used = await _db.AiUsageLogs.CountAsync(l =>
            l.UserId == userId &&
            l.Feature == AiFeatures.Chat &&
            l.Success &&
            l.CreatedAt >= startOfUtcDay, ct);

        return new ChatQuota(used, DailyLimit, Math.Max(0, DailyLimit - used));
    }
}
