using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.RDPGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDarkModeLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogoPathDark",
                table: "AppearanceSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoPathDark",
                table: "AppearanceSettings");
        }
    }
}
