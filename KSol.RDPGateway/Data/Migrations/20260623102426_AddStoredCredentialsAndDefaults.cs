using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.RDPGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredCredentialsAndDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConnectionDefaults",
                table: "RDPResourceUserAuthorizations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedDomain",
                table: "RDPResourceUserAuthorizations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedPassword",
                table: "RDPResourceUserAuthorizations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedUsername",
                table: "RDPResourceUserAuthorizations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConnectionDefaults",
                table: "RDPResourceUserAuthorizations");

            migrationBuilder.DropColumn(
                name: "ProtectedDomain",
                table: "RDPResourceUserAuthorizations");

            migrationBuilder.DropColumn(
                name: "ProtectedPassword",
                table: "RDPResourceUserAuthorizations");

            migrationBuilder.DropColumn(
                name: "ProtectedUsername",
                table: "RDPResourceUserAuthorizations");
        }
    }
}
