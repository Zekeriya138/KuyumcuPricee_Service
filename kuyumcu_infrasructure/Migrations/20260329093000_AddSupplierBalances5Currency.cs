using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierBalances5Currency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierBalances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BalanceTL = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceUSD = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceEUR = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceHAS = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceGUMUS = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBalances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierBalances_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalances_TenantId_SupplierId",
                table: "SupplierBalances",
                columns: new[] { "TenantId", "SupplierId" },
                unique: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitCode",
                table: "PurchasePayments",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitAmount",
                table: "PurchasePayments",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SilverWeight",
                table: "PurchasePayments",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.Sql(@"
                INSERT INTO SupplierBalances (Id, TenantId, SupplierId, BalanceTL, BalanceUSD, BalanceEUR, BalanceHAS, BalanceGUMUS, UpdatedAt, IsDeleted, CreatedAt)
                SELECT NEWID(), s.TenantId, s.Id, ISNULL(s.Balance, 0), 0, 0, 0, 0, GETUTCDATE(), 0, GETUTCDATE()
                FROM Suppliers s
                WHERE s.IsDeleted = 0
                  AND NOT EXISTS (
                    SELECT 1 FROM SupplierBalances sb
                    WHERE sb.TenantId = s.TenantId AND sb.SupplierId = s.Id
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnitCode",
                table: "PurchasePayments");

            migrationBuilder.DropColumn(
                name: "UnitAmount",
                table: "PurchasePayments");

            migrationBuilder.DropColumn(
                name: "SilverWeight",
                table: "PurchasePayments");

            migrationBuilder.DropTable(
                name: "SupplierBalances");
        }
    }
}
