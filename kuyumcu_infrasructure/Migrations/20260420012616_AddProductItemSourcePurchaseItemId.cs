using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductItemSourcePurchaseItemId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourcePurchaseItemId",
                table: "ProductItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductItems_SourcePurchaseItemId",
                table: "ProductItems",
                column: "SourcePurchaseItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductItems_SourcePurchaseItemId",
                table: "ProductItems");

            migrationBuilder.DropColumn(
                name: "SourcePurchaseItemId",
                table: "ProductItems");
        }
    }
}
