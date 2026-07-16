using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleItemDeliveredQuantity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DeliveredQuantity",
                table: "SaleItems",
                type: "decimal(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveredQuantity",
                table: "SaleItems");
        }
    }
}
