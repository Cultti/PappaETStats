using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerFormatRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MuLarge",
                table: "Players",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MuSmall",
                table: "Players",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SigmaLarge",
                table: "Players",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SigmaSmall",
                table: "Players",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MuLarge",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "MuSmall",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "SigmaLarge",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "SigmaSmall",
                table: "Players");
        }
    }
}
