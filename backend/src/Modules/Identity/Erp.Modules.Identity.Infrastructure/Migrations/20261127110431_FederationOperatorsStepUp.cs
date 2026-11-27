using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FederationOperatorsStepUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthenticationMethod",
                schema: "identity",
                table: "UserSession",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "pwd");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastMfaAtUtc",
                schema: "identity",
                table: "UserSession",
                type: "datetime2(3)",
                precision: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExternalIdentityProvider",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Authority = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ClientSecretName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Scopes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    EmailClaim = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequireVerifiedEmail = table.Column<bool>(type: "bit", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIdentityProvider", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAuditEntry",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    OperatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAuditEntry", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformOperator",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    MfaSecretProtected = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    MfaLastTimeStep = table.Column<long>(type: "bigint", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsBootstrap = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false),
                    LockoutEndUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastLoginAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformOperator", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentityProvider_TenantId",
                schema: "identity",
                table: "ExternalIdentityProvider",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentityProvider_TenantId_Key",
                schema: "identity",
                table: "ExternalIdentityProvider",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditEntry_AtUtc",
                schema: "identity",
                table: "PlatformAuditEntry",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformOperator_IsBootstrap",
                schema: "identity",
                table: "PlatformOperator",
                column: "IsBootstrap",
                unique: true,
                filter: "[IsBootstrap] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformOperator_NormalizedEmail",
                schema: "identity",
                table: "PlatformOperator",
                column: "NormalizedEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalIdentityProvider",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "PlatformAuditEntry",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "PlatformOperator",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "AuthenticationMethod",
                schema: "identity",
                table: "UserSession");

            migrationBuilder.DropColumn(
                name: "LastMfaAtUtc",
                schema: "identity",
                table: "UserSession");
        }
    }
}
