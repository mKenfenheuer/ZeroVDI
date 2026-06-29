using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.RDPGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVdiPools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VdiInstanceId",
                table: "RDPResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VdiPools",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProxmoxBackendId = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateVmId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CloneMode = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetNode = table.Column<string>(type: "TEXT", nullable: true),
                    TargetStorage = table.Column<string>(type: "TEXT", nullable: true),
                    VmidRangeStart = table.Column<int>(type: "INTEGER", nullable: true),
                    VmidRangeEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    NamePattern = table.Column<string>(type: "TEXT", nullable: false),
                    MaxSize = table.Column<int>(type: "INTEGER", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    OsType = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentityMode = table.Column<int>(type: "INTEGER", nullable: false),
                    HostnamePattern = table.Column<string>(type: "TEXT", nullable: true),
                    CiUser = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedCiPassword = table.Column<string>(type: "TEXT", nullable: true),
                    CiSshKeys = table.Column<string>(type: "TEXT", nullable: true),
                    DomainName = table.Column<string>(type: "TEXT", nullable: true),
                    DomainOu = table.Column<string>(type: "TEXT", nullable: true),
                    DomainJoinUser = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedDomainJoinPassword = table.Column<string>(type: "TEXT", nullable: true),
                    FloatingReset = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectionDefaults = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VdiPools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VdiPools_ProxmoxBackends_ProxmoxBackendId",
                        column: x => x.ProxmoxBackendId,
                        principalTable: "ProxmoxBackends",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VdiInstances",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PoolId = table.Column<string>(type: "TEXT", nullable: false),
                    RDPResourceId = table.Column<string>(type: "TEXT", nullable: true),
                    ProxmoxNode = table.Column<string>(type: "TEXT", nullable: true),
                    ProxmoxVmId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLeasedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VdiInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VdiInstances_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VdiInstances_RDPResources_RDPResourceId",
                        column: x => x.RDPResourceId,
                        principalTable: "RDPResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VdiInstances_VdiPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "VdiPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VdiPoolAssignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PoolId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    GroupId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VdiPoolAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VdiPoolAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VdiPoolAssignments_UserGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VdiPoolAssignments_VdiPools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "VdiPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VdiInstances_OwnerUserId",
                table: "VdiInstances",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VdiInstances_PoolId_OwnerUserId",
                table: "VdiInstances",
                columns: new[] { "PoolId", "OwnerUserId" },
                unique: true,
                filter: "\"OwnerUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VdiInstances_RDPResourceId",
                table: "VdiInstances",
                column: "RDPResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_VdiPoolAssignments_GroupId",
                table: "VdiPoolAssignments",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_VdiPoolAssignments_PoolId_GroupId",
                table: "VdiPoolAssignments",
                columns: new[] { "PoolId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VdiPoolAssignments_PoolId_UserId",
                table: "VdiPoolAssignments",
                columns: new[] { "PoolId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VdiPoolAssignments_UserId",
                table: "VdiPoolAssignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_VdiPools_Name",
                table: "VdiPools",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VdiPools_ProxmoxBackendId",
                table: "VdiPools",
                column: "ProxmoxBackendId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VdiInstances");

            migrationBuilder.DropTable(
                name: "VdiPoolAssignments");

            migrationBuilder.DropTable(
                name: "VdiPools");

            migrationBuilder.DropColumn(
                name: "VdiInstanceId",
                table: "RDPResources");
        }
    }
}
