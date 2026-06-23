using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations;

/// <summary>Hurda altın stok + defter + PurchasePayments.GoldSource.</summary>
public partial class AddScrapGoldModule : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ScrapStocks' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[ScrapStocks] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [Karat] nvarchar(16) NOT NULL,
        [WeightGram] decimal(18,4) NOT NULL,
        [PureGoldGram] decimal(18,4) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [IsDeleted] bit NOT NULL DEFAULT 0,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ScrapStocks] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ScrapStocks_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [dbo].[Branches] ([Id])
    );
    CREATE UNIQUE INDEX [IX_ScrapStocks_TenantId_BranchId_Karat] ON [dbo].[ScrapStocks] ([TenantId], [BranchId], [Karat]) WHERE [IsDeleted] = 0;
END

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ScrapLedgers' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[ScrapLedgers] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [Kind] int NOT NULL,
        [Karat] nvarchar(16) NOT NULL,
        [DeltaWeightGram] decimal(18,4) NOT NULL,
        [DeltaPureGoldGram] decimal(18,4) NOT NULL,
        [GoldPricePerGram] decimal(18,4) NULL,
        [AmountTl] decimal(18,2) NULL,
        [CustomerId] uniqueidentifier NULL,
        [SupplierId] uniqueidentifier NULL,
        [PurchaseId] uniqueidentifier NULL,
        [ProductId] uniqueidentifier NULL,
        [Note] nvarchar(500) NULL,
        [IsDeleted] bit NOT NULL DEFAULT 0,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ScrapLedgers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ScrapLedgers_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [dbo].[Branches] ([Id])
    );
    CREATE INDEX [IX_ScrapLedgers_TenantId_BranchId_CreatedAt] ON [dbo].[ScrapLedgers] ([TenantId], [BranchId], [CreatedAt]);
END

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PurchasePayments' AND schema_id = SCHEMA_ID('dbo'))
AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PurchasePayments') AND name = 'GoldSource')
BEGIN
    ALTER TABLE [dbo].[PurchasePayments] ADD [GoldSource] int NOT NULL DEFAULT 0;
END
");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PurchasePayments') AND name = 'GoldSource')
    ALTER TABLE [dbo].[PurchasePayments] DROP COLUMN [GoldSource];

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ScrapLedgers' AND schema_id = SCHEMA_ID('dbo'))
    DROP TABLE [dbo].[ScrapLedgers];

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ScrapStocks' AND schema_id = SCHEMA_ID('dbo'))
    DROP TABLE [dbo].[ScrapStocks];
");
    }
}
