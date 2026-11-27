using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MfaAndPasswordFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MfaEnabled",
                schema: "identity",
                table: "User",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "MfaLastTimeStep",
                schema: "identity",
                table: "User",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MfaSecretProtected",
                schema: "identity",
                table: "User",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                schema: "identity",
                table: "User",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PendingMfaSecretProtected",
                schema: "identity",
                table: "User",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryCodeHashes",
                schema: "identity",
                table: "User",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MfaEnabled",
                schema: "identity",
                table: "User");

            migrationBuilder.DropColumn(
                name: "MfaLastTimeStep",
                schema: "identity",
                table: "User");

            migrationBuilder.DropColumn(
                name: "MfaSecretProtected",
                schema: "identity",
                table: "User");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                schema: "identity",
                table: "User");

            migrationBuilder.DropColumn(
                name: "PendingMfaSecretProtected",
                schema: "identity",
                table: "User");

            migrationBuilder.DropColumn(
                name: "RecoveryCodeHashes",
                schema: "identity",
                table: "User");
        }
    }
}
