using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Matches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    ExternalMatchId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MapName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Config = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ServerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ServerIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ServerPort = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Matches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MatchRounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MatchId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RoundNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DefenderTeam = table.Column<int>(type: "INTEGER", nullable: false),
                    WinnerTeam = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeLimit = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    NextTimeLimit = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RoundStartMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RoundEndMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RoundStartUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    RoundEndUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    IngestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchRounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchRounds_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatchObituaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MatchRoundId = table.Column<Guid>(type: "char(36)", nullable: false),
                    TimestampMs = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetGuid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AttackerGuid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MeansOfDeath = table.Column<int>(type: "INTEGER", nullable: false),
                    AttackerRespawnTime = table.Column<int>(type: "INTEGER", nullable: false),
                    VictimRespawnTime = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchObituaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchObituaries_MatchRounds_MatchRoundId",
                        column: x => x.MatchRoundId,
                        principalTable: "MatchRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatchSides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MatchRoundId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Team = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDefender = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsWinner = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlayerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalXp = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalDamageGiven = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalDamageReceived = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTeamDamageGiven = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTeamDamageReceived = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalGibs = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalSelfKills = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTeamKills = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalTeamGibs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchSides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchSides_MatchRounds_MatchRoundId",
                        column: x => x.MatchRoundId,
                        principalTable: "MatchRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatchPlayers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MatchSideId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ClientNum = table.Column<int>(type: "INTEGER", nullable: false),
                    Guid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Team = table.Column<int>(type: "INTEGER", nullable: false),
                    Rounds = table.Column<int>(type: "INTEGER", nullable: false),
                    Xp = table.Column<int>(type: "INTEGER", nullable: false),
                    TimePlayedPercent = table.Column<double>(type: "REAL", nullable: false),
                    DamageGiven = table.Column<int>(type: "INTEGER", nullable: false),
                    DamageReceived = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamDamageGiven = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamDamageReceived = table.Column<int>(type: "INTEGER", nullable: false),
                    Gibs = table.Column<int>(type: "INTEGER", nullable: false),
                    SelfKills = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamKills = table.Column<int>(type: "INTEGER", nullable: false),
                    TeamGibs = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchPlayers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchPlayers_MatchSides_MatchSideId",
                        column: x => x.MatchSideId,
                        principalTable: "MatchSides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatchPlayerClassStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MatchPlayerId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ClassId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchPlayerClassStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchPlayerClassStats_MatchPlayers_MatchPlayerId",
                        column: x => x.MatchPlayerId,
                        principalTable: "MatchPlayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MatchPlayerWeaponStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MatchPlayerId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Weapon = table.Column<int>(type: "INTEGER", nullable: false),
                    Hits = table.Column<int>(type: "INTEGER", nullable: false),
                    Atts = table.Column<int>(type: "INTEGER", nullable: false),
                    Kills = table.Column<int>(type: "INTEGER", nullable: false),
                    Deaths = table.Column<int>(type: "INTEGER", nullable: false),
                    Headshots = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchPlayerWeaponStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchPlayerWeaponStats_MatchPlayers_MatchPlayerId",
                        column: x => x.MatchPlayerId,
                        principalTable: "MatchPlayers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Matches_ExternalMatchId",
                table: "Matches",
                column: "ExternalMatchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatchObituaries_MatchRoundId",
                table: "MatchObituaries",
                column: "MatchRoundId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchPlayerClassStats_MatchPlayerId",
                table: "MatchPlayerClassStats",
                column: "MatchPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchPlayers_MatchSideId",
                table: "MatchPlayers",
                column: "MatchSideId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchPlayerWeaponStats_MatchPlayerId",
                table: "MatchPlayerWeaponStats",
                column: "MatchPlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchRounds_MatchId_RoundNumber",
                table: "MatchRounds",
                columns: new[] { "MatchId", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatchSides_MatchRoundId",
                table: "MatchSides",
                column: "MatchRoundId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchObituaries");

            migrationBuilder.DropTable(
                name: "MatchPlayerClassStats");

            migrationBuilder.DropTable(
                name: "MatchPlayerWeaponStats");

            migrationBuilder.DropTable(
                name: "MatchPlayers");

            migrationBuilder.DropTable(
                name: "MatchSides");

            migrationBuilder.DropTable(
                name: "MatchRounds");

            migrationBuilder.DropTable(
                name: "Matches");
        }
    }
}
