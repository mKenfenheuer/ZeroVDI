using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostCertPinning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HostCertFingerprint",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HostCertPinnedUtc",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HostCertSubject",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HostCertFingerprint",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "HostCertPinnedUtc",
                table: "RDPResources");

            migrationBuilder.DropColumn(
                name: "HostCertSubject",
                table: "RDPResources");
        }
    }
}
