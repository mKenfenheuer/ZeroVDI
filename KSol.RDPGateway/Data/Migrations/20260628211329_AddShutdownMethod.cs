using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.RDPGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShutdownMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProtectedWindowsPassword",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ShutdownMethod",
                table: "RDPResources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WindowsUser",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectedWindowsPassword",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "ShutdownMethod",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "WindowsUser",
                table: "RDPResources");
        }
    }
}
