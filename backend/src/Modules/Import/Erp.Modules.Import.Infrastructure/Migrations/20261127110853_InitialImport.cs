using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Import.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "import");

            migrationBuilder.CreateTable(
                name: "ImportJob",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Format = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpeningDate = table.Column<DateOnly>(type: "date", nullable: true),
                    UploadedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    ErrorRows = table.Column<int>(type: "int", nullable: false),
                    Errors = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ErrorsTruncated = table.Column<bool>(type: "bit", nullable: false),
                    ValidatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CommitStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CommittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CommittedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CommittedRows = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportJob", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportJobFile",
                schema: "import",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    ModifiedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportJobFile", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportJobFile_ImportJob_Id",
                        column: x => x.Id,
                        principalSchema: "import",
                        principalTable: "ImportJob",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportJob_TenantId",
                schema: "import",
                table: "ImportJob",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportJob_TenantId_CompanyId_UploadedAtUtc",
                schema: "import",
                table: "ImportJob",
                columns: new[] { "TenantId", "CompanyId", "UploadedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobFile_TenantId",
                schema: "import",
                table: "ImportJobFile",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ProcessedAtUtc_OccurredAtUtc",
                schema: "import",
                table: "OutboxMessage",
                columns: new[] { "ProcessedAtUtc", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportJobFile",
                schema: "import");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "import");

            migrationBuilder.DropTable(
                name: "ImportJob",
                schema: "import");
        }
    }
}
