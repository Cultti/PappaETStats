using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchWinner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Winner",
                table: "Matches",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Winner",
                table: "Matches");
        }
    }
}
