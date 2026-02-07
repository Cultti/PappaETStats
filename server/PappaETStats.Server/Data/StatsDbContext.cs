using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PappaETStats.Server.Domain;

namespace PappaETStats.Server.Data;

public sealed class StatsDbContext(DbContextOptions<StatsDbContext> options) : DbContext(options)
{
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchRound> MatchRounds => Set<MatchRound>();
    public DbSet<MatchSide> MatchSides => Set<MatchSide>();
    public DbSet<MatchPlayer> MatchPlayers => Set<MatchPlayer>();
    public DbSet<MatchPlayerWeaponStat> MatchPlayerWeaponStats => Set<MatchPlayerWeaponStat>();
    public DbSet<MatchPlayerClassStat> MatchPlayerClassStats => Set<MatchPlayerClassStat>();
    public DbSet<MatchObituary> MatchObituaries => Set<MatchObituary>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Use a stable GUID storage across providers.
        // - MySQL/MariaDB cannot use TEXT as a PK without a key length.
        // - SQLite doesn't care about the declared column type.
        // Using char(36) keeps migrations provider-agnostic and works everywhere.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = property.ClrType;
                if (clrType == typeof(Guid) || clrType == typeof(Guid?))
                {
                    property.SetColumnType("char(36)");
                }
            }
        }

        modelBuilder.Entity<Match>()
            .HasIndex(m => m.ExternalMatchId)
            .IsUnique();

        modelBuilder.Entity<Match>()
            .HasMany(m => m.Rounds)
            .WithOne(r => r.Match)
            .HasForeignKey(r => r.MatchId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MatchRound>()
            .HasIndex(r => new { r.MatchId, r.RoundNumber })
            .IsUnique();

        modelBuilder.Entity<MatchRound>()
            .HasMany(r => r.Sides)
            .WithOne(s => s.MatchRound)
            .HasForeignKey(s => s.MatchRoundId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MatchRound>()
            .HasMany(r => r.Obituaries)
            .WithOne(o => o.MatchRound)
            .HasForeignKey(o => o.MatchRoundId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MatchSide>()
            .HasMany(s => s.Players)
            .WithOne(p => p.MatchSide)
            .HasForeignKey(p => p.MatchSideId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MatchPlayer>()
            .HasMany(p => p.WeaponStats)
            .WithOne(w => w.MatchPlayer)
            .HasForeignKey(w => w.MatchPlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MatchPlayer>()
            .HasMany(p => p.ClassStats)
            .WithOne(c => c.MatchPlayer)
            .HasForeignKey(c => c.MatchPlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        base.OnModelCreating(modelBuilder);
    }
}
