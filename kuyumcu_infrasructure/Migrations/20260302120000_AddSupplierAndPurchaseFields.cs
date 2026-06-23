using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    public partial class AddSupplierAndPurchaseFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TaxNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    TaxOffice = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table => { table.PrimaryKey("PK_Suppliers", x => x.Id); });

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Purchases') AND name = 'SupplierId')
                    ALTER TABLE Purchases ADD SupplierId uniqueidentifier NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Purchases') AND name = 'PurchaseType')
                    ALTER TABLE Purchases ADD PurchaseType int NOT NULL DEFAULT 1;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Purchases') AND name = 'PaymentMethod')
                    ALTER TABLE Purchases ADD PaymentMethod int NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Purchases') AND name = 'TotalHas')
                    ALTER TABLE Purchases ADD TotalHas decimal(18,4) NULL;
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Suppliers");
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Purchases') AND name = 'SupplierId')
                    ALTER TABLE Purchases DROP COLUMN SupplierId;
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Purchases') AND name = 'PurchaseType')
                    ALTER TABLE Purchases DROP COLUMN PurchaseType;
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Purchases') AND name = 'PaymentMethod')
                    ALTER TABLE Purchases DROP COLUMN PaymentMethod;
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Purchases') AND name = 'TotalHas')
                    ALTER TABLE Purchases DROP COLUMN TotalHas;
            ");
        }
    }
}
