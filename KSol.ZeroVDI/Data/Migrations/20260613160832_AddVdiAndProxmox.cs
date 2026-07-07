using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KSol.ZeroVDI.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVdiAndProxmox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The RDPResources identity changes from the IP-as-id (ResourceIdentifier, the PK) to a
            // GUID (Id), the IP moves to its own column, and several VDI/Proxmox columns are added.
            // Because RDPResourceUserAuthorizations has an FK to the old PK, EF's automatic SQLite
            // table-rebuild interleaves the PK swap and the dependent-table rebuild and trips a
            // "foreign key mismatch". We instead rebuild RDPResources by hand in a controlled order
            // with FK enforcement disabled, preserving the existing row: the old ResourceIdentifier
            // becomes BOTH the new Id (keeping identity stable so existing authorizations and issued
            // .rdp files keep working) and the IpAddress. RdpOptions starts as a NULL JSON document
            // (the app treats a null owned-options value as defaults).
            migrationBuilder.CreateTable(
                name: "ProxmoxBackends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: true),
                    ApiTokenId = table.Column<string>(type: "TEXT", nullable: true),
                    ApiTokenSecret = table.Column<string>(type: "TEXT", nullable: true),
                    VerifyTls = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultRdpPort = table.Column<int>(type: "INTEGER", nullable: false),
                    IdleTimeoutHours = table.Column<int>(type: "INTEGER", nullable: false),
                    PauseAction = table.Column<int>(type: "INTEGER", nullable: false),
                    StartTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxBackends", x => x.Id);
                });

            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            migrationBuilder.Sql(@"
                CREATE TABLE ""RDPResources_new"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_RDPResources"" PRIMARY KEY,
                    ""Name"" TEXT NULL,
                    ""Description"" TEXT NULL,
                    ""IpAddress"" TEXT NULL,
                    ""Port"" INTEGER NOT NULL DEFAULT 3389,
                    ""Source"" INTEGER NOT NULL DEFAULT 0,
                    ""ProxmoxBackendId"" INTEGER NULL,
                    ""ProxmoxNode"" TEXT NULL,
                    ""ProxmoxVmId"" INTEGER NULL,
                    ""PowerState"" INTEGER NOT NULL DEFAULT 0,
                    ""LastActivityUtc"" TEXT NULL,
                    ""RdpOptions"" TEXT NULL,
                    ""ConfigJson"" TEXT NULL,
                    CONSTRAINT ""FK_RDPResources_ProxmoxBackends_ProxmoxBackendId"" FOREIGN KEY (""ProxmoxBackendId"") REFERENCES ""ProxmoxBackends"" (""Id"")
                );");

            migrationBuilder.Sql(@"
                INSERT INTO ""RDPResources_new"" (""Id"", ""Name"", ""Description"", ""IpAddress"", ""Port"", ""Source"", ""PowerState"")
                SELECT ""ResourceIdentifier"", ""Name"", ""Description"", ""ResourceIdentifier"", 3389, 0, 0
                FROM ""RDPResources"";");

            migrationBuilder.Sql(@"DROP TABLE ""RDPResources"";");
            migrationBuilder.Sql(@"ALTER TABLE ""RDPResources_new"" RENAME TO ""RDPResources"";");

            migrationBuilder.Sql(@"CREATE INDEX ""IX_RDPResources_ProxmoxBackendId"" ON ""RDPResources"" (""ProxmoxBackendId"");");

            // Rebuild the dependent table so its FK targets the new RDPResources.Id column (the old
            // FK referenced ResourceIdentifier, which no longer exists). RDPResourceId values are the
            // preserved ids, so existing authorizations continue to resolve.
            migrationBuilder.Sql(@"
                CREATE TABLE ""RDPResourceUserAuthorizations_new"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_RDPResourceUserAuthorizations"" PRIMARY KEY,
                    ""UserId"" TEXT NULL,
                    ""RDPResourceId"" TEXT NULL,
                    CONSTRAINT ""FK_RDPResourceUserAuthorizations_AspNetUsers_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""AspNetUsers"" (""Id""),
                    CONSTRAINT ""FK_RDPResourceUserAuthorizations_RDPResources_RDPResourceId"" FOREIGN KEY (""RDPResourceId"") REFERENCES ""RDPResources"" (""Id"")
                );");
            migrationBuilder.Sql(@"
                INSERT INTO ""RDPResourceUserAuthorizations_new"" (""Id"", ""UserId"", ""RDPResourceId"")
                SELECT ""Id"", ""UserId"", ""RDPResourceId"" FROM ""RDPResourceUserAuthorizations"";");
            migrationBuilder.Sql(@"DROP TABLE ""RDPResourceUserAuthorizations"";");
            migrationBuilder.Sql(@"ALTER TABLE ""RDPResourceUserAuthorizations_new"" RENAME TO ""RDPResourceUserAuthorizations"";");
            migrationBuilder.Sql(@"CREATE INDEX ""IX_RDPResourceUserAuthorizations_RDPResourceId"" ON ""RDPResourceUserAuthorizations"" (""RDPResourceId"");");
            migrationBuilder.Sql(@"CREATE INDEX ""IX_RDPResourceUserAuthorizations_UserId"" ON ""RDPResourceUserAuthorizations"" (""UserId"");");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse of Up: rebuild RDPResources back to the IP-as-id (ResourceIdentifier) PK,
            // carrying the preserved Id back into ResourceIdentifier, then drop the VDI/Proxmox
            // columns and the ProxmoxBackends table, and re-point the dependent FK.
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            migrationBuilder.Sql(@"
                CREATE TABLE ""RDPResources_old"" (
                    ""ResourceIdentifier"" TEXT NOT NULL CONSTRAINT ""PK_RDPResources"" PRIMARY KEY,
                    ""Name"" TEXT NULL,
                    ""Description"" TEXT NULL
                );");
            migrationBuilder.Sql(@"
                INSERT INTO ""RDPResources_old"" (""ResourceIdentifier"", ""Name"", ""Description"")
                SELECT ""Id"", ""Name"", ""Description"" FROM ""RDPResources"";");
            migrationBuilder.Sql(@"DROP TABLE ""RDPResources"";");
            migrationBuilder.Sql(@"ALTER TABLE ""RDPResources_old"" RENAME TO ""RDPResources"";");

            migrationBuilder.Sql(@"
                CREATE TABLE ""RDPResourceUserAuthorizations_old"" (
                    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_RDPResourceUserAuthorizations"" PRIMARY KEY,
                    ""UserId"" TEXT NULL,
                    ""RDPResourceId"" TEXT NULL,
                    CONSTRAINT ""FK_RDPResourceUserAuthorizations_AspNetUsers_UserId"" FOREIGN KEY (""UserId"") REFERENCES ""AspNetUsers"" (""Id""),
                    CONSTRAINT ""FK_RDPResourceUserAuthorizations_RDPResources_RDPResourceId"" FOREIGN KEY (""RDPResourceId"") REFERENCES ""RDPResources"" (""ResourceIdentifier"")
                );");
            migrationBuilder.Sql(@"
                INSERT INTO ""RDPResourceUserAuthorizations_old"" (""Id"", ""UserId"", ""RDPResourceId"")
                SELECT ""Id"", ""UserId"", ""RDPResourceId"" FROM ""RDPResourceUserAuthorizations"";");
            migrationBuilder.Sql(@"DROP TABLE ""RDPResourceUserAuthorizations"";");
            migrationBuilder.Sql(@"ALTER TABLE ""RDPResourceUserAuthorizations_old"" RENAME TO ""RDPResourceUserAuthorizations"";");
            migrationBuilder.Sql(@"CREATE INDEX ""IX_RDPResourceUserAuthorizations_RDPResourceId"" ON ""RDPResourceUserAuthorizations"" (""RDPResourceId"");");
            migrationBuilder.Sql(@"CREATE INDEX ""IX_RDPResourceUserAuthorizations_UserId"" ON ""RDPResourceUserAuthorizations"" (""UserId"");");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);

            migrationBuilder.DropTable(name: "ProxmoxBackends");
        }
    }
}
