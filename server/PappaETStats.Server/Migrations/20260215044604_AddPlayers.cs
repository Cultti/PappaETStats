using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PappaETStats.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayers : Migration
    {
        private const double DefaultMu = 25.0;
        private const double DefaultSigma = 25.0 / 3.0;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    Guid = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Mu = table.Column<double>(type: "REAL", nullable: false),
                    Sigma = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.Guid);
                });

            // Backfill players from existing MatchPlayers rows (multiple rows may exist per player).
            // We store the initial rating values; later match completion can update these.
            if (ActiveProvider.Contains("Sqlite"))
            {
                migrationBuilder.Sql(@"
    INSERT INTO Players (""Guid"", Mu, Sigma)
    SELECT DISTINCT mp.""Guid"", 25.0, 8.333333333333334
    FROM MatchPlayers mp
    WHERE mp.""Guid"" IS NOT NULL AND mp.""Guid"" <> '';
 ");
            }
            else if (ActiveProvider.Contains("MySql") || ActiveProvider.Contains("MariaDb"))
            {
                migrationBuilder.Sql(@"
    INSERT INTO Players (`Guid`, Mu, Sigma)
    SELECT DISTINCT mp.`Guid`, 25.0, 8.333333333333334
    FROM MatchPlayers mp
    WHERE mp.`Guid` IS NOT NULL AND mp.`Guid` <> '';
 ");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Players");
        }
    }
}
