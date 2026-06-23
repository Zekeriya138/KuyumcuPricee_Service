using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class FixDepoStokGramColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Eski DB'lerde hâlâ ToplamGram olabilir; yeni kod TotalGram + BarcodedGram + UnbarcodedGram bekler.
            // Her adım ayrı batch (SQL Server tek batch'te UPDATE'i yeni sütunlar oluşmadan parse edebiliyor).
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1 FROM sys.columns c
    INNER JOIN sys.tables t ON c.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND t.name = N'DepoStoklar' AND c.name = N'ToplamGram')
BEGIN
    EXEC sp_rename N'dbo.DepoStoklar.ToplamGram', N'TotalGram', N'COLUMN';
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    INNER JOIN sys.tables t ON c.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND t.name = N'DepoStoklar' AND c.name = N'BarcodedGram')
BEGIN
    ALTER TABLE [dbo].[DepoStoklar] ADD [BarcodedGram] decimal(18,4) NOT NULL CONSTRAINT [DF_DepoStoklar_BarcodedGram] DEFAULT 0;
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    INNER JOIN sys.tables t ON c.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'dbo' AND t.name = N'DepoStoklar' AND c.name = N'UnbarcodedGram')
BEGIN
    ALTER TABLE [dbo].[DepoStoklar] ADD [UnbarcodedGram] decimal(18,4) NOT NULL CONSTRAINT [DF_DepoStoklar_UnbarcodedGram] DEFAULT 0;
END");

            migrationBuilder.Sql(@"
UPDATE [dbo].[DepoStoklar]
SET [UnbarcodedGram] = [TotalGram], [BarcodedGram] = 0
WHERE [BarcodedGram] = 0 AND [UnbarcodedGram] = 0 AND [TotalGram] <> 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_DepoStoklar_BarcodedGram')
    ALTER TABLE [dbo].[DepoStoklar] DROP CONSTRAINT [DF_DepoStoklar_BarcodedGram];
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_DepoStoklar_UnbarcodedGram')
    ALTER TABLE [dbo].[DepoStoklar] DROP CONSTRAINT [DF_DepoStoklar_UnbarcodedGram];
IF EXISTS (SELECT 1 FROM sys.columns c INNER JOIN sys.tables t ON c.object_id = t.object_id INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = N'dbo' AND t.name = N'DepoStoklar' AND c.name = N'UnbarcodedGram')
    ALTER TABLE [dbo].[DepoStoklar] DROP COLUMN [UnbarcodedGram];
IF EXISTS (SELECT 1 FROM sys.columns c INNER JOIN sys.tables t ON c.object_id = t.object_id INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = N'dbo' AND t.name = N'DepoStoklar' AND c.name = N'BarcodedGram')
    ALTER TABLE [dbo].[DepoStoklar] DROP COLUMN [BarcodedGram];
IF EXISTS (SELECT 1 FROM sys.columns c INNER JOIN sys.tables t ON c.object_id = t.object_id INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = N'dbo' AND t.name = N'DepoStoklar' AND c.name = N'TotalGram')
    EXEC sp_rename N'dbo.DepoStoklar.TotalGram', N'ToplamGram', N'COLUMN';
");
        }
    }
}
