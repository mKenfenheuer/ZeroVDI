using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConnectorId",
                table: "ProxmoxBackends",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Connectors",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegistrationToken = table.Column<string>(type: "TEXT", nullable: true),
                    RegistrationTokenUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuthTokenHash = table.Column<string>(type: "TEXT", nullable: true),
                    AllowScope = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRemoteAddress = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connectors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxBackends_ConnectorId",
                table: "ProxmoxBackends",
                column: "ConnectorId");

            migrationBuilder.CreateIndex(
                name: "IX_Connectors_AuthTokenHash",
                table: "Connectors",
                column: "AuthTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Connectors_Name",
                table: "Connectors",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ProxmoxBackends_Connectors_ConnectorId",
                table: "ProxmoxBackends",
                column: "ConnectorId",
                principalTable: "Connectors",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProxmoxBackends_Connectors_ConnectorId",
                table: "ProxmoxBackends");

            migrationBuilder.DropTable(
                name: "Connectors");

            migrationBuilder.DropIndex(
                name: "IX_ProxmoxBackends_ConnectorId",
                table: "ProxmoxBackends");

            migrationBuilder.DropColumn(
                name: "ConnectorId",
                table: "ProxmoxBackends");
        }
    }
}
