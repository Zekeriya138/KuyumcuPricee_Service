using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

/// <summary>
/// Canlı/yerel ortamda migration atlanmışsa EInvoiceProfiles kolonlarını idempotent ekler.
/// </summary>
public sealed class EInvoiceProfileSchemaEnsurer
{
    private readonly AppDbContext _db;
    private readonly ILogger<EInvoiceProfileSchemaEnsurer> _logger;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static volatile bool _ensured;

    public EInvoiceProfileSchemaEnsurer(AppDbContext db, ILogger<EInvoiceProfileSchemaEnsurer> logger)
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

            await _db.Database.ExecuteSqlRawAsync(EnsureColumnsSql, ct);
            _ensured = true;
            _logger.LogDebug("EInvoiceProfiles şema kolonları doğrulandı.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EInvoiceProfiles şema kolonları oluşturulamadı.");
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal const string EnsureColumnsSql = @"
IF OBJECT_ID(N'[dbo].[EInvoiceProfiles]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'[dbo].[EInvoiceProfiles]') AND name = N'SoleProprietorName')
    BEGIN
        ALTER TABLE [dbo].[EInvoiceProfiles] ADD [SoleProprietorName] nvarchar(200) NULL;
    END
END";
}
