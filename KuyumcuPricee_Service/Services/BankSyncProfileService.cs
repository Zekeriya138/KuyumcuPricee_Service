using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace KUYUMCU.Price_Service.Services;

public interface IBankSyncProfileService
{
    Task<BankSyncProfileDto> GetProfileAsync(Guid tenantId, Guid branchId, CancellationToken ct);
    Task<BankSyncProfileDto> SaveProfileAsync(Guid tenantId, SaveBankSyncProfileReq req, CancellationToken ct);
    Task<BankSyncWorkerConfigDto?> GetWorkerConfigAsync(Guid tenantId, Guid branchId, CancellationToken ct);
    Task RequestManualSyncAsync(Guid tenantId, Guid branchId, CancellationToken ct);
    Task CompleteManualSyncAsync(Guid tenantId, Guid branchId, BankSyncPullResult result, CancellationToken ct);
    Task<BankSyncProfileDto> SaveAutoInstructionAsync(Guid tenantId, SaveBankAutoInstructionReq req, CancellationToken ct);
}

public sealed class BankSyncProfileDto
{
    public Guid BranchId { get; set; }
    public Guid TenantId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? VomsisAppKey { get; set; }
    public bool HasVomsisAppSecret { get; set; }
    public string ErpApiBaseUrl { get; set; } = "";
    public bool HasErpApiAppKey { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;
    public string AllowedAccountIds { get; set; } = "46";
    public int LookbackDays { get; set; } = 7;
    public DateTime? ManualSyncRequestedUtc { get; set; }
    public DateTime? LastWorkerSyncUtc { get; set; }
    public int? LastWorkerSyncFetched { get; set; }
    public int? LastWorkerSyncImported { get; set; }
    public string? LastWorkerSyncMessage { get; set; }
    public bool AutoInstructionIncomingEnabled { get; set; }
    public decimal? AutoInstructionIncomingMinAmount { get; set; }
    public bool AutoInstructionOutgoingEnabled { get; set; }
    public decimal? AutoInstructionOutgoingMinAmount { get; set; }

    public bool IsConfigured =>
        HasVomsisAppSecret &&
        !string.IsNullOrWhiteSpace(VomsisAppKey) &&
        HasErpApiAppKey &&
        !string.IsNullOrWhiteSpace(ErpApiBaseUrl);
}

public sealed class BankSyncWorkerConfigDto
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public bool IsEnabled { get; set; }
    public string? VomsisAppKey { get; set; }
    public string? VomsisAppSecret { get; set; }
    public string ErpApiBaseUrl { get; set; } = "";
    public string? ErpApiAppKey { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;
    public int[] AllowedAccountIds { get; set; } = [];
    public int LookbackDays { get; set; } = 7;
    public DateTime? ManualSyncRequestedUtc { get; set; }
}

public sealed class SaveBankSyncProfileReq
{
    public Guid BranchId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? VomsisAppKey { get; set; }
    public string? VomsisAppSecret { get; set; }
    public string? ErpApiBaseUrl { get; set; }
    public string? ErpApiAppKey { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;
    public string? AllowedAccountIds { get; set; }
    public int LookbackDays { get; set; } = 7;
}

public sealed class SaveBankAutoInstructionReq
{
    public Guid BranchId { get; set; }
    public bool AutoInstructionIncomingEnabled { get; set; }
    public decimal? AutoInstructionIncomingMinAmount { get; set; }
    public bool AutoInstructionOutgoingEnabled { get; set; }
    public decimal? AutoInstructionOutgoingMinAmount { get; set; }
}

public sealed class BankSyncProfileService : IBankSyncProfileService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly BankSyncSchemaEnsurer _schema;

    public BankSyncProfileService(AppDbContext db, IConfiguration config, BankSyncSchemaEnsurer schema)
    {
        _db = db;
        _config = config;
        _schema = schema;
    }

    public async Task<BankSyncProfileDto> GetProfileAsync(Guid tenantId, Guid branchId, CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        var profile = await _db.BankSyncProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId, ct);

        if (profile is null)
        {
            return new BankSyncProfileDto
            {
                TenantId = tenantId,
                BranchId = branchId,
                IsEnabled = true,
                PollIntervalMinutes = 5,
                AllowedAccountIds = "46",
                LookbackDays = 7
            };
        }

        return ToDto(profile);
    }

    public async Task<BankSyncProfileDto> SaveProfileAsync(Guid tenantId, SaveBankSyncProfileReq req, CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        if (req.BranchId == Guid.Empty)
            throw new InvalidOperationException("BranchId zorunludur.");

        var profile = await _db.BankSyncProfiles
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == req.BranchId, ct);

        var isNew = profile is null;
        profile ??= new BankSyncProfile
        {
            TenantId = tenantId,
            BranchId = req.BranchId
        };

        profile.IsEnabled = req.IsEnabled;
        profile.VomsisAppKey = string.IsNullOrWhiteSpace(req.VomsisAppKey) ? profile.VomsisAppKey : req.VomsisAppKey.Trim();
        if (!string.IsNullOrWhiteSpace(req.VomsisAppSecret))
            profile.VomsisAppSecret = req.VomsisAppSecret.Trim();

        profile.ErpApiBaseUrl = string.IsNullOrWhiteSpace(req.ErpApiBaseUrl)
            ? (string.IsNullOrWhiteSpace(profile.ErpApiBaseUrl) ? "" : profile.ErpApiBaseUrl.Trim())
            : req.ErpApiBaseUrl.Trim().TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(req.ErpApiAppKey))
            profile.ErpApiAppKey = req.ErpApiAppKey.Trim();

        profile.PollIntervalMinutes = Math.Clamp(req.PollIntervalMinutes, 1, 60);
        profile.LookbackDays = Math.Clamp(req.LookbackDays, 1, 30);
        profile.AllowedAccountIds = NormalizeAccountIds(req.AllowedAccountIds);

        ApplyErpDefaults(profile, req);

        if (isNew)
        {
            if (string.IsNullOrWhiteSpace(profile.VomsisAppKey))
                throw new InvalidOperationException("Vomsis App Key zorunludur.");
            if (string.IsNullOrWhiteSpace(profile.VomsisAppSecret))
                throw new InvalidOperationException("Vomsis App Secret zorunludur.");
            if (string.IsNullOrWhiteSpace(profile.ErpApiBaseUrl))
                throw new InvalidOperationException("ERP API adresi zorunludur.");
            if (string.IsNullOrWhiteSpace(profile.ErpApiAppKey))
                throw new InvalidOperationException("ERP API anahtarı (x-app-key) zorunludur.");
            _db.BankSyncProfiles.Add(profile);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(profile.VomsisAppKey))
                throw new InvalidOperationException("Vomsis App Key zorunludur.");
            if (string.IsNullOrWhiteSpace(profile.ErpApiBaseUrl))
                throw new InvalidOperationException("ERP API adresi zorunludur.");
        }

        await _db.SaveChangesAsync(ct);
        return ToDto(profile);
    }

    public async Task<BankSyncWorkerConfigDto?> GetWorkerConfigAsync(Guid tenantId, Guid branchId, CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        var profile = await _db.BankSyncProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && x.IsEnabled, ct);
        if (profile is null || profile.IsDeleted)
            return null;
        if (string.IsNullOrWhiteSpace(profile.VomsisAppKey) || string.IsNullOrWhiteSpace(profile.VomsisAppSecret))
            return null;
        if (string.IsNullOrWhiteSpace(profile.ErpApiBaseUrl) || string.IsNullOrWhiteSpace(profile.ErpApiAppKey))
            return null;

        return new BankSyncWorkerConfigDto
        {
            TenantId = tenantId,
            BranchId = branchId,
            IsEnabled = profile.IsEnabled,
            VomsisAppKey = profile.VomsisAppKey,
            VomsisAppSecret = profile.VomsisAppSecret,
            ErpApiBaseUrl = profile.ErpApiBaseUrl.TrimEnd('/'),
            ErpApiAppKey = profile.ErpApiAppKey,
            PollIntervalMinutes = Math.Clamp(profile.PollIntervalMinutes, 1, 60),
            LookbackDays = Math.Clamp(profile.LookbackDays, 1, 30),
            AllowedAccountIds = ParseAccountIds(profile.AllowedAccountIds),
            ManualSyncRequestedUtc = profile.ManualSyncRequestedUtc
        };
    }

    public async Task RequestManualSyncAsync(Guid tenantId, Guid branchId, CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        var profile = await _db.BankSyncProfiles
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("Vomsis ayarları bulunamadı.");

        profile.ManualSyncRequestedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task CompleteManualSyncAsync(Guid tenantId, Guid branchId, BankSyncPullResult result, CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        var profile = await _db.BankSyncProfiles
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (profile is null) return;

        profile.ManualSyncRequestedUtc = null;
        profile.LastWorkerSyncUtc = DateTime.UtcNow;
        profile.LastWorkerSyncFetched = result.FetchedFromVomsis;
        profile.LastWorkerSyncImported = result.Imported;
        profile.LastWorkerSyncMessage = result.SummaryMessage;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<BankSyncProfileDto> SaveAutoInstructionAsync(Guid tenantId, SaveBankAutoInstructionReq req, CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        if (req.BranchId == Guid.Empty)
            throw new InvalidOperationException("BranchId zorunludur.");

        if (req.AutoInstructionIncomingEnabled &&
            (!req.AutoInstructionIncomingMinAmount.HasValue || req.AutoInstructionIncomingMinAmount.Value <= 0m))
        {
            throw new InvalidOperationException("Gelen havale otomatik gönderimi için minimum tutar (TL) zorunludur.");
        }

        if (req.AutoInstructionOutgoingEnabled &&
            (!req.AutoInstructionOutgoingMinAmount.HasValue || req.AutoInstructionOutgoingMinAmount.Value <= 0m))
        {
            throw new InvalidOperationException("Giden havale otomatik gider pusulası için minimum tutar (TL) zorunludur.");
        }

        var profile = await _db.BankSyncProfiles
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == req.BranchId && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("Vomsis ayarları bulunamadı. Önce Vomsis Ayarları sekmesinden kaydedin.");

        profile.AutoInstructionIncomingEnabled = req.AutoInstructionIncomingEnabled;
        profile.AutoInstructionIncomingMinAmount = req.AutoInstructionIncomingEnabled
            ? decimal.Round(req.AutoInstructionIncomingMinAmount!.Value, 2, MidpointRounding.AwayFromZero)
            : null;
        profile.AutoInstructionOutgoingEnabled = req.AutoInstructionOutgoingEnabled;
        profile.AutoInstructionOutgoingMinAmount = req.AutoInstructionOutgoingEnabled
            ? decimal.Round(req.AutoInstructionOutgoingMinAmount!.Value, 2, MidpointRounding.AwayFromZero)
            : null;

        await _db.SaveChangesAsync(ct);
        return ToDto(profile);
    }

    private void ApplyErpDefaults(BankSyncProfile profile, SaveBankSyncProfileReq req)
    {
        if (string.IsNullOrWhiteSpace(profile.ErpApiBaseUrl))
        {
            var fromReq = req.ErpApiBaseUrl?.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(fromReq))
                profile.ErpApiBaseUrl = fromReq;
            else
            {
                var fromConfig = (_config["BankSync:DefaultErpApiBaseUrl"] ?? _config["Hosting:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(fromConfig))
                    profile.ErpApiBaseUrl = fromConfig;
            }
        }

        if (string.IsNullOrWhiteSpace(profile.ErpApiAppKey))
        {
            if (!string.IsNullOrWhiteSpace(req.ErpApiAppKey))
                profile.ErpApiAppKey = req.ErpApiAppKey.Trim();
            else
            {
                var keys = _config.GetSection("Auth:AllowedKeys").Get<string[]>() ?? [];
                profile.ErpApiAppKey = keys.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
            }
        }
    }

    private static BankSyncProfileDto ToDto(BankSyncProfile profile) => new()
    {
        TenantId = profile.TenantId,
        BranchId = profile.BranchId,
        IsEnabled = profile.IsEnabled,
        VomsisAppKey = profile.VomsisAppKey,
        HasVomsisAppSecret = !string.IsNullOrWhiteSpace(profile.VomsisAppSecret),
        ErpApiBaseUrl = profile.ErpApiBaseUrl,
        HasErpApiAppKey = !string.IsNullOrWhiteSpace(profile.ErpApiAppKey),
        PollIntervalMinutes = profile.PollIntervalMinutes,
        AllowedAccountIds = profile.AllowedAccountIds,
        LookbackDays = profile.LookbackDays,
        ManualSyncRequestedUtc = profile.ManualSyncRequestedUtc,
        LastWorkerSyncUtc = profile.LastWorkerSyncUtc,
        LastWorkerSyncFetched = profile.LastWorkerSyncFetched,
        LastWorkerSyncImported = profile.LastWorkerSyncImported,
        LastWorkerSyncMessage = profile.LastWorkerSyncMessage,
        AutoInstructionIncomingEnabled = profile.AutoInstructionIncomingEnabled,
        AutoInstructionIncomingMinAmount = profile.AutoInstructionIncomingMinAmount,
        AutoInstructionOutgoingEnabled = profile.AutoInstructionOutgoingEnabled,
        AutoInstructionOutgoingMinAmount = profile.AutoInstructionOutgoingMinAmount
    };

    private static string NormalizeAccountIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "46";
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => int.TryParse(x, out var n) && n > 0)
            .Distinct()
            .ToList();
        return parts.Count == 0 ? "46" : string.Join(",", parts);
    }

    private static int[] ParseAccountIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [46];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .ToArray();
    }
}
