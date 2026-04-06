using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddGuidIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MatchPlayers_Guid",
                table: "MatchPlayers",
                column: "Guid");

            migrationBuilder.CreateIndex(
                name: "IX_MatchObituaries_TargetGuid",
                table: "MatchObituaries",
                column: "TargetGuid");

            migrationBuilder.CreateIndex(
                name: "IX_MatchObituaries_TargetGuid_AttackerGuid",
                table: "MatchObituaries",
                columns: new[] { "TargetGuid", "AttackerGuid" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MatchPlayers_Guid",
                table: "MatchPlayers");

            migrationBuilder.DropIndex(
                name: "IX_MatchObituaries_TargetGuid",
                table: "MatchObituaries");

            migrationBuilder.DropIndex(
                name: "IX_MatchObituaries_TargetGuid_AttackerGuid",
                table: "MatchObituaries");
        }
    }
}
