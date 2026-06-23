using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <inheritdoc />
    public partial class msdşkjsşkdj : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[dbo].[Accounts]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Accounts](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [TenantId] uniqueidentifier NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [Code] nvarchar(32) NOT NULL,
        [Type] int NOT NULL,
        [IsSystemAccount] bit NOT NULL DEFAULT CAST(0 AS bit),
        [IsDeleted] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL
    );
END
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Accounts_TenantId_Code' AND object_id = OBJECT_ID(N'[dbo].[Accounts]'))
    CREATE UNIQUE INDEX [IX_Accounts_TenantId_Code] ON [dbo].[Accounts]([TenantId], [Code]);

IF OBJECT_ID(N'[dbo].[JournalEntries]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[JournalEntries](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NULL,
        [Date] datetime2 NOT NULL,
        [Description] nvarchar(512) NOT NULL,
        [IsDeleted] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL
    );
END
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_JournalEntries_Branches_BranchId')
    ALTER TABLE [dbo].[JournalEntries] WITH CHECK
        ADD CONSTRAINT [FK_JournalEntries_Branches_BranchId] FOREIGN KEY([BranchId]) REFERENCES [dbo].[Branches]([Id]) ON DELETE SET NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JournalEntries_BranchId' AND object_id = OBJECT_ID(N'[dbo].[JournalEntries]'))
    CREATE INDEX [IX_JournalEntries_BranchId] ON [dbo].[JournalEntries]([BranchId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JournalEntries_TenantId_BranchId_Date_CreatedAt' AND object_id = OBJECT_ID(N'[dbo].[JournalEntries]'))
    CREATE INDEX [IX_JournalEntries_TenantId_BranchId_Date_CreatedAt] ON [dbo].[JournalEntries]([TenantId], [BranchId], [Date], [CreatedAt]);

IF OBJECT_ID(N'[dbo].[JournalLines]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[JournalLines](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NULL,
        [JournalEntryId] uniqueidentifier NOT NULL,
        [AccountId] uniqueidentifier NOT NULL,
        [Debit] decimal(18,4) NOT NULL,
        [Credit] decimal(18,4) NOT NULL,
        [IsDeleted] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL
    );
END
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_JournalLines_Accounts_AccountId')
    ALTER TABLE [dbo].[JournalLines] WITH CHECK
        ADD CONSTRAINT [FK_JournalLines_Accounts_AccountId] FOREIGN KEY([AccountId]) REFERENCES [dbo].[Accounts]([Id]) ON DELETE NO ACTION;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_JournalLines_Branches_BranchId')
    ALTER TABLE [dbo].[JournalLines] WITH CHECK
        ADD CONSTRAINT [FK_JournalLines_Branches_BranchId] FOREIGN KEY([BranchId]) REFERENCES [dbo].[Branches]([Id]) ON DELETE SET NULL;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_JournalLines_JournalEntries_JournalEntryId')
    ALTER TABLE [dbo].[JournalLines] WITH CHECK
        ADD CONSTRAINT [FK_JournalLines_JournalEntries_JournalEntryId] FOREIGN KEY([JournalEntryId]) REFERENCES [dbo].[JournalEntries]([Id]) ON DELETE CASCADE;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JournalLines_AccountId' AND object_id = OBJECT_ID(N'[dbo].[JournalLines]'))
    CREATE INDEX [IX_JournalLines_AccountId] ON [dbo].[JournalLines]([AccountId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JournalLines_BranchId' AND object_id = OBJECT_ID(N'[dbo].[JournalLines]'))
    CREATE INDEX [IX_JournalLines_BranchId] ON [dbo].[JournalLines]([BranchId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JournalLines_JournalEntryId' AND object_id = OBJECT_ID(N'[dbo].[JournalLines]'))
    CREATE INDEX [IX_JournalLines_JournalEntryId] ON [dbo].[JournalLines]([JournalEntryId]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_JournalLines_TenantId_BranchId_AccountId_CreatedAt' AND object_id = OBJECT_ID(N'[dbo].[JournalLines]'))
    CREATE INDEX [IX_JournalLines_TenantId_BranchId_AccountId_CreatedAt] ON [dbo].[JournalLines]([TenantId], [BranchId], [AccountId], [CreatedAt]);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[dbo].[JournalLines]', N'U') IS NOT NULL DROP TABLE [dbo].[JournalLines];
IF OBJECT_ID(N'[dbo].[JournalEntries]', N'U') IS NOT NULL DROP TABLE [dbo].[JournalEntries];
IF OBJECT_ID(N'[dbo].[Accounts]', N'U') IS NOT NULL DROP TABLE [dbo].[Accounts];
");
        }
    }
}
