using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBankSyncProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankSyncProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    VomsisAppKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    VomsisAppSecret = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ErpApiBaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ErpApiAppKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PollIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    AllowedAccountIds = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LookbackDays = table.Column<int>(type: "int", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankSyncProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankSyncProfiles_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankSyncProfiles_BranchId",
                table: "BankSyncProfiles",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_BankSyncProfiles_TenantId_BranchId",
                table: "BankSyncProfiles",
                columns: new[] { "TenantId", "BranchId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankSyncProfiles");
        }
    }
}
