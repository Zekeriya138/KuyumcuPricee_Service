using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    public partial class AddTransactionReversal : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "CustomerTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "CustomerTransactions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "CustomerTransactions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalLogId",
                table: "CustomerTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "SupplierTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefType",
                table: "SupplierTransactions",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RefId",
                table: "SupplierTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "SupplierTransactions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "SupplierTransactions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalLogId",
                table: "SupplierTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BatchId",
                table: "CashTransactions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReversed",
                table: "CashTransactions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "CashTransactions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TransactionReversalLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PartyType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PartyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OriginalTxDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OriginalPerformedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReversedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReversedByUserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ReversedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DisplayGrup = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayKalem = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayDeger = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayCariDurum = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayAciklama = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionReversalLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTransactions_TenantId_BatchId",
                table: "CustomerTransactions",
                columns: new[] { "TenantId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierTransactions_TenantId_BatchId",
                table: "SupplierTransactions",
                columns: new[] { "TenantId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_TenantId_BatchId",
                table: "CashTransactions",
                columns: new[] { "TenantId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionReversalLogs_TenantId_BatchId",
                table: "TransactionReversalLogs",
                columns: new[] { "TenantId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionReversalLogs_TenantId_PartyType_PartyId_ReversedAt",
                table: "TransactionReversalLogs",
                columns: new[] { "TenantId", "PartyType", "PartyId", "ReversedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TransactionReversalLogs");
            migrationBuilder.DropColumn(name: "BatchId", table: "CashTransactions");
            migrationBuilder.DropColumn(name: "IsReversed", table: "CashTransactions");
            migrationBuilder.DropColumn(name: "ReversedAt", table: "CashTransactions");
            migrationBuilder.DropColumn(name: "BatchId", table: "SupplierTransactions");
            migrationBuilder.DropColumn(name: "RefType", table: "SupplierTransactions");
            migrationBuilder.DropColumn(name: "RefId", table: "SupplierTransactions");
            migrationBuilder.DropColumn(name: "IsReversed", table: "SupplierTransactions");
            migrationBuilder.DropColumn(name: "ReversedAt", table: "SupplierTransactions");
            migrationBuilder.DropColumn(name: "ReversalLogId", table: "SupplierTransactions");
            migrationBuilder.DropColumn(name: "BatchId", table: "CustomerTransactions");
            migrationBuilder.DropColumn(name: "IsReversed", table: "CustomerTransactions");
            migrationBuilder.DropColumn(name: "ReversedAt", table: "CustomerTransactions");
            migrationBuilder.DropColumn(name: "ReversalLogId", table: "CustomerTransactions");
        }
    }
}
