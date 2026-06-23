using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class mihhdaj : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TxType = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    SourceUnit = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceAmount = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TargetUnit = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TargetAmount = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    IsConverted = table.Column<bool>(type: "bit", nullable: false),
                    SourceUnitTlRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TargetUnitTlRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TxDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierTransactions_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SupplierTransactions_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierTransactions_BranchId",
                table: "SupplierTransactions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierTransactions_SupplierId",
                table: "SupplierTransactions",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierTransactions_TenantId_SupplierId_TxDate_CreatedAt",
                table: "SupplierTransactions",
                columns: new[] { "TenantId", "SupplierId", "TxDate", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierTransactions");
        }
    }
}
