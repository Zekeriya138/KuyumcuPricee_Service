using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class migadsasqwqq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Not: AskTlOffset / BidTlOffset için bkz. 20260415013000_AddRateBidAskOffsets — tekrar eklenmez.

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'dbo.SupplierBalances', N'BalanceGBP') IS NULL
    ALTER TABLE [SupplierBalances] ADD [BalanceGBP] decimal(18,2) NOT NULL CONSTRAINT [DF_SupplierBalances_BalanceGBP_mig] DEFAULT 0;
");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'dbo.CustomerBalances', N'BalanceGBP') IS NULL
    ALTER TABLE [CustomerBalances] ADD [BalanceGBP] decimal(18,2) NOT NULL CONSTRAINT [DF_CustomerBalances_BalanceGBP_mig] DEFAULT 0;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH(N'dbo.CustomerBalances', N'BalanceGBP') IS NOT NULL
BEGIN
    DECLARE @dn sysname;
    SELECT @dn = dc.name FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'[CustomerBalances]') AND c.name = N'BalanceGBP';
    IF @dn IS NOT NULL EXEC(N'ALTER TABLE [CustomerBalances] DROP CONSTRAINT [' + @dn + N'];');
    ALTER TABLE [CustomerBalances] DROP COLUMN [BalanceGBP];
END
");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'dbo.SupplierBalances', N'BalanceGBP') IS NOT NULL
BEGIN
    DECLARE @dn2 sysname;
    SELECT @dn2 = dc.name FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'[SupplierBalances]') AND c.name = N'BalanceGBP';
    IF @dn2 IS NOT NULL EXEC(N'ALTER TABLE [SupplierBalances] DROP CONSTRAINT [' + @dn2 + N'];');
    ALTER TABLE [SupplierBalances] DROP COLUMN [BalanceGBP];
END
");
        }
    }
}
