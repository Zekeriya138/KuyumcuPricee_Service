using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrastructure.Migrations
{
    public partial class AddAlisAyarlari : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlisAyarlari",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ayar = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MilyemDegeri = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    BirimIscilikHas = table.Column<decimal>(type: "decimal(9,4)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlisAyarlari", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlisAyarlari_TenantId_Ayar_Model",
                table: "AlisAyarlari",
                columns: new[] { "TenantId", "Ayar", "Model" },
                unique: true);

            // Varsayılan kayıtlar: 14K|Normal, 18K|Normal, 22K|Normal
            migrationBuilder.Sql(@"
                INSERT INTO AlisAyarlari (Id, TenantId, Ayar, Model, MilyemDegeri, BirimIscilikHas, UpdatedAt, IsDeleted, CreatedAt)
                SELECT NEWID(), t.Id, '14K', 'Normal', 0.585, 0.150, GETUTCDATE(), 0, GETUTCDATE()
                FROM Tenants t WHERE NOT EXISTS (SELECT 1 FROM AlisAyarlari a WHERE a.TenantId = t.Id AND a.Ayar = '14K' AND a.Model = 'Normal');
                INSERT INTO AlisAyarlari (Id, TenantId, Ayar, Model, MilyemDegeri, BirimIscilikHas, UpdatedAt, IsDeleted, CreatedAt)
                SELECT NEWID(), t.Id, '18K', 'Normal', 0.750, 0.150, GETUTCDATE(), 0, GETUTCDATE()
                FROM Tenants t WHERE NOT EXISTS (SELECT 1 FROM AlisAyarlari a WHERE a.TenantId = t.Id AND a.Ayar = '18K' AND a.Model = 'Normal');
                INSERT INTO AlisAyarlari (Id, TenantId, Ayar, Model, MilyemDegeri, BirimIscilikHas, UpdatedAt, IsDeleted, CreatedAt)
                SELECT NEWID(), t.Id, '22K', 'Normal', 0.916, 0.150, GETUTCDATE(), 0, GETUTCDATE()
                FROM Tenants t WHERE NOT EXISTS (SELECT 1 FROM AlisAyarlari a WHERE a.TenantId = t.Id AND a.Ayar = '22K' AND a.Model = 'Normal');
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AlisAyarlari");
        }
    }
}
