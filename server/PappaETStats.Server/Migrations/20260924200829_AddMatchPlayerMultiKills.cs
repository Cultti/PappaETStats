using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchPlayerMultiKills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MultiKills2",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MultiKills3",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MultiKills4",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MultiKills5",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MultiKills6",
                table: "MatchPlayers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MultiKills2",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "MultiKills3",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "MultiKills4",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "MultiKills5",
                table: "MatchPlayers");

            migrationBuilder.DropColumn(
                name: "MultiKills6",
                table: "MatchPlayers");
        }
    }
}
