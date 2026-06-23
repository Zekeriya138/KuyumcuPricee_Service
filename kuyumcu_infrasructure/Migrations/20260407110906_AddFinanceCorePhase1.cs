using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFinanceCorePhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CashAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CurrentBalance = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashAccounts_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DayEndReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessDate = table.Column<DateTime>(type: "date", nullable: false),
                    OpeningTl = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OpeningUsd = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OpeningEur = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OpeningHas = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ClosingTl = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ClosingUsd = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ClosingEur = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ClosingHas = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TotalIncomeTl = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TotalExpenseTl = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PdfPath = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DayEndReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DayEndReports_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TxType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceModule = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TxDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RefType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RefId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashTransactions_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashTransactions_CashAccounts_CashAccountId",
                        column: x => x.CashAccountId,
                        principalTable: "CashAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashAccounts_BranchId",
                table: "CashAccounts",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_CashAccounts_TenantId_BranchId_AccountType_Currency_Name",
                table: "CashAccounts",
                columns: new[] { "TenantId", "BranchId", "AccountType", "Currency", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_BranchId",
                table: "CashTransactions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_CashAccountId",
                table: "CashTransactions",
                column: "CashAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_TenantId_BranchId_TxDate_CreatedAt",
                table: "CashTransactions",
                columns: new[] { "TenantId", "BranchId", "TxDate", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_TenantId_RefType_RefId",
                table: "CashTransactions",
                columns: new[] { "TenantId", "RefType", "RefId" });

            migrationBuilder.CreateIndex(
                name: "IX_DayEndReports_BranchId",
                table: "DayEndReports",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_DayEndReports_TenantId_BranchId_BusinessDate",
                table: "DayEndReports",
                columns: new[] { "TenantId", "BranchId", "BusinessDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashTransactions");

            migrationBuilder.DropTable(
                name: "DayEndReports");

            migrationBuilder.DropTable(
                name: "CashAccounts");
        }
    }
}
