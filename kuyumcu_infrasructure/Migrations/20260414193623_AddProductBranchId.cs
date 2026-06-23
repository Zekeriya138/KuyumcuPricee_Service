using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductBranchId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_TenantId_ProductCode",
                table: "Products");

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Products",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE p SET p.BranchId = x.BranchId
FROM Products p
INNER JOIN (
  SELECT ProductId, MIN(BranchId) AS BranchId
  FROM ProductItems
  WHERE IsDeleted = 0
  GROUP BY ProductId
) x ON x.ProductId = p.Id
WHERE p.BranchId IS NULL AND p.IsDeleted = 0;

UPDATE p SET p.BranchId = x.BranchId
FROM Products p
INNER JOIN (
  SELECT ProductId, MIN(BranchId) AS BranchId
  FROM Stocks
  GROUP BY ProductId
) x ON x.ProductId = p.Id
WHERE p.BranchId IS NULL AND p.IsDeleted = 0;

UPDATE p SET p.BranchId = b.Id
FROM Products p
CROSS APPLY (
  SELECT TOP 1 b2.Id
  FROM Branches b2
  WHERE b2.TenantId = p.TenantId AND b2.IsDeleted = 0
  ORDER BY b2.CreatedAt
) b
WHERE p.BranchId IS NULL;
");

            migrationBuilder.Sql(@"
UPDATE Products
SET BranchId = (SELECT TOP 1 Id FROM Branches WHERE TenantId = Products.TenantId AND IsDeleted = 0 ORDER BY CreatedAt)
WHERE BranchId IS NULL;
");

            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "Products",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_BranchId",
                table: "Products",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_BranchId_ProductCode",
                table: "Products",
                columns: new[] { "TenantId", "BranchId", "ProductCode" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Branches_BranchId",
                table: "Products",
                column: "BranchId",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_Branches_BranchId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_TenantId_BranchId_ProductCode",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_BranchId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Products");

            migrationBuilder.CreateIndex(
                name: "IX_Products_TenantId_ProductCode",
                table: "Products",
                columns: new[] { "TenantId", "ProductCode" },
                unique: true);
        }
    }
}
