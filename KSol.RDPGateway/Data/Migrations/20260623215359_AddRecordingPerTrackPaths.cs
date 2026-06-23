using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.RDPGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingPerTrackPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioFilePath",
                table: "Recordings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MicFilePath",
                table: "Recordings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioFilePath",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "MicFilePath",
                table: "Recordings");
        }
    }
}
