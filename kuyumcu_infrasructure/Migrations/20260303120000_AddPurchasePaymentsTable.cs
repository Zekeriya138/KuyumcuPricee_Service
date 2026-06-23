using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kuyumcu_infrasructure.Migrations
{
    /// <summary>Alış ödeme satırları (karma ödeme).</summary>
    public partial class AddPurchasePaymentsTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PurchasePayments' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE [dbo].[PurchasePayments] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [PurchaseId] uniqueidentifier NOT NULL,
        [PaymentType] int NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [GoldWeight] decimal(18,4) NULL,
        [GoldKarat] nvarchar(16) NULL,
        [GoldPrice] decimal(18,4) NULL,
        [BankName] nvarchar(128) NULL,
        [IBAN] nvarchar(34) NULL,
        [DueDate] datetime2 NULL,
        [CashAccount] nvarchar(128) NULL,
        [IsDeleted] bit NOT NULL DEFAULT 0,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_PurchasePayments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_PurchasePayments_Purchases_PurchaseId] FOREIGN KEY ([PurchaseId]) REFERENCES [dbo].[Purchases] ([Id]) ON DELETE CASCADE
    );
    CREATE INDEX [IX_PurchasePayments_PurchaseId] ON [dbo].[PurchasePayments] ([PurchaseId]);
    CREATE INDEX [IX_PurchasePayments_TenantId] ON [dbo].[PurchasePayments] ([TenantId]);
END
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PurchasePayments' AND schema_id = SCHEMA_ID('dbo'))
    DROP TABLE [dbo].[PurchasePayments];
");
        }
    }
}
