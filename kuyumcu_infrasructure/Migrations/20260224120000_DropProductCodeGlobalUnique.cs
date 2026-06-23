using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <summary>ProductCode artık sadece (TenantId, ProductCode) ile benzersiz; global IX_Products_ProductCode kaldırıldı.</summary>
    public partial class DropProductCodeGlobalUnique : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Products_ProductCode", table: "Products");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(name: "IX_Products_ProductCode", table: "Products", column: "ProductCode", unique: true);
        }
    }
}
