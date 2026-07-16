using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    [Migration("20260711020000_AddBranchNoteOwnerFields")]
    public partial class AddBranchNoteOwnerFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "BranchNotes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerType",
                table: "BranchNotes",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BranchNotes_TenantId_BranchId_OwnerType_OwnerId",
                table: "BranchNotes",
                columns: new[] { "TenantId", "BranchId", "OwnerType", "OwnerId" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BranchNotes_TenantId_BranchId_OwnerType_OwnerId",
                table: "BranchNotes");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "BranchNotes");

            migrationBuilder.DropColumn(
                name: "OwnerType",
                table: "BranchNotes");
        }
    }
}
