-- DepoGumusLots tablosu yoksa oluşturur (migration çalışmadıysa / yanlış DB).
IF NOT EXISTS (SELECT 1 FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
               WHERE t.name = N'DepoGumusLots' AND s.name = N'dbo')
BEGIN
    CREATE TABLE [dbo].[DepoGumusLots] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [SupplierId] uniqueidentifier NULL,
        [SupplierName] nvarchar(256) NOT NULL,
        [ProductCode] nvarchar(64) NOT NULL,
        [ProductName] nvarchar(256) NOT NULL,
        [Gram] decimal(18,4) NOT NULL,
        [UnitCostTl] decimal(18,2) NOT NULL,
        [Note] nvarchar(512) NULL,
        [EntryDate] datetime2 NOT NULL,
        [IsDeleted] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_DepoGumusLots] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DepoGumusLots_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [dbo].[Branches] ([Id]) ON DELETE NO ACTION
    );

    CREATE INDEX [IX_DepoGumusLots_BranchId] ON [dbo].[DepoGumusLots] ([BranchId]);
    CREATE INDEX [IX_DepoGumusLots_TenantId_BranchId_ProductCode] ON [dbo].[DepoGumusLots] ([TenantId], [BranchId], [ProductCode]);
END
