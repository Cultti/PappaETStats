using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class OksiiStatsAndMatchSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                table: "MatchRounds",
                type: "longtext",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePayloadJson",
                table: "MatchRounds",
                type: "longtext",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatsSource",
                table: "MatchRounds",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<double>(
                name: "AliveSeconds",
                table: "MatchPlayers",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Assists",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetailsJson",
                table: "MatchPlayers",
                type: "longtext",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DistanceMeters",
                table: "MatchPlayers",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DownedSeconds",
                table: "MatchPlayers",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EngagedSeconds",
                table: "MatchPlayers",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectivesDefused",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectivesDestroyed",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectivesPlanted",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectivesRepaired",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectivesReturned",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectivesSecured",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpawnCount",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MapNumber",
                table: "Matches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SeriesId",
                table: "Matches",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeriesTeam1Faction",
                table: "Matches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "SourceMatchId",
                table: "Matches",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MatchSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    ServerKey = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                    StartedAtUnix = table.Column<long>(type: "bigint", nullable: false),
                    LastPlayedAtUnix = table.Column<long>(type: "bigint", nullable: false),
                    EndedAtUnix = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchSeries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RosterSnapshots",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    ServerIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ServerPort = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TimestampUnix = table.Column<long>(type: "bigint", nullable: false),
                    PayloadJson = table.Column<string>(type: "longtext", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RosterSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoundEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MatchRoundId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    LevelTime = table.Column<long>(type: "bigint", nullable: false),
                    UnixTimeMs = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Group = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    DataJson = table.Column<string>(type: "longtext", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoundEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoundEvents_MatchRounds_MatchRoundId",
                        column: x => x.MatchRoundId,
                        principalTable: "MatchRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Matches_SeriesId",
                table: "Matches",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_MatchSeries_ServerKey_LastPlayedAtUnix",
                table: "MatchSeries",
                columns: new[] { "ServerKey", "LastPlayedAtUnix" });

            migrationBuilder.CreateIndex(
                name: "IX_RoundEvents_Label",
                table: "RoundEvents",
                column: "Label");

            migrationBuilder.CreateIndex(
                name: "IX_RoundEvents_MatchRoundId_Sequence",
                table: "RoundEvents",
                columns: new[] { "MatchRoundId", "Sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Matches_MatchSeries_SeriesId",
                table: "Matches",
                column: "SeriesId",
                principalTable: "MatchSeries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Matches_MatchSeries_SeriesId",
                table: "Matches");

            migrationBuilder.DropTable(
                name: "MatchSeries");

            migrationBuilder.DropTable(
                name: "RosterSnapshots");

            migrationBuilder.DropTable(
                name: "RoundEvents");

            migrationBuilder.DropIndex(
                name: "IX_Matches_SeriesId",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "MatchRounds");

            migrationBuilder.DropColumn(
                name: "SourcePayloadJson",
                table: "MatchRounds");

            migrationBuilder.DropColumn(
                name: "StatsSource",
                table: "MatchRounds");

            migrationBuilder.DropColumn(
                name: "AliveSeconds",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "Assists",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "DetailsJson",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "DistanceMeters",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "DownedSeconds",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "EngagedSeconds",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "ObjectivesDefused",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "ObjectivesDestroyed",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "ObjectivesPlanted",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "ObjectivesRepaired",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "ObjectivesReturned",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "ObjectivesSecured",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "SpawnCount",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "MapNumber",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "SeriesTeam1Faction",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "SourceMatchId",
                table: "Matches");
        }
    }
}
