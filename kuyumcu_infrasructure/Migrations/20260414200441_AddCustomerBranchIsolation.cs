using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerBranchIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Customers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE c
SET c.BranchId = x.BranchId
FROM Customers c
INNER JOIN (
    SELECT CustomerId, MIN(BranchId) AS BranchId
    FROM CustomerTransactions
    WHERE BranchId IS NOT NULL AND IsDeleted = 0
    GROUP BY CustomerId
) x ON x.CustomerId = c.Id
WHERE c.BranchId IS NULL;

UPDATE c
SET c.BranchId = x.BranchId
FROM Customers c
INNER JOIN (
    SELECT CustomerId, MIN(BranchId) AS BranchId
    FROM Sales
    WHERE CustomerId IS NOT NULL AND IsDeleted = 0
    GROUP BY CustomerId
) x ON x.CustomerId = c.Id
WHERE c.BranchId IS NULL;

UPDATE c
SET c.BranchId = x.BranchId
FROM Customers c
INNER JOIN (
    SELECT CustomerId, MIN(BranchId) AS BranchId
    FROM Purchases
    WHERE CustomerId IS NOT NULL
    GROUP BY CustomerId
) x ON x.CustomerId = c.Id
WHERE c.BranchId IS NULL;

UPDATE c
SET c.BranchId = b.Id
FROM Customers c
CROSS APPLY (
    SELECT TOP 1 b2.Id
    FROM Branches b2
    WHERE b2.TenantId = c.TenantId
    ORDER BY b2.CreatedAt
) b
WHERE c.BranchId IS NULL;

UPDATE ct
SET ct.BranchId = c.BranchId
FROM CustomerTransactions ct
INNER JOIN Customers c ON c.Id = ct.CustomerId
WHERE ct.BranchId IS NULL;

UPDATE s
SET s.BranchId = b.Id
FROM Suppliers s
CROSS APPLY (
    SELECT TOP 1 b2.Id
    FROM Branches b2
    WHERE b2.TenantId = s.TenantId
    ORDER BY b2.CreatedAt
) b
WHERE s.BranchId IS NULL;

UPDATE st
SET st.BranchId = s.BranchId
FROM SupplierTransactions st
INNER JOIN Suppliers s ON s.Id = st.SupplierId
WHERE st.BranchId IS NULL;
");

            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "Customers",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Customers_BranchId",
                table: "Customers",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_BranchId_CreatedAt",
                table: "Customers",
                columns: new[] { "TenantId", "BranchId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_Branches_BranchId",
                table: "Customers",
                column: "BranchId",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Branches_BranchId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_BranchId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_TenantId_BranchId_CreatedAt",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Customers");
        }
    }
}
