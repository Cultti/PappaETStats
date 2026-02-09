using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchDemoMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DemoFileName",
                table: "Matches",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DemoUploadedAtUtc",
                table: "Matches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DemoZipFileName",
                table: "Matches",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DemoZippedAtUtc",
                table: "Matches",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DemoFileName",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "DemoUploadedAtUtc",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "DemoZipFileName",
                table: "Matches");

            migrationBuilder.DropColumn(
                name: "DemoZippedAtUtc",
                table: "Matches");
        }
    }
}
