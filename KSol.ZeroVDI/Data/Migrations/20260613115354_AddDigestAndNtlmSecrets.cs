using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDigestAndNtlmSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DigestHA1",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DigestRealm",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NtHash",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigestHA1",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DigestRealm",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "NtHash",
                table: "AspNetUsers");
        }
    }
}
