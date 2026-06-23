using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    public partial class AddRateSettingsBranchIsolation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "RateDisplaySettings",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(@"
;WITH FirstBranch AS (
    SELECT t.Id AS TenantId,
           (
               SELECT TOP 1 b2.Id
               FROM Branches b2
               WHERE b2.TenantId = t.Id
               ORDER BY b2.CreatedAt
           ) AS BranchId
    FROM Tenants t
)
UPDATE r
SET r.BranchId = fb.BranchId
FROM RateDisplaySettings r
INNER JOIN FirstBranch fb ON fb.TenantId = r.TenantId
WHERE r.BranchId IS NULL
  AND fb.BranchId IS NOT NULL;
");

            migrationBuilder.Sql(@"
UPDATE r
SET r.BranchId = b.Id
FROM RateDisplaySettings r
CROSS APPLY (
    SELECT TOP 1 b2.Id
    FROM Branches b2
    WHERE b2.TenantId = r.TenantId
    ORDER BY b2.CreatedAt
) b
WHERE r.BranchId IS NULL;
");

            migrationBuilder.AlterColumn<Guid>(
                name: "BranchId",
                table: "RateDisplaySettings",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_RateDisplaySettings_TenantId_Code",
                table: "RateDisplaySettings");

            migrationBuilder.CreateIndex(
                name: "IX_RateDisplaySettings_BranchId",
                table: "RateDisplaySettings",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_RateDisplaySettings_TenantId_BranchId_Code",
                table: "RateDisplaySettings",
                columns: new[] { "TenantId", "BranchId", "Code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RateDisplaySettings_Branches_BranchId",
                table: "RateDisplaySettings",
                column: "BranchId",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RateDisplaySettings_Branches_BranchId",
                table: "RateDisplaySettings");

            migrationBuilder.DropIndex(
                name: "IX_RateDisplaySettings_BranchId",
                table: "RateDisplaySettings");

            migrationBuilder.DropIndex(
                name: "IX_RateDisplaySettings_TenantId_BranchId_Code",
                table: "RateDisplaySettings");

            migrationBuilder.CreateIndex(
                name: "IX_RateDisplaySettings_TenantId_Code",
                table: "RateDisplaySettings",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "RateDisplaySettings");
        }
    }
}
