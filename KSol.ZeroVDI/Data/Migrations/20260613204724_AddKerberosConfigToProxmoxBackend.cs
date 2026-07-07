using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKerberosConfigToProxmoxBackend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KdcHost",
                table: "ProxmoxBackends",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KerberosRealm",
                table: "ProxmoxBackends",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KdcHost",
                table: "ProxmoxBackends");

            migrationBuilder.DropColumn(
                name: "KerberosRealm",
                table: "ProxmoxBackends");
        }
    }
}
