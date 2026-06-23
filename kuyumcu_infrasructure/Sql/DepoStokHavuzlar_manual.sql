-- DepoStokHavuzlar tablosu yoksa oluşturur (migration çalışmadıysa / yanlış DB).
-- Önce API'nin kullandığı connection string ile AYNI veritabanında çalıştırın.
-- Sonra (isteğe bağlı) EF geçmişine kayıt ekleyin: aşağıdaki INSERT yorumunu okuyun.

IF NOT EXISTS (SELECT 1 FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
               WHERE t.name = N'DepoStokHavuzlar' AND s.name = N'dbo')
BEGIN
    CREATE TABLE [dbo].[DepoStokHavuzlar] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [Ayar] nvarchar(16) NOT NULL,
        [MalTanimNorm] nvarchar(512) NOT NULL,
        [TedarikciFirmaNorm] nvarchar(256) NOT NULL,
        [BirimMaliyet] decimal(18,4) NOT NULL,
        [TotalGram] decimal(18,4) NOT NULL,
        [BarcodedGram] decimal(18,4) NOT NULL,
        [UnbarcodedGram] decimal(18,4) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [IsDeleted] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_DepoStokHavuzlar] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_DepoStokHavuzlar_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [dbo].[Branches] ([Id]) ON DELETE NO ACTION
    );

    CREATE INDEX [IX_DepoStokHavuzlar_BranchId] ON [dbo].[DepoStokHavuzlar] ([BranchId]);

    CREATE UNIQUE INDEX [IX_DepoStokHavuzlar_TenantId_BranchId_Ayar_MalTanimNorm_TedarikciFirmaNorm_BirimMaliyet]
        ON [dbo].[DepoStokHavuzlar] ([TenantId], [BranchId], [Ayar], [MalTanimNorm], [TedarikciFirmaNorm], [BirimMaliyet]);
END
GO

-- EF migration geçmişi boşsa veya bu migration eksikse (manuel tablo sonrası), bir kez çalıştırın:
-- INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
-- VALUES (N'20260326111947_CreateDepoStokHavuzlarTable', N'9.0.8');
-- NOT: Aynı MigrationId zaten varsa bu INSERT'i atlayın (duplicate key hatası verir).
