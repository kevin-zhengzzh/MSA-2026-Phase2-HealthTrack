using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<CheckIn> CheckIns { get; set; }
    public DbSet<Skin> Skins { get; set; }
    public DbSet<UserSkin> UserSkins { get; set; }
    public DbSet<WorkoutRecord> WorkoutRecords { get; set; }
    public DbSet<PointTransaction> PointTransactions { get; set; }
    public DbSet<AiUsageLog> AiUsageLogs { get; set; }
    public DbSet<WeeklySummary> WeeklySummaries { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username).IsUnique();
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email).IsUnique();

        // One check-in per user per day
        modelBuilder.Entity<CheckIn>()
            .HasIndex(c => new { c.UserId, c.Date }).IsUnique();

        // Speeds up "has this user already earned today's workout bonus" lookups
        modelBuilder.Entity<WorkoutRecord>()
            .HasIndex(w => new { w.UserId, w.Date });

        // Speeds up fetching a user's point history in chronological order
        modelBuilder.Entity<PointTransaction>()
            .HasIndex(p => new { p.UserId, p.CreatedAt });

        // Speeds up the daily chat quota count ("this user's rows since 00:00 UTC")
        modelBuilder.Entity<AiUsageLog>()
            .HasIndex(a => new { a.UserId, a.CreatedAt });

        // One cached summary per user, week and language — also the cache lookup key
        modelBuilder.Entity<WeeklySummary>()
            .HasIndex(s => new { s.UserId, s.WeekStart, s.Language }).IsUnique();

        // UserSkin uses composite primary key
        modelBuilder.Entity<UserSkin>()
            .HasKey(us => new { us.UserId, us.SkinId });

        // Seed purchasable skins (null EquippedSkinId = default green theme).
        // Dark is reward-only — SkinController grants it automatically at a
        // 7-day check-in streak instead of letting it be bought with points.
        modelBuilder.Entity<Skin>().HasData(
            new Skin { Id = 1, Name = "Ocean",    Description = "Cool blue tones.",          PointCost = 100, Theme = "ocean"    },
            new Skin { Id = 2, Name = "Sunset",   Description = "Warm orange energy.",       PointCost = 200, Theme = "sunset"   },
            new Skin { Id = 3, Name = "Midnight", Description = "Deep purple mystery.",      PointCost = 300, Theme = "midnight" },
            new Skin { Id = 4, Name = "Cherry",   Description = "Bold pink for champions.",  PointCost = 500, Theme = "cherry"   },
            new Skin { Id = 5, Name = "Dark",     Description = "Easy on the eyes at night. Earned at a 7-day streak.", PointCost = 0, Theme = "dark", IsReward = true }
        );
    }
}
