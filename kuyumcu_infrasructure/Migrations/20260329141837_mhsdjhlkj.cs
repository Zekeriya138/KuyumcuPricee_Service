using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class mhsdjhlkj : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerBalances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BalanceTL = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceUSD = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceEUR = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceHAS = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerBalances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerBalances_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupCode = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ItemName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ItemType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Gram = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Ayar = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Milyem = table.Column<decimal>(type: "decimal(9,4)", nullable: true),
                    HasEquivalent = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    UnitPriceTl = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    TotalPriceTl = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    TxDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CariDurum = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    RefType = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    RefId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerTransactions_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CustomerTransactions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerBalances_CustomerId",
                table: "CustomerBalances",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerBalances_TenantId_CustomerId",
                table: "CustomerBalances",
                columns: new[] { "TenantId", "CustomerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTransactions_BranchId",
                table: "CustomerTransactions",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTransactions_CustomerId",
                table: "CustomerTransactions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTransactions_TenantId_CustomerId_GroupCode_ItemName_ItemType_CreatedAt",
                table: "CustomerTransactions",
                columns: new[] { "TenantId", "CustomerId", "GroupCode", "ItemName", "ItemType", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerBalances");

            migrationBuilder.DropTable(
                name: "CustomerTransactions");
        }
    }
}
