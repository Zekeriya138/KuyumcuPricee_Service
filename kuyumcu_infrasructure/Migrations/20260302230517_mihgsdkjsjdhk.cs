using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class mihgsdkjsjdhk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CariTip",
                table: "Customers",
                type: "int",
                nullable: false,
                defaultValue: 0);

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
                constraints: table =>
                {
                    table.PrimaryKey("PK_AyarAyarlari", x => x.Id);
                });

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

            migrationBuilder.CreateIndex(
                name: "IX_AyarAyarlari_TenantId_Ayar",
                table: "AyarAyarlari",
                columns: new[] { "TenantId", "Ayar" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DepoStoklar_BranchId",
                table: "DepoStoklar",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_DepoStoklar_TenantId_BranchId_Ayar",
                table: "DepoStoklar",
                columns: new[] { "TenantId", "BranchId", "Ayar" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AyarAyarlari");

            migrationBuilder.DropTable(
                name: "DepoStoklar");

            migrationBuilder.DropColumn(
                name: "CariTip",
                table: "Customers");
        }
    }
}
