using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoMoveToVoice",
                table: "Players",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordId",
                table: "Players",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlayerRegistrationTokens",
                columns: table => new
                {
                    Token = table.Column<Guid>(type: "char(36)", nullable: false),
                    DiscordId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    DiscordUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UsedByEtGuid = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerRegistrationTokens", x => x.Token);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Players_DiscordId",
                table: "Players",
                column: "DiscordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRegistrationTokens_DiscordId",
                table: "PlayerRegistrationTokens",
                column: "DiscordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerRegistrationTokens");

            migrationBuilder.DropIndex(
                name: "IX_Players_DiscordId",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "AutoMoveToVoice",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "DiscordId",
                table: "Players");
        }
    }
}
