using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveVncSpiceProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyboardLayout",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "Protocol",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "DefaultSpicePort",
                table: "ProxmoxBackends");

            migrationBuilder.DropColumn(
                name: "DefaultVncPort",
                table: "ProxmoxBackends");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KeyboardLayout",
                table: "RDPResources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Protocol",
                table: "RDPResources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DefaultSpicePort",
                table: "ProxmoxBackends",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DefaultVncPort",
                table: "ProxmoxBackends",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
