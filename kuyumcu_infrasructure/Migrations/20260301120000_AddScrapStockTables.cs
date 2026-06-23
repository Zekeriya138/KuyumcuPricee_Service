using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <summary>Hurda altın stoğu ve hareket günlüğü.</summary>
    public partial class AddScrapStockTables : Migration
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
        [Weight] decimal(18,4) NOT NULL,
        [PureGold] decimal(18,4) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [IsDeleted] bit NOT NULL DEFAULT 0,
        [UpdatedAt] datetime2 NOT NULL DEFAULT GETUTCDATE(),
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
        [Type] int NOT NULL,
        [Karat] nvarchar(16) NOT NULL,
        [DeltaWeight] decimal(18,4) NOT NULL,
        [DeltaPureGold] decimal(18,4) NOT NULL,
        [AmountTl] decimal(18,2) NULL,
        [GoldPricePerGram] decimal(18,2) NULL,
        [CustomerId] uniqueidentifier NULL,
        [PurchaseId] uniqueidentifier NULL,
        [Note] nvarchar(500) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [IsDeleted] bit NOT NULL DEFAULT 0,
        CONSTRAINT [PK_ScrapLedgers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ScrapLedgers_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [dbo].[Branches] ([Id])
    );
    CREATE INDEX [IX_ScrapLedgers_BranchId] ON [dbo].[ScrapLedgers] ([BranchId]);
    CREATE INDEX [IX_ScrapLedgers_TenantId] ON [dbo].[ScrapLedgers] ([TenantId]);
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ScrapLedgers' AND schema_id = SCHEMA_ID('dbo'))
    DROP TABLE [dbo].[ScrapLedgers];
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ScrapStocks' AND schema_id = SCHEMA_ID('dbo'))
    DROP TABLE [dbo].[ScrapStocks];
");
        }
    }
}
