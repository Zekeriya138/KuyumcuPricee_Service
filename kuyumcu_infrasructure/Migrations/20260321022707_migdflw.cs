using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class migdflw : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GoldSource",
                table: "PurchasePayments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ScrapLedgers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Karat = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DeltaWeightGram = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    DeltaPureGoldGram = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    GoldPricePerGram = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    AmountTl = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PurchaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapLedgers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScrapLedgers_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScrapStocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Karat = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    WeightGram = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PureGoldGram = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapStocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScrapStocks_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapLedgers_BranchId",
                table: "ScrapLedgers",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapStocks_BranchId",
                table: "ScrapStocks",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapStocks_TenantId_BranchId_Karat",
                table: "ScrapStocks",
                columns: new[] { "TenantId", "BranchId", "Karat" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScrapLedgers");

            migrationBuilder.DropTable(
                name: "ScrapStocks");

            migrationBuilder.DropColumn(
                name: "GoldSource",
                table: "PurchasePayments");
        }
    }
}
