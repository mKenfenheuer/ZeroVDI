using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVdiInstanceLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CloneUpid",
                table: "VdiInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "VdiInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotesStamped",
                table: "VdiInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedUtc",
                table: "VdiInstances",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CloneUpid",
                table: "VdiInstances");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "VdiInstances");

            migrationBuilder.DropColumn(
                name: "NotesStamped",
                table: "VdiInstances");

            migrationBuilder.DropColumn(
                name: "UpdatedUtc",
                table: "VdiInstances");
        }
    }
}
