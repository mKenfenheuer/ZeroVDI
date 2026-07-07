using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioSha256",
                table: "Recordings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CameraSha256",
                table: "Recordings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChainHash",
                table: "Recordings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DesktopSha256",
                table: "Recordings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Encrypted",
                table: "Recordings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MicSha256",
                table: "Recordings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioSha256",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "CameraSha256",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "ChainHash",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "DesktopSha256",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "Encrypted",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "MicSha256",
                table: "Recordings");
        }
    }
}
