using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseItemIscilikHasColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BirimIscilikHas",
                table: "PurchaseItems",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OdenecekToplamHas",
                table: "PurchaseItems",
                type: "decimal(18,6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BirimIscilikHas",
                table: "PurchaseItems");

            migrationBuilder.DropColumn(
                name: "OdenecekToplamHas",
                table: "PurchaseItems");
        }
    }
}
