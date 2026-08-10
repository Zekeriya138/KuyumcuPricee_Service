-- Canlı veritabanında BankSyncProfiles / BankImportTransactions tabloları yoksa çalıştırın.
-- Azure SQL Query Editor veya SSMS ile production DB'de execute edin.

IF OBJECT_ID(N'[dbo].[BankImportTransactions]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[BankImportTransactions](
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_BankImportTransactions] PRIMARY KEY,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [Provider] nvarchar(32) NOT NULL,
        [ExternalId] bigint NOT NULL,
        [ExternalKey] nvarchar(128) NOT NULL,
        [VomsisAccountId] int NULL,
        [Amount] decimal(18,4) NOT NULL,
        [Currency] nvarchar(8) NOT NULL,
        [TransactionType] nvarchar(32) NOT NULL,
        [Description] nvarchar(1000) NULL,
        [CounterpartyName] nvarchar(256) NULL,
        [CounterpartyTaxNo] nvarchar(32) NULL,
        [CounterpartyIban] nvarchar(64) NULL,
        [TransactionDateUtc] datetime2 NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [StatusMessage] nvarchar(500) NULL,
        [MatchedCustomerId] uniqueidentifier NULL,
        [InvoiceId] uniqueidentifier NULL,
        [EInvoiceDocumentId] uniqueidentifier NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_BankImportTransactions_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_BankImportTransactions_CreatedAt] DEFAULT(sysutcdatetime())
    );
END
GO

IF OBJECT_ID(N'[dbo].[BankImportTransactions]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BankImportTransactions_TenantId_BranchId_Provider_ExternalKey' AND object_id = OBJECT_ID(N'[BankImportTransactions]'))
        CREATE UNIQUE INDEX [IX_BankImportTransactions_TenantId_BranchId_Provider_ExternalKey]
            ON [dbo].[BankImportTransactions]([TenantId],[BranchId],[Provider],[ExternalKey]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BankImportTransactions_TenantId_BranchId_Status_TransactionDateUtc' AND object_id = OBJECT_ID(N'[BankImportTransactions]'))
        CREATE INDEX [IX_BankImportTransactions_TenantId_BranchId_Status_TransactionDateUtc]
            ON [dbo].[BankImportTransactions]([TenantId],[BranchId],[Status],[TransactionDateUtc]);
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BankImportTransactions_Branches_BranchId')
        ALTER TABLE [dbo].[BankImportTransactions] WITH CHECK ADD CONSTRAINT [FK_BankImportTransactions_Branches_BranchId]
            FOREIGN KEY([BranchId]) REFERENCES [dbo].[Branches]([Id]) ON DELETE NO ACTION;
END
GO

IF OBJECT_ID(N'[dbo].[BankSyncProfiles]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[BankSyncProfiles](
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_BankSyncProfiles] PRIMARY KEY,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [IsEnabled] bit NOT NULL CONSTRAINT [DF_BankSyncProfiles_IsEnabled] DEFAULT(1),
        [VomsisAppKey] nvarchar(256) NULL,
        [VomsisAppSecret] nvarchar(512) NULL,
        [ErpApiBaseUrl] nvarchar(512) NOT NULL,
        [ErpApiAppKey] nvarchar(256) NULL,
        [PollIntervalMinutes] int NOT NULL CONSTRAINT [DF_BankSyncProfiles_PollIntervalMinutes] DEFAULT(5),
        [AllowedAccountIds] nvarchar(128) NOT NULL CONSTRAINT [DF_BankSyncProfiles_AllowedAccountIds] DEFAULT(N'46'),
        [LookbackDays] int NOT NULL CONSTRAINT [DF_BankSyncProfiles_LookbackDays] DEFAULT(7),
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_BankSyncProfiles_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_BankSyncProfiles_CreatedAt] DEFAULT(sysutcdatetime())
    );
END
GO

IF OBJECT_ID(N'[dbo].[BankSyncProfiles]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BankSyncProfiles_TenantId_BranchId' AND object_id = OBJECT_ID(N'[BankSyncProfiles]'))
        CREATE UNIQUE INDEX [IX_BankSyncProfiles_TenantId_BranchId] ON [dbo].[BankSyncProfiles]([TenantId],[BranchId]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BankSyncProfiles_BranchId' AND object_id = OBJECT_ID(N'[BankSyncProfiles]'))
        CREATE INDEX [IX_BankSyncProfiles_BranchId] ON [dbo].[BankSyncProfiles]([BranchId]);
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BankSyncProfiles_Branches_BranchId')
        ALTER TABLE [dbo].[BankSyncProfiles] WITH CHECK ADD CONSTRAINT [FK_BankSyncProfiles_Branches_BranchId]
            FOREIGN KEY([BranchId]) REFERENCES [dbo].[Branches]([Id]) ON DELETE NO ACTION;
END
GO

IF OBJECT_ID(N'[dbo].[CounterpartyIdentityCaches]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[CounterpartyIdentityCaches](
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_CounterpartyIdentityCaches] PRIMARY KEY,
        [NormalizedIban] nvarchar(64) NULL,
        [NormalizedName] nvarchar(256) NOT NULL,
        [TaxNo] nvarchar(32) NOT NULL,
        [DisplayName] nvarchar(256) NULL,
        [Source] nvarchar(32) NOT NULL,
        [LinkedCustomerId] uniqueidentifier NULL,
        [LinkedSupplierId] uniqueidentifier NULL,
        [LearnedByTenantId] uniqueidentifier NULL,
        [LastSeenAtUtc] datetime2 NOT NULL CONSTRAINT [DF_CounterpartyIdentityCaches_LastSeenAtUtc] DEFAULT(sysutcdatetime()),
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_CounterpartyIdentityCaches_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_CounterpartyIdentityCaches_CreatedAt] DEFAULT(sysutcdatetime())
    );
END
GO

IF OBJECT_ID(N'[dbo].[CounterpartyIdentityCaches]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CounterpartyIdentityCaches_NormalizedIban' AND object_id = OBJECT_ID(N'[CounterpartyIdentityCaches]'))
        CREATE INDEX [IX_CounterpartyIdentityCaches_NormalizedIban] ON [dbo].[CounterpartyIdentityCaches]([NormalizedIban]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CounterpartyIdentityCaches_NormalizedName' AND object_id = OBJECT_ID(N'[CounterpartyIdentityCaches]'))
        CREATE INDEX [IX_CounterpartyIdentityCaches_NormalizedName] ON [dbo].[CounterpartyIdentityCaches]([NormalizedName]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CounterpartyIdentityCaches_NormalizedIban_TaxNo' AND object_id = OBJECT_ID(N'[CounterpartyIdentityCaches]'))
        CREATE INDEX [IX_CounterpartyIdentityCaches_NormalizedIban_TaxNo] ON [dbo].[CounterpartyIdentityCaches]([NormalizedIban],[TaxNo]);
END
GO
