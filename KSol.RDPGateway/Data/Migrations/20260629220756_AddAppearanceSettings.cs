using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.RDPGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppearanceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppearanceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ForceTheme = table.Column<bool>(type: "INTEGER", nullable: false),
                    ThemeSource = table.Column<int>(type: "INTEGER", nullable: false),
                    PresetTheme = table.Column<string>(type: "TEXT", nullable: false),
                    CustomColorHex = table.Column<string>(type: "TEXT", nullable: false),
                    ForceMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    LogoPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppearanceSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppearanceSettings");
        }
    }
}
