using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceKeyboardLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "KeyboardLayout",
                table: "RDPResources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyboardLayout",
                table: "RDPResources");
        }
    }
}
