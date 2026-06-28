using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.RDPGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManualResourceLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultConnectionDefaults",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IpmiHost",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IpmiUser",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OsType",
                table: "RDPResources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedIpmiPassword",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedSshKey",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShutdownCommand",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshUser",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WakeMethod",
                table: "RDPResources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WolMacAddress",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultConnectionDefaults",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "IpmiHost",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "IpmiUser",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "OsType",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "ProtectedIpmiPassword",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "ProtectedSshKey",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "ShutdownCommand",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "SshUser",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "WakeMethod",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "WolMacAddress",
                table: "RDPResources");
        }
    }
}
