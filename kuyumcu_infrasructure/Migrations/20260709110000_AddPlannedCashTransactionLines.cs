using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    public partial class AddPlannedCashTransactionLines : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlannedCashTransactionLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlannedCashTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TxType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    PaymentMethod = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CashTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannedCashTransactionLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannedCashTransactionLines_PlannedCashTransactions_PlannedCashTransactionId",
                        column: x => x.PlannedCashTransactionId,
                        principalTable: "PlannedCashTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlannedCashTransactionLines_PlannedCashTransactionId_CreatedAt",
                table: "PlannedCashTransactionLines",
                columns: new[] { "PlannedCashTransactionId", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PlannedCashTransactionLines");
        }
    }
}
