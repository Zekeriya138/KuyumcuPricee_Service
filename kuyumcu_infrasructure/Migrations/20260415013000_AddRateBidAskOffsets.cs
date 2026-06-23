using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    public partial class AddRateBidAskOffsets : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AskTlOffset",
                table: "RateDisplaySettings",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BidTlOffset",
                table: "RateDisplaySettings",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(@"
UPDATE r
SET r.BidTlOffset = r.TlOffset,
    r.AskTlOffset = r.TlOffset
FROM RateDisplaySettings r
WHERE r.BidTlOffset = 0
  AND r.AskTlOffset = 0
  AND r.TlOffset <> 0;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AskTlOffset",
                table: "RateDisplaySettings");

            migrationBuilder.DropColumn(
                name: "BidTlOffset",
                table: "RateDisplaySettings");
        }
    }
}
