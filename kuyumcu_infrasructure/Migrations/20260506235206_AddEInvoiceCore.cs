using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEInvoiceCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Users', 'Phone') IS NULL
    ALTER TABLE [Users] ADD [Phone] nvarchar(max) NULL;
");

            migrationBuilder.CreateTable(
                name: "EInvoiceDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Scenario = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    GrandTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IntegratorDocumentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Uuid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Ettn = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RawLastResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EInvoiceDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EInvoiceDocuments_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EInvoiceDocuments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EInvoiceDocuments_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EInvoiceOutboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EInvoiceOutboxes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EInvoiceProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TaxNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TaxOffice = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SenderLabel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IntegratorCompanyCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IntegratorUsername = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IntegratorSecretRef = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DefaultInvoicePrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DefaultArchivePrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EInvoiceProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EInvoiceProfiles_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EInvoiceWebhookLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Signature = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EventId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IntegratorDocumentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsVerified = table.Column<bool>(type: "bit", nullable: false),
                    IsProcessed = table.Column<bool>(type: "bit", nullable: false),
                    ProcessError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EInvoiceWebhookLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceDocuments_BranchId",
                table: "EInvoiceDocuments",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceDocuments_CustomerId",
                table: "EInvoiceDocuments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceDocuments_InvoiceId",
                table: "EInvoiceDocuments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceDocuments_TenantId_BranchId_Status_CreatedAt",
                table: "EInvoiceDocuments",
                columns: new[] { "TenantId", "BranchId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceDocuments_TenantId_InvoiceId",
                table: "EInvoiceDocuments",
                columns: new[] { "TenantId", "InvoiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceOutboxes_TenantId_DocumentId_Operation_Status",
                table: "EInvoiceOutboxes",
                columns: new[] { "TenantId", "DocumentId", "Operation", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceOutboxes_TenantId_Status_NextAttemptAt",
                table: "EInvoiceOutboxes",
                columns: new[] { "TenantId", "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceProfiles_BranchId",
                table: "EInvoiceProfiles",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceProfiles_TenantId_BranchId",
                table: "EInvoiceProfiles",
                columns: new[] { "TenantId", "BranchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceWebhookLogs_TenantId_BranchId_ReceivedAt",
                table: "EInvoiceWebhookLogs",
                columns: new[] { "TenantId", "BranchId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EInvoiceWebhookLogs_TenantId_ProviderCode_EventId",
                table: "EInvoiceWebhookLogs",
                columns: new[] { "TenantId", "ProviderCode", "EventId" },
                unique: true,
                filter: "[EventId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EInvoiceDocuments");

            migrationBuilder.DropTable(
                name: "EInvoiceOutboxes");

            migrationBuilder.DropTable(
                name: "EInvoiceProfiles");

            migrationBuilder.DropTable(
                name: "EInvoiceWebhookLogs");

            migrationBuilder.Sql(@"
IF COL_LENGTH('Users', 'Phone') IS NOT NULL
    ALTER TABLE [Users] DROP COLUMN [Phone];
");
        }
    }
}
