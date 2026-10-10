using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PappaETStats.Server.Domain;

namespace PappaETStats.Server.Data;

public sealed class StatsDbContext(DbContextOptions<StatsDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<LastReadyUp> LastReadyUps => Set<LastReadyUp>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchSeries> MatchSeries => Set<MatchSeries>();
    public DbSet<RoundEvent> RoundEvents => Set<RoundEvent>();
    public DbSet<RosterSnapshot> RosterSnapshots => Set<RosterSnapshot>();
    public DbSet<MatchRound> MatchRounds => Set<MatchRound>();
    public DbSet<MatchSide> MatchSides => Set<MatchSide>();
    public DbSet<MatchPlayer> MatchPlayers => Set<MatchPlayer>();
    public DbSet<MatchPlayerWeaponStat> MatchPlayerWeaponStats => Set<MatchPlayerWeaponStat>();
    public DbSet<MatchPlayerClassStat> MatchPlayerClassStats => Set<MatchPlayerClassStat>();
    public DbSet<MatchObituary> MatchObituaries => Set<MatchObituary>();
    public DbSet<PlayerRegistrationToken> PlayerRegistrationTokens => Set<PlayerRegistrationToken>();

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

        modelBuilder.Entity<MatchSeries>().HasIndex(s => new { s.ServerKey, s.LastPlayedAtUnix });
        modelBuilder.Entity<MatchSeries>().HasMany(s => s.Maps).WithOne(m => m.Series)
            .HasForeignKey(m => m.SeriesId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<MatchRound>().HasMany(r => r.Events).WithOne(e => e.MatchRound)
            .HasForeignKey(e => e.MatchRoundId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RoundEvent>().HasIndex(e => new { e.MatchRoundId, e.Sequence }).IsUnique();
        modelBuilder.Entity<RoundEvent>().HasIndex(e => e.Label);
        modelBuilder.Entity<MatchSeries>().Property(s => s.ServerKey).HasColumnType("varchar(512)");
        modelBuilder.Entity<MatchSeries>().Property(s => s.StartedAtUnix).HasColumnType("bigint");
        modelBuilder.Entity<MatchSeries>().Property(s => s.LastPlayedAtUnix).HasColumnType("bigint");
        modelBuilder.Entity<MatchSeries>().Property(s => s.EndedAtUnix).HasColumnType("bigint");
        modelBuilder.Entity<Match>().Property(m => m.SeriesTeam1Faction).HasDefaultValue(1);
        modelBuilder.Entity<MatchRound>().Property(r => r.StatsSource).HasDefaultValue("legacy");
        modelBuilder.Entity<MatchRound>().Property(r => r.PayloadJson).HasColumnType("longtext");
        modelBuilder.Entity<MatchRound>().Property(r => r.SourcePayloadJson).HasColumnType("longtext");
        modelBuilder.Entity<MatchPlayer>().Property(p => p.DetailsJson).HasColumnType("longtext");
        modelBuilder.Entity<RoundEvent>().Property(e => e.DataJson).HasColumnType("longtext");
        modelBuilder.Entity<RoundEvent>().Property(e => e.Label).HasColumnType("varchar(128)");
        modelBuilder.Entity<RoundEvent>().Property(e => e.Group).HasColumnType("varchar(64)");
        modelBuilder.Entity<RoundEvent>().Property(e => e.LevelTime).HasColumnType("bigint");
        modelBuilder.Entity<RoundEvent>().Property(e => e.UnixTimeMs).HasColumnType("bigint");
        modelBuilder.Entity<RosterSnapshot>().Property(s => s.Id).HasColumnType("varchar(64)");
        modelBuilder.Entity<RosterSnapshot>().Property(s => s.TimestampUnix).HasColumnType("bigint");
        modelBuilder.Entity<RosterSnapshot>().Property(s => s.PayloadJson).HasColumnType("longtext");
        foreach (var name in new[] { "DistanceMeters", "AliveSeconds", "EngagedSeconds", "DownedSeconds" })
            modelBuilder.Entity<MatchPlayer>().Property(name).HasColumnType("double");

        modelBuilder.Entity<LastReadyUp>().HasKey(r => r.EventId);
        modelBuilder.Entity<LastReadyUp>().Property(r => r.EventId).HasColumnType("varchar(64)");
        modelBuilder.Entity<LastReadyUp>().Property(r => r.PlayerGuid).HasColumnType("varchar(64)");
        modelBuilder.Entity<LastReadyUp>().HasIndex(r => r.PlayerGuid);

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

        modelBuilder.Entity<MatchObituary>()
            .HasIndex(o => o.TargetGuid);

        modelBuilder.Entity<MatchObituary>()
            .HasIndex(o => new { o.TargetGuid, o.AttackerGuid });

        modelBuilder.Entity<Player>()
            .HasKey(p => p.Guid);

        modelBuilder.Entity<Player>()
            .Property(p => p.Guid)
            .HasColumnType("varchar(64)");

        modelBuilder.Entity<Player>()
            .Property(p => p.DiscordId)
            .HasColumnType("varchar(64)");

        modelBuilder.Entity<Player>()
            .HasIndex(p => p.DiscordId)
            .IsUnique();

        modelBuilder.Entity<PlayerRegistrationToken>()
            .Property(t => t.DiscordId)
            .HasColumnType("varchar(64)");

        modelBuilder.Entity<PlayerRegistrationToken>()
            .Property(t => t.UsedByEtGuid)
            .HasColumnType("varchar(64)");

        modelBuilder.Entity<PlayerRegistrationToken>()
            .HasIndex(t => t.DiscordId);

        base.OnModelCreating(modelBuilder);
    }
}
