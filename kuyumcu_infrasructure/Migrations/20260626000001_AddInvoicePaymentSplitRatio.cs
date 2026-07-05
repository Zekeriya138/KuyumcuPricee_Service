using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoicePaymentSplitRatio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Invoices', 'PaymentSplitRatio') IS NULL
    ALTER TABLE [Invoices] ADD [PaymentSplitRatio] decimal(18,10) NOT NULL DEFAULT 1.0;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Invoices', 'PaymentSplitRatio') IS NOT NULL
    ALTER TABLE [Invoices] DROP COLUMN [PaymentSplitRatio];
");
        }
    }
}
