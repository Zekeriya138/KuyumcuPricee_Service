using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

/// <summary>
/// Canlı ortamda migration atlanmışsa gider pusulası tablolarını idempotent oluşturur.
/// </summary>
public sealed class ExpenseSlipSchemaEnsurer
{
    private readonly AppDbContext _db;
    private readonly ILogger<ExpenseSlipSchemaEnsurer> _logger;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static volatile bool _ensured;

    public ExpenseSlipSchemaEnsurer(AppDbContext db, ILogger<ExpenseSlipSchemaEnsurer> logger)
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

            // CancellationToken'ı params object[] sanmasın diye boş parametre listesi geçiyoruz.
            // Aksi halde SQL içindeki '{}' format hatasına düşer (offset ~898).
            await _db.Database.ExecuteSqlRawAsync(UsersCanUseExpenseSlipSql, Array.Empty<object>(), ct);
            await _db.Database.ExecuteSqlRawAsync(ExpenseSlipDocumentsSql, Array.Empty<object>(), ct);
            await _db.Database.ExecuteSqlRawAsync(ExpenseSlipDocumentsIndexesSql, Array.Empty<object>(), ct);
            await _db.Database.ExecuteSqlRawAsync(ExpenseSlipAuditLogsSql, Array.Empty<object>(), ct);
            await _db.Database.ExecuteSqlRawAsync(ExpenseSlipAuditLogsIndexesSql, Array.Empty<object>(), ct);

            _ensured = true;
            _logger.LogInformation("Gider pusulası tabloları doğrulandı (ExpenseSlipDocuments, ExpenseSlipAuditLogs).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gider pusulası tabloları oluşturulamadı.");
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal const string UsersCanUseExpenseSlipSql =
        @"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanUseExpenseSlip')
          ALTER TABLE Users ADD CanUseExpenseSlip bit NOT NULL CONSTRAINT DF_Users_CanUseExpenseSlip DEFAULT(0);";

    internal const string ExpenseSlipDocumentsSql = """
IF OBJECT_ID(N'[dbo].[ExpenseSlipDocuments]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ExpenseSlipDocuments](
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [SourceSaleId] uniqueidentifier NULL,
        [DocumentNo] nvarchar(64) NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [Currency] nvarchar(8) NOT NULL,
        [GrandTotal] decimal(18,2) NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_GrandTotal] DEFAULT(0),
        [BuyerName] nvarchar(256) NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_BuyerName] DEFAULT(N''),
        [BuyerTaxNumber] nvarchar(32) NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_BuyerTaxNumber] DEFAULT(N''),
        [Description] nvarchar(512) NULL,
        [PayloadJson] nvarchar(max) NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_PayloadJson] DEFAULT(N'{{}}'),
        [RawLastResponse] nvarchar(max) NULL,
        [IntegratorDocumentId] nvarchar(128) NULL,
        [Uuid] nvarchar(64) NULL,
        [LastError] nvarchar(1000) NULL,
        [RetryCount] int NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_RetryCount] DEFAULT(0),
        [SubmittedAt] datetime2 NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_CreatedAt] DEFAULT(sysutcdatetime()),
        CONSTRAINT [PK_ExpenseSlipDocuments] PRIMARY KEY ([Id])
    );
END
""";

    internal const string ExpenseSlipDocumentsIndexesSql = """
IF OBJECT_ID(N'[dbo].[ExpenseSlipDocuments]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ExpenseSlipDocuments_Branches_BranchId')
        ALTER TABLE [dbo].[ExpenseSlipDocuments] WITH CHECK ADD CONSTRAINT [FK_ExpenseSlipDocuments_Branches_BranchId]
            FOREIGN KEY([BranchId]) REFERENCES [dbo].[Branches]([Id]) ON DELETE NO ACTION;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseSlipDocuments_BranchId' AND object_id = OBJECT_ID(N'[ExpenseSlipDocuments]'))
        CREATE INDEX [IX_ExpenseSlipDocuments_BranchId] ON [dbo].[ExpenseSlipDocuments]([BranchId]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseSlipDocuments_TenantId_BranchId_Status_CreatedAt' AND object_id = OBJECT_ID(N'[ExpenseSlipDocuments]'))
        CREATE INDEX [IX_ExpenseSlipDocuments_TenantId_BranchId_Status_CreatedAt]
            ON [dbo].[ExpenseSlipDocuments]([TenantId],[BranchId],[Status],[CreatedAt]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseSlipDocuments_TenantId_DocumentNo' AND object_id = OBJECT_ID(N'[ExpenseSlipDocuments]'))
        CREATE UNIQUE INDEX [IX_ExpenseSlipDocuments_TenantId_DocumentNo]
            ON [dbo].[ExpenseSlipDocuments]([TenantId],[DocumentNo]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseSlipDocuments_Tenant_Branch_Status_CreatedAt' AND object_id = OBJECT_ID(N'[ExpenseSlipDocuments]'))
        CREATE INDEX [IX_ExpenseSlipDocuments_Tenant_Branch_Status_CreatedAt]
            ON [dbo].[ExpenseSlipDocuments]([TenantId],[BranchId],[Status],[CreatedAt]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseSlipDocuments_Tenant_DocumentNo' AND object_id = OBJECT_ID(N'[ExpenseSlipDocuments]'))
        CREATE UNIQUE INDEX [IX_ExpenseSlipDocuments_Tenant_DocumentNo]
            ON [dbo].[ExpenseSlipDocuments]([TenantId],[DocumentNo]);
END
""";

    internal const string ExpenseSlipAuditLogsSql = """
IF OBJECT_ID(N'[dbo].[ExpenseSlipAuditLogs]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ExpenseSlipAuditLogs](
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [DocumentId] uniqueidentifier NOT NULL,
        [Action] nvarchar(64) NOT NULL,
        [StatusBefore] nvarchar(32) NULL,
        [StatusAfter] nvarchar(32) NULL,
        [IsSuccess] bit NOT NULL CONSTRAINT [DF_ExpenseSlipAuditLogs_IsSuccess] DEFAULT(0),
        [RequestJson] nvarchar(max) NULL,
        [ResponseRaw] nvarchar(max) NULL,
        [ErrorMessage] nvarchar(1000) NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_ExpenseSlipAuditLogs_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_ExpenseSlipAuditLogs_CreatedAt] DEFAULT(sysutcdatetime()),
        CONSTRAINT [PK_ExpenseSlipAuditLogs] PRIMARY KEY ([Id])
    );
END
""";

    internal const string ExpenseSlipAuditLogsIndexesSql = """
IF OBJECT_ID(N'[dbo].[ExpenseSlipAuditLogs]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ExpenseSlipAuditLogs_Branches_BranchId')
        ALTER TABLE [dbo].[ExpenseSlipAuditLogs] WITH CHECK ADD CONSTRAINT [FK_ExpenseSlipAuditLogs_Branches_BranchId]
            FOREIGN KEY([BranchId]) REFERENCES [dbo].[Branches]([Id]) ON DELETE NO ACTION;
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_ExpenseSlipAuditLogs_ExpenseSlipDocuments_DocumentId')
        ALTER TABLE [dbo].[ExpenseSlipAuditLogs] WITH CHECK ADD CONSTRAINT [FK_ExpenseSlipAuditLogs_ExpenseSlipDocuments_DocumentId]
            FOREIGN KEY([DocumentId]) REFERENCES [dbo].[ExpenseSlipDocuments]([Id]) ON DELETE CASCADE;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseSlipAuditLogs_BranchId' AND object_id = OBJECT_ID(N'[ExpenseSlipAuditLogs]'))
        CREATE INDEX [IX_ExpenseSlipAuditLogs_BranchId] ON [dbo].[ExpenseSlipAuditLogs]([BranchId]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseSlipAuditLogs_DocumentId' AND object_id = OBJECT_ID(N'[ExpenseSlipAuditLogs]'))
        CREATE INDEX [IX_ExpenseSlipAuditLogs_DocumentId] ON [dbo].[ExpenseSlipAuditLogs]([DocumentId]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseSlipAuditLogs_TenantId_BranchId_DocumentId_CreatedAt' AND object_id = OBJECT_ID(N'[ExpenseSlipAuditLogs]'))
        CREATE INDEX [IX_ExpenseSlipAuditLogs_TenantId_BranchId_DocumentId_CreatedAt]
            ON [dbo].[ExpenseSlipAuditLogs]([TenantId],[BranchId],[DocumentId],[CreatedAt]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExpenseSlipAuditLogs_Tenant_Branch_Document_CreatedAt' AND object_id = OBJECT_ID(N'[ExpenseSlipAuditLogs]'))
        CREATE INDEX [IX_ExpenseSlipAuditLogs_Tenant_Branch_Document_CreatedAt]
            ON [dbo].[ExpenseSlipAuditLogs]([TenantId],[BranchId],[DocumentId],[CreatedAt]);
END
""";
}
