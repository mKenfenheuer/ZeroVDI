using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.RDPGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RDPResources",
                columns: table => new
                {
                    ResourceIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RDPResources", x => x.ResourceIdentifier);
                });

            migrationBuilder.CreateTable(
                name: "RDPResourceUserAuthorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    RDPResourceId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RDPResourceUserAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RDPResourceUserAuthorizations_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RDPResourceUserAuthorizations_RDPResources_RDPResourceId",
                        column: x => x.RDPResourceId,
                        principalTable: "RDPResources",
                        principalColumn: "ResourceIdentifier");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RDPResourceUserAuthorizations_RDPResourceId",
                table: "RDPResourceUserAuthorizations",
                column: "RDPResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_RDPResourceUserAuthorizations_UserId",
                table: "RDPResourceUserAuthorizations",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RDPResourceUserAuthorizations");

            migrationBuilder.DropTable(
                name: "RDPResources");
        }
    }
}
