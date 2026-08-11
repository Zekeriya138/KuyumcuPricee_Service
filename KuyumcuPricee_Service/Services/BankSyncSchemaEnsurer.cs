using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

/// <summary>
/// Canlı ortamda migration atlanmışsa Vomsis banka sync tablolarını oluşturur.
/// </summary>
public sealed class BankSyncSchemaEnsurer
{
    private readonly AppDbContext _db;
    private readonly ILogger<BankSyncSchemaEnsurer> _logger;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static volatile bool _ensured;

    public BankSyncSchemaEnsurer(AppDbContext db, ILogger<BankSyncSchemaEnsurer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task EnsureAsync(CancellationToken ct = default) => EnsureCoreAsync(force: false, ct);

    public async Task EnsureCoreAsync(bool force, CancellationToken ct)
    {
        if (_ensured && !force)
            return;

        await Gate.WaitAsync(ct);
        try
        {
            if (_ensured && !force)
                return;

            await _db.Database.ExecuteSqlRawAsync(BankImportTransactionsSql, Array.Empty<object>(), ct);
            await _db.Database.ExecuteSqlRawAsync(BankImportTransactionsIndexesSql, Array.Empty<object>(), ct);
            await _db.Database.ExecuteSqlRawAsync(BankImportTransactionsColumnsSql, Array.Empty<object>(), ct);
            await _db.Database.ExecuteSqlRawAsync(BankSyncProfilesSql, Array.Empty<object>(), ct);
            await _db.Database.ExecuteSqlRawAsync(BankSyncProfilesIndexesSql, Array.Empty<object>(), ct);
            await _db.Database.ExecuteSqlRawAsync(BankSyncProfilesColumnsSql, Array.Empty<object>(), ct);

            _ensured = true;
            _logger.LogInformation("Bank sync tabloları doğrulandı (BankSyncProfiles, BankImportTransactions).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bank sync tabloları oluşturulamadı.");
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private const string BankImportTransactionsSql = """
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
""";

    private const string BankImportTransactionsIndexesSql = """
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
""";

    private const string BankSyncProfilesSql = """
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
        [PollIntervalMinutes] int NOT NULL CONSTRAINT [DF_BankSyncProfiles_PollIntervalMinutes] DEFAULT(2),
        [AllowedAccountIds] nvarchar(128) NOT NULL CONSTRAINT [DF_BankSyncProfiles_AllowedAccountIds] DEFAULT(N'46'),
        [LookbackDays] int NOT NULL CONSTRAINT [DF_BankSyncProfiles_LookbackDays] DEFAULT(7),
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_BankSyncProfiles_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_BankSyncProfiles_CreatedAt] DEFAULT(sysutcdatetime())
    );
END
""";

    private const string BankSyncProfilesIndexesSql = """
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
""";

    private const string BankImportTransactionsColumnsSql = """
IF OBJECT_ID(N'[dbo].[BankImportTransactions]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('BankImportTransactions', 'BankBranchName') IS NULL
        ALTER TABLE [dbo].[BankImportTransactions] ADD [BankBranchName] nvarchar(256) NULL;
    IF COL_LENGTH('BankImportTransactions', 'BankBranchCity') IS NULL
        ALTER TABLE [dbo].[BankImportTransactions] ADD [BankBranchCity] nvarchar(64) NULL;
    IF COL_LENGTH('BankImportTransactions', 'BankBranchDistrict') IS NULL
        ALTER TABLE [dbo].[BankImportTransactions] ADD [BankBranchDistrict] nvarchar(64) NULL;
END
""";

    private const string BankSyncProfilesColumnsSql = """
IF OBJECT_ID(N'[dbo].[BankSyncProfiles]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('BankSyncProfiles', 'ManualSyncRequestedUtc') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [ManualSyncRequestedUtc] datetime2 NULL;
    IF COL_LENGTH('BankSyncProfiles', 'LastWorkerSyncUtc') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [LastWorkerSyncUtc] datetime2 NULL;
    IF COL_LENGTH('BankSyncProfiles', 'LastWorkerSyncFetched') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [LastWorkerSyncFetched] int NULL;
    IF COL_LENGTH('BankSyncProfiles', 'LastWorkerSyncImported') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [LastWorkerSyncImported] int NULL;
    IF COL_LENGTH('BankSyncProfiles', 'LastWorkerSyncMessage') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [LastWorkerSyncMessage] nvarchar(500) NULL;
    IF COL_LENGTH('BankSyncProfiles', 'AutoInstructionIncomingEnabled') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [AutoInstructionIncomingEnabled] bit NOT NULL CONSTRAINT [DF_BankSyncProfiles_AutoInstructionIncomingEnabled] DEFAULT(0);
    IF COL_LENGTH('BankSyncProfiles', 'AutoInstructionIncomingMinAmount') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [AutoInstructionIncomingMinAmount] decimal(18,2) NULL;
    IF COL_LENGTH('BankSyncProfiles', 'AutoInstructionOutgoingEnabled') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [AutoInstructionOutgoingEnabled] bit NOT NULL CONSTRAINT [DF_BankSyncProfiles_AutoInstructionOutgoingEnabled] DEFAULT(0);
    IF COL_LENGTH('BankSyncProfiles', 'AutoInstructionOutgoingMinAmount') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [AutoInstructionOutgoingMinAmount] decimal(18,2) NULL;
    IF COL_LENGTH('BankSyncProfiles', 'PendingEnrichExternalIdsJson') IS NULL
        ALTER TABLE [dbo].[BankSyncProfiles] ADD [PendingEnrichExternalIdsJson] nvarchar(2000) NULL;
    -- Eski varsayılan 5 dk; daha hızlı çekim için 2 dk'ya indir.
    UPDATE [dbo].[BankSyncProfiles]
    SET [PollIntervalMinutes] = 2
    WHERE [IsDeleted] = 0 AND [PollIntervalMinutes] = 5;
END
""";

    private const string CounterpartyIdentityCachesSql = """
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
""";

    private const string CounterpartyIdentityCachesIndexesSql = """
IF OBJECT_ID(N'[dbo].[CounterpartyIdentityCaches]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CounterpartyIdentityCaches_NormalizedIban' AND object_id = OBJECT_ID(N'[CounterpartyIdentityCaches]'))
        CREATE INDEX [IX_CounterpartyIdentityCaches_NormalizedIban] ON [dbo].[CounterpartyIdentityCaches]([NormalizedIban]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CounterpartyIdentityCaches_NormalizedName' AND object_id = OBJECT_ID(N'[CounterpartyIdentityCaches]'))
        CREATE INDEX [IX_CounterpartyIdentityCaches_NormalizedName] ON [dbo].[CounterpartyIdentityCaches]([NormalizedName]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CounterpartyIdentityCaches_NormalizedIban_TaxNo' AND object_id = OBJECT_ID(N'[CounterpartyIdentityCaches]'))
        CREATE INDEX [IX_CounterpartyIdentityCaches_NormalizedIban_TaxNo] ON [dbo].[CounterpartyIdentityCaches]([NormalizedIban],[TaxNo]);
END
""";
}
