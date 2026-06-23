using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class CreateDepoStokHavuzlarTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DepoStokHavuzlar",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ayar = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    MalTanimNorm = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TedarikciFirmaNorm = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    BirimMaliyet = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    TotalGram = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BarcodedGram = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnbarcodedGram = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepoStokHavuzlar", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DepoStokHavuzlar_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DepoStokHavuzlar_BranchId",
                table: "DepoStokHavuzlar",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_DepoStokHavuzlar_TenantId_BranchId_Ayar_MalTanimNorm_TedarikciFirmaNorm_BirimMaliyet",
                table: "DepoStokHavuzlar",
                columns: new[] { "TenantId", "BranchId", "Ayar", "MalTanimNorm", "TedarikciFirmaNorm", "BirimMaliyet" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DepoStokHavuzlar");
        }
    }
}
