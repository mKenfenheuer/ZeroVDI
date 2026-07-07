using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceForcedConnector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ForcedConnectorId",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RDPResources_ForcedConnectorId",
                table: "RDPResources",
                column: "ForcedConnectorId");

            migrationBuilder.AddForeignKey(
                name: "FK_RDPResources_Connectors_ForcedConnectorId",
                table: "RDPResources",
                column: "ForcedConnectorId",
                principalTable: "Connectors",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RDPResources_Connectors_ForcedConnectorId",
                table: "RDPResources");

            migrationBuilder.DropIndex(
                name: "IX_RDPResources_ForcedConnectorId",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "ForcedConnectorId",
                table: "RDPResources");
        }
    }
}
