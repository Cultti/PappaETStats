using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddLastReadyUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LastReadyUps",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    PlayerGuid = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    ReadyAtUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    CountdownAtUnix = table.Column<long>(type: "INTEGER", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MapName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LastReadyUps", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LastReadyUps_PlayerGuid",
                table: "LastReadyUps",
                column: "PlayerGuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LastReadyUps");
        }
    }
}
