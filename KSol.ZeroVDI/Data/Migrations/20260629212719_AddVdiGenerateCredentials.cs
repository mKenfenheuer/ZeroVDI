using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVdiGenerateCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GenerateCredentials",
                table: "VdiPools",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenerateCredentials",
                table: "VdiPools");
        }
    }
}
