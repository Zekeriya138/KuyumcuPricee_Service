using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    public partial class AddDepoStokAyarAyarCariTip : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Customers') AND name = 'CariTip')
                    ALTER TABLE Customers ADD CariTip int NOT NULL DEFAULT 0;
            ");

            migrationBuilder.CreateTable(
                name: "DepoStoklar",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ayar = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ToplamGram = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OrtalamaMaliyet = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepoStoklar", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DepoStoklar_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AyarAyarlari",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ayar = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Milyem = table.Column<decimal>(type: "decimal(9,3)", nullable: false),
                    Iscilik = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    VarsayilanMaliyet = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table => { table.PrimaryKey("PK_AyarAyarlari", x => x.Id); });

            migrationBuilder.CreateIndex(
                name: "IX_DepoStoklar_TenantId_BranchId_Ayar",
                table: "DepoStoklar",
                columns: new[] { "TenantId", "BranchId", "Ayar" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AyarAyarlari_TenantId_Ayar",
                table: "AyarAyarlari",
                columns: new[] { "TenantId", "Ayar" },
                unique: true);

            // Varsayılan ayar ayarları (seed)
            migrationBuilder.Sql(@"
                INSERT INTO AyarAyarlari (Id, TenantId, Ayar, Milyem, Iscilik, VarsayilanMaliyet, UpdatedAt, IsDeleted, CreatedAt)
                SELECT NEWID(), t.Id, '14K', 585, 0, 0, GETUTCDATE(), 0, GETUTCDATE()
                FROM Tenants t WHERE NOT EXISTS (SELECT 1 FROM AyarAyarlari a WHERE a.TenantId = t.Id AND a.Ayar = '14K');
                INSERT INTO AyarAyarlari (Id, TenantId, Ayar, Milyem, Iscilik, VarsayilanMaliyet, UpdatedAt, IsDeleted, CreatedAt)
                SELECT NEWID(), t.Id, '18K', 750, 0, 0, GETUTCDATE(), 0, GETUTCDATE()
                FROM Tenants t WHERE NOT EXISTS (SELECT 1 FROM AyarAyarlari a WHERE a.TenantId = t.Id AND a.Ayar = '18K');
                INSERT INTO AyarAyarlari (Id, TenantId, Ayar, Milyem, Iscilik, VarsayilanMaliyet, UpdatedAt, IsDeleted, CreatedAt)
                SELECT NEWID(), t.Id, '22K', 916, 0, 0, GETUTCDATE(), 0, GETUTCDATE()
                FROM Tenants t WHERE NOT EXISTS (SELECT 1 FROM AyarAyarlari a WHERE a.TenantId = t.Id AND a.Ayar = '22K');
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DepoStoklar");
            migrationBuilder.DropTable(name: "AyarAyarlari");
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Customers') AND name = 'CariTip')
                    ALTER TABLE Customers DROP COLUMN CariTip;
            ");
        }
    }
}
