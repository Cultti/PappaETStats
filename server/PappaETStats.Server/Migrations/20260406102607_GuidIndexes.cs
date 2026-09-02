using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class GuidIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TargetGuid",
                table: "MatchObituaries",
                type: "varchar(64)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AttackerGuid",
                table: "MatchObituaries",
                type: "varchar(64)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldNullable: true);

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
                name: "IX_MatchObituaries_TargetGuid",
                table: "MatchObituaries");

            migrationBuilder.DropIndex(
                name: "IX_MatchObituaries_TargetGuid_AttackerGuid",
                table: "MatchObituaries");

            migrationBuilder.AlterColumn<string>(
                name: "TargetGuid",
                table: "MatchObituaries",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AttackerGuid",
                table: "MatchObituaries",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldNullable: true);
        }
    }
}
