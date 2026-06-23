using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class ExtendSupplierJewelryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowsManufacturing",
                table: "Suppliers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "Balance",
                table: "Suppliers",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "Suppliers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Suppliers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Suppliers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "Suppliers",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContactName",
                table: "Suppliers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrencyType",
                table: "Suppliers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "CurrentCredit",
                table: "Suppliers",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "CurrentDebt",
                table: "Suppliers",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DefaultLaborCostPerGram",
                table: "Suppliers",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "DefaultPaymentType",
                table: "Suppliers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "District",
                table: "Suppliers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FireRate",
                table: "Suppliers",
                type: "decimal(9,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "IBAN",
                table: "Suppliers",
                type: "nvarchar(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Suppliers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "KaratTypes",
                table: "Suppliers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Suppliers",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentTermDays",
                table: "Suppliers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PricingType",
                table: "Suppliers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProductCategoriesWorkedWith",
                table: "Suppliers",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskLimit",
                table: "Suppliers",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SupplierCode",
                table: "Suppliers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SupplierType",
                table: "Suppliers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Suppliers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Whatsapp",
                table: "Suppliers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WorksOnConsignment",
                table: "Suppliers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_BranchId",
                table: "Suppliers",
                column: "BranchId");

            migrationBuilder.AddForeignKey(
                name: "FK_Suppliers_Branches_BranchId",
                table: "Suppliers",
                column: "BranchId",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Mevcut kayıtlar için: CompanyName = Name, SupplierCode = Id'nin ilk 8 karakteri, IsActive = true
            migrationBuilder.Sql(@"
                UPDATE Suppliers SET CompanyName = ISNULL(Name, ''), SupplierCode = UPPER(LEFT(REPLACE(CAST(Id AS nvarchar(36)), '-', ''), 8)), IsActive = 1
                WHERE CompanyName = '' OR SupplierCode = ''
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Suppliers_Branches_BranchId",
                table: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Suppliers_BranchId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "AllowsManufacturing",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "Balance",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "City",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "ContactName",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "CurrencyType",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "CurrentCredit",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "CurrentDebt",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "DefaultLaborCostPerGram",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "DefaultPaymentType",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "District",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "FireRate",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "IBAN",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "KaratTypes",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "PaymentTermDays",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "PricingType",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "ProductCategoriesWorkedWith",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "RiskLimit",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "SupplierCode",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "SupplierType",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "Whatsapp",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "WorksOnConsignment",
                table: "Suppliers");
        }
    }
}
