using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using KUYUMCU.Price_Service.Services;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Services;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/einvoice")]
[Authorize]
public sealed class EInvoiceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IEInvoiceWorkflowService _workflow;
    private readonly IEInvoiceProviderResolver _providerResolver;
    private readonly EInvoiceProfileSchemaEnsurer _einvoiceSchema;

    public EInvoiceController(
        AppDbContext db,
        ITenantContext tenant,
        IEInvoiceWorkflowService workflow,
        IEInvoiceProviderResolver providerResolver,
        EInvoiceProfileSchemaEnsurer einvoiceSchema)
    {
        _db = db;
        _tenant = tenant;
        _workflow = workflow;
        _providerResolver = providerResolver;
        _einvoiceSchema = einvoiceSchema;
    }

    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> GetProfile([FromQuery] Guid? branchId, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var bid = branchId ?? _tenant.BranchId;
        if (!bid.HasValue || bid.Value == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        await _einvoiceSchema.EnsureAsync(ct);

        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == bid.Value, ct);

        if (profile is null)
        {
            return Ok(new EInvoiceProfileDto
            {
                BranchId = bid.Value,
                ProviderCode = "edm",
                IsActive = false,
                DefaultInvoicePrefix = "AUR",
                DefaultArchivePrefix = "ARS",
                SpecialMatrahCraftedVatRatePercent = 20m,
                SpecialMatrahZiynetVatRatePercent = 20m,
                SalesInvoiceVatRatePercent = 20m,
                AutoDraftEnabled = true,
                AutoDraftMatchMode = "ANY"
            });
        }

        return Ok(ToDto(profile));
    }

    [HttpPut("profile")]
    [Authorize]
    public async Task<IActionResult> UpsertProfile([FromBody] SaveEInvoiceProfileReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        await _einvoiceSchema.EnsureAsync(ct);
        try
        {
            await _db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('EInvoiceProfiles', 'IntegratorCompanyCode') IS NOT NULL
BEGIN
    ALTER TABLE [dbo].[EInvoiceProfiles] ALTER COLUMN [IntegratorCompanyCode] nvarchar(max) NULL;
END", ct);
        }
        catch
        {
            // Kolon zaten uygunsa veya SQL yetkisi yoksa kaydı engelleme.
        }
        if (req.BranchId == Guid.Empty) return BadRequest(new { error = "BranchId zorunludur." });
        if (string.IsNullOrWhiteSpace(req.TaxNumber) || req.TaxNumber.Trim().Length < 10)
            return BadRequest(new { error = "Vergi numarası en az 10 hane olmalıdır." });
        if (string.IsNullOrWhiteSpace(req.TaxOffice))
            return BadRequest(new { error = "Vergi dairesi zorunludur." });
        if (string.IsNullOrWhiteSpace(req.CompanyName))
            return BadRequest(new { error = "Firma adı zorunludur." });
        if (IsSoleProprietorTaxNumber(req.TaxNumber) && string.IsNullOrWhiteSpace(req.SoleProprietorName))
            return BadRequest(new { error = "Şahıs firmaları için ad soyad zorunludur." });
        if (string.IsNullOrWhiteSpace(req.CompanyAddress))
            return BadRequest(new { error = "Firma adresi zorunludur." });
        if (string.IsNullOrWhiteSpace(req.IntegratorUsername))
            return BadRequest(new { error = "Entegratör kullanıcı adı zorunludur." });
        if (req.SpecialMatrahCraftedVatRatePercent < 0m || req.SpecialMatrahCraftedVatRatePercent > 100m)
            return BadRequest(new { error = "İşçilikli ürün KDV oranı 0-100 arasında olmalıdır." });
        if (req.SpecialMatrahZiynetVatRatePercent < 0m || req.SpecialMatrahZiynetVatRatePercent > 100m)
            return BadRequest(new { error = "Ziynet ürün KDV oranı 0-100 arasında olmalıdır." });
        if (req.SalesInvoiceVatRatePercent < 0m || req.SalesInvoiceVatRatePercent > 100m)
            return BadRequest(new { error = "Satış faturası KDV oranı 0-100 arasında olmalıdır." });
        if (req.AutoDraftMinTotal.HasValue && req.AutoDraftMaxTotal.HasValue &&
            req.AutoDraftMaxTotal.Value > 0m && req.AutoDraftMinTotal.Value > req.AutoDraftMaxTotal.Value)
            return BadRequest(new { error = "Otomatik taslak alt tutarı üst tutardan büyük olamaz." });
        var normalizedWorkmanshipRules = (req.WorkmanshipRules ?? [])
            .Select(x => new WorkmanshipRuleSetting
            {
                ProductType = x.ProductType,
                Karat = x.Karat,
                MinTotal = x.MinTotal,
                MaxTotal = x.MaxTotal,
                Percentage = x.Percentage
            })
            .ToList();

        foreach (var rule in normalizedWorkmanshipRules)
        {
            var productType = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipProductType(rule.ProductType);
            if (productType != EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCrafted &&
                productType != EInvoiceProfileSettingsCodec.WorkmanshipProductTypeZiynet &&
                productType != EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCollection)
                return BadRequest(new { error = "İşçilik kuralı ürün tipi geçersiz." });
            if (rule.MinTotal < 0m)
                return BadRequest(new { error = "İşçilik kuralı alt limit 0'dan küçük olamaz." });
            if (rule.MaxTotal <= 0m)
                return BadRequest(new { error = "İşçilik kuralı üst limit 0'dan büyük olmalıdır." });
            if (rule.MinTotal > rule.MaxTotal)
                return BadRequest(new { error = "İşçilik kuralı alt limit üst limitten büyük olamaz." });
            if (rule.Percentage < 0m || rule.Percentage > 100m)
                return BadRequest(new { error = "İşçilik yüzdesi 0-100 arasında olmalıdır." });
            var normalizedSelector = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipSelector(productType, rule.Karat);
            if (string.IsNullOrWhiteSpace(normalizedSelector))
            {
                var msg = string.Equals(productType, EInvoiceProfileSettingsCodec.WorkmanshipProductTypeZiynet, StringComparison.OrdinalIgnoreCase)
                    ? "Ziynet ürün seçimi geçersiz."
                    : string.Equals(productType, EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCollection, StringComparison.OrdinalIgnoreCase)
                        ? "Tahsilat kuralı geçersiz."
                        : "Ayar seçimi geçersiz. Sadece 24K, 22K, 18K, 14K, 8K desteklenir.";
                return BadRequest(new { error = msg });
            }
        }
        var overlapGroup = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipRules(normalizedWorkmanshipRules)
            .GroupBy(x => $"{EInvoiceProfileSettingsCodec.NormalizeWorkmanshipProductType(x.ProductType)}|{EInvoiceProfileSettingsCodec.NormalizeWorkmanshipSelector(x.ProductType, x.Karat)}",
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g =>
            {
                var ordered = g.OrderBy(x => x.MinTotal).ThenBy(x => x.MaxTotal).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    if (ordered[i].MinTotal <= ordered[i - 1].MaxTotal)
                        return true;
                }
                return false;
            });
        if (overlapGroup is not null)
            return BadRequest(new { error = "İşçilik kurallarında çakışan tutar aralıkları var. Aynı ürün tipi ve ayar için aralıklar üst üste gelemez." });

        var profile = await _db.EInvoiceProfiles.FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == req.BranchId, ct);
        var isNewProfile = profile is null;
        if (profile is null)
        {
            profile = new kuyumcu_domain.Entities.EInvoiceProfile
            {
                TenantId = tid,
                BranchId = req.BranchId
            };
            _db.EInvoiceProfiles.Add(profile);
        }

        var usernameChanged = !string.Equals(
            profile.IntegratorUsername?.Trim(),
            req.IntegratorUsername?.Trim(),
            StringComparison.OrdinalIgnoreCase);
        var hasStoredPassword = !string.IsNullOrWhiteSpace(profile.IntegratorSecretRef);
        if (string.IsNullOrWhiteSpace(req.IntegratorPassword) && (isNewProfile || !hasStoredPassword || usernameChanged))
        {
            return BadRequest(new
            {
                error = "EDM şifresi zorunludur. Kullanıcı adı değiştiyse veya ilk kayıtsa şifreyi tekrar girin."
            });
        }

        profile.ProviderCode = string.IsNullOrWhiteSpace(req.ProviderCode) ? "edm" : req.ProviderCode.Trim().ToLowerInvariant();
        profile.CompanyName = req.CompanyName.Trim();
        profile.SoleProprietorName = string.IsNullOrWhiteSpace(req.SoleProprietorName) ? null : req.SoleProprietorName.Trim();
        profile.CompanyAddress = req.CompanyAddress.Trim();
        profile.TaxNumber = req.TaxNumber.Trim();
        profile.TaxOffice = req.TaxOffice.Trim();
        profile.SenderLabel = req.SenderLabel?.Trim();
        profile.DefaultInvoicePrefix = string.IsNullOrWhiteSpace(req.DefaultInvoicePrefix) ? "AUR" : req.DefaultInvoicePrefix.Trim().ToUpperInvariant();
        profile.DefaultArchivePrefix = string.IsNullOrWhiteSpace(req.DefaultArchivePrefix) ? "ARS" : req.DefaultArchivePrefix.Trim().ToUpperInvariant();
        profile.IntegratorUsername = req.IntegratorUsername?.Trim();
        if (!string.IsNullOrWhiteSpace(req.IntegratorPassword))
            profile.IntegratorSecretRef = req.IntegratorPassword.Trim();
        profile.IsActive = req.IsActive;
        profile.IntegratorCompanyCode = EInvoiceProfileSettingsCodec.Encode(new EInvoiceProfileSettings
        {
            SpecialMatrahCraftedVatRatePercent = req.SpecialMatrahCraftedVatRatePercent,
            SpecialMatrahZiynetVatRatePercent = req.SpecialMatrahZiynetVatRatePercent,
            SalesInvoiceVatRatePercent = req.SalesInvoiceVatRatePercent,
            AutoDraftEnabled = req.AutoDraftEnabled,
            AutoDraftMatchMode = req.AutoDraftMatchMode,
            AutoDraftAllowedPaymentMethods = req.AutoDraftAllowedPaymentMethods ?? [],
            AutoDraftMinTotal = req.AutoDraftMinTotal,
            AutoDraftMaxTotal = req.AutoDraftMaxTotal,
            WorkmanshipRules = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipRules(normalizedWorkmanshipRules)
        });

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(profile));
    }

    [HttpPut("profile/invoice-settings")]
    [Authorize]
    public async Task<IActionResult> UpsertInvoiceSettings([FromBody] SaveInvoiceSettingsReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        await _einvoiceSchema.EnsureAsync(ct);
        try
        {
            await _db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('EInvoiceProfiles', 'IntegratorCompanyCode') IS NOT NULL
BEGIN
    ALTER TABLE [dbo].[EInvoiceProfiles] ALTER COLUMN [IntegratorCompanyCode] nvarchar(max) NULL;
END", ct);
        }
        catch
        {
            // Kolon zaten uygunsa kaydı engelleme.
        }

        if (req.BranchId == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });
        if (req.SpecialMatrahCraftedVatRatePercent < 0m || req.SpecialMatrahCraftedVatRatePercent > 100m)
            return BadRequest(new { error = "İşçilikli ürün KDV oranı 0-100 arasında olmalıdır." });
        if (req.SpecialMatrahZiynetVatRatePercent < 0m || req.SpecialMatrahZiynetVatRatePercent > 100m)
            return BadRequest(new { error = "Ziynet ürün KDV oranı 0-100 arasında olmalıdır." });
        if (req.SalesInvoiceVatRatePercent < 0m || req.SalesInvoiceVatRatePercent > 100m)
            return BadRequest(new { error = "Satış faturası KDV oranı 0-100 arasında olmalıdır." });
        if (req.AutoDraftMinTotal.HasValue && req.AutoDraftMaxTotal.HasValue &&
            req.AutoDraftMaxTotal.Value > 0m && req.AutoDraftMinTotal.Value > req.AutoDraftMaxTotal.Value)
            return BadRequest(new { error = "Otomatik taslak alt tutarı üst tutardan büyük olamaz." });

        var normalizedWorkmanshipRules = (req.WorkmanshipRules ?? [])
            .Select(x => new WorkmanshipRuleSetting
            {
                ProductType = x.ProductType,
                Karat = x.Karat,
                MinTotal = x.MinTotal,
                MaxTotal = x.MaxTotal,
                Percentage = x.Percentage
            })
            .ToList();

        foreach (var rule in normalizedWorkmanshipRules)
        {
            var productType = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipProductType(rule.ProductType);
            if (productType != EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCrafted &&
                productType != EInvoiceProfileSettingsCodec.WorkmanshipProductTypeZiynet &&
                productType != EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCollection)
                return BadRequest(new { error = "İşçilik kuralı ürün tipi geçersiz." });
            if (rule.MinTotal < 0m)
                return BadRequest(new { error = "İşçilik kuralı alt limit 0'dan küçük olamaz." });
            if (rule.MaxTotal <= 0m)
                return BadRequest(new { error = "İşçilik kuralı üst limit 0'dan büyük olmalıdır." });
            if (rule.MinTotal > rule.MaxTotal)
                return BadRequest(new { error = "İşçilik kuralı alt limit üst limitten büyük olamaz." });
            if (rule.Percentage < 0m || rule.Percentage > 100m)
                return BadRequest(new { error = "İşçilik yüzdesi 0-100 arasında olmalıdır." });
            var normalizedSelector = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipSelector(productType, rule.Karat);
            if (string.IsNullOrWhiteSpace(normalizedSelector))
            {
                var msg = string.Equals(productType, EInvoiceProfileSettingsCodec.WorkmanshipProductTypeZiynet, StringComparison.OrdinalIgnoreCase)
                    ? "Ziynet ürün seçimi geçersiz."
                    : string.Equals(productType, EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCollection, StringComparison.OrdinalIgnoreCase)
                        ? "Tahsilat kuralı geçersiz."
                        : "Ayar seçimi geçersiz. Sadece 24K, 22K, 18K, 14K, 8K desteklenir.";
                return BadRequest(new { error = msg });
            }
        }

        var overlapGroup = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipRules(normalizedWorkmanshipRules)
            .GroupBy(x => $"{EInvoiceProfileSettingsCodec.NormalizeWorkmanshipProductType(x.ProductType)}|{EInvoiceProfileSettingsCodec.NormalizeWorkmanshipSelector(x.ProductType, x.Karat)}",
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g =>
            {
                var ordered = g.OrderBy(x => x.MinTotal).ThenBy(x => x.MaxTotal).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    if (ordered[i].MinTotal <= ordered[i - 1].MaxTotal)
                        return true;
                }
                return false;
            });
        if (overlapGroup is not null)
            return BadRequest(new { error = "İşçilik kurallarında çakışan tutar aralıkları var. Aynı ürün tipi ve ayar için aralıklar üst üste gelemez." });

        var profile = await _db.EInvoiceProfiles.FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == req.BranchId, ct);
        if (profile is null)
        {
            profile = new kuyumcu_domain.Entities.EInvoiceProfile
            {
                TenantId = tid,
                BranchId = req.BranchId,
                ProviderCode = "edm",
                CompanyName = "-",
                CompanyAddress = "-",
                TaxNumber = "0000000000",
                TaxOffice = "-",
                DefaultInvoicePrefix = "AUR",
                DefaultArchivePrefix = "ARS",
                IsActive = false
            };
            _db.EInvoiceProfiles.Add(profile);
        }

        profile.IntegratorCompanyCode = EInvoiceProfileSettingsCodec.Encode(new EInvoiceProfileSettings
        {
            SpecialMatrahCraftedVatRatePercent = req.SpecialMatrahCraftedVatRatePercent,
            SpecialMatrahZiynetVatRatePercent = req.SpecialMatrahZiynetVatRatePercent,
            SalesInvoiceVatRatePercent = req.SalesInvoiceVatRatePercent,
            AutoDraftEnabled = req.AutoDraftEnabled,
            AutoDraftMatchMode = req.AutoDraftMatchMode,
            AutoDraftAllowedPaymentMethods = req.AutoDraftAllowedPaymentMethods ?? [],
            AutoDraftMinTotal = req.AutoDraftMinTotal,
            AutoDraftMaxTotal = req.AutoDraftMaxTotal,
            WorkmanshipRules = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipRules(normalizedWorkmanshipRules)
        });

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(profile));
    }

    [HttpPost("profile/test-connection")]
    [Authorize]
    public async Task<IActionResult> TestConnection([FromBody] TestEInvoiceConnectionReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var providerCode = string.IsNullOrWhiteSpace(req.ProviderCode) ? "edm" : req.ProviderCode.Trim().ToLowerInvariant();
        var adapter = _providerResolver.Resolve(providerCode);

        // Test, gönderimle AYNI kimlik bilgilerini kullanmalı. Alanlar boşsa kayıtlı profil değerlerine düşülür;
        // böylece "test başarılı" mesajı, gönderimde kullanılacak gerçek kimlik bilgisini doğrular.
        var savedProfile = req.BranchId != Guid.Empty
            ? await _db.EInvoiceProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == _tenant.TenantId && x.BranchId == req.BranchId, ct)
            : null;
        var effectiveUsername = !string.IsNullOrWhiteSpace(req.IntegratorUsername)
            ? req.IntegratorUsername.Trim()
            : savedProfile?.IntegratorUsername;
        var effectivePassword = !string.IsNullOrWhiteSpace(req.IntegratorPassword)
            ? req.IntegratorPassword.Trim()
            : savedProfile?.IntegratorSecretRef;

        var savedProvider = savedProfile?.ProviderCode?.Trim().ToLowerInvariant() ?? "";
        var providerSwitched = !string.IsNullOrWhiteSpace(savedProvider)
            && !string.Equals(savedProvider, providerCode, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(effectiveUsername)
            && string.IsNullOrWhiteSpace(effectivePassword))
        {
            var integratorLabel = string.Equals(providerCode, "uyumsoft", StringComparison.OrdinalIgnoreCase) ? "Uyumsoft" : "EDM";
            return Ok(new kuyumcu_application.Abstractions.EInvoiceConnectionTestResult(
                false,
                $"{integratorLabel} şifresi girilmedi. Kullanıcı adı değiştiyse, entegratör değiştiyse veya ilk kayıtsa şifreyi tekrar girin."));
        }

        if (providerSwitched && string.IsNullOrWhiteSpace(req.IntegratorPassword))
        {
            var integratorLabel = string.Equals(providerCode, "uyumsoft", StringComparison.OrdinalIgnoreCase) ? "Uyumsoft" : "EDM";
            return Ok(new kuyumcu_application.Abstractions.EInvoiceConnectionTestResult(
                false,
                $"Entegratör {savedProvider} → {providerCode} olarak değiştirildi. {integratorLabel} web servis şifresini girip tekrar deneyin."));
        }

        if (!string.IsNullOrWhiteSpace(req.IntegratorUsername)
            && string.IsNullOrWhiteSpace(req.IntegratorPassword)
            && savedProfile is not null
            && !string.Equals(req.IntegratorUsername.Trim(), savedProfile.IntegratorUsername, StringComparison.OrdinalIgnoreCase))
        {
            var integratorLabel = string.Equals(providerCode, "uyumsoft", StringComparison.OrdinalIgnoreCase) ? "Uyumsoft" : "EDM";
            return Ok(new kuyumcu_application.Abstractions.EInvoiceConnectionTestResult(
                false,
                $"Kullanıcı adı değiştirildi. {integratorLabel} şifresini girin; kayıtlı şifre eski kullanıcı adına aittir."));
        }

        var res = await adapter.TestConnectionAsync(new kuyumcu_application.Abstractions.EInvoiceConnectionTestRequest(
            _tenant.TenantId,
            req.BranchId,
            providerCode,
            effectiveUsername,
            effectivePassword,
            req.TaxNumber?.Trim() ?? "",
            req.TaxOffice?.Trim() ?? "",
            req.CompanyAddress?.Trim() ?? ""), ct);

        if (res.IsSuccess
            && string.Equals(providerCode, "edm", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(effectiveUsername)
            && !string.IsNullOrWhiteSpace(effectivePassword))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await adapter.WarmTaxpayerSearchCacheAsync(
                        effectiveUsername,
                        effectivePassword,
                        CancellationToken.None);
                }
                catch
                {
                    // Arka plan GİB listesi ısıtması başarısız olabilir; bağlantı testi yine başarılı kalır.
                }
            }, CancellationToken.None);
        }

        if (res.IsSuccess
            && string.Equals(providerCode, "uyumsoft", StringComparison.OrdinalIgnoreCase)
            && req.BranchId != Guid.Empty)
        {
            var trackedProfile = await _db.EInvoiceProfiles
                .FirstOrDefaultAsync(x => x.TenantId == _tenant.TenantId && x.BranchId == req.BranchId, ct);
            if (trackedProfile is not null)
            {
                await SyncUyumsoftSeriesToProfileAsync(
                    trackedProfile,
                    adapter,
                    effectiveUsername,
                    effectivePassword,
                    ct);
            }
        }

        // WPF tarafında başarısız testin detay mesajını gösterebilmek için her durumda 200 döndürüyoruz.
        return Ok(res);
    }

    private async Task SyncUyumsoftSeriesToProfileAsync(
        EInvoiceProfile profile,
        IEInvoiceProviderAdapter adapter,
        string? username,
        string? password,
        CancellationToken ct)
    {
        var year = DateTime.Now.Year;
        var settings = EInvoiceProfileSettingsCodec.Decode(profile.IntegratorCompanyCode);
        var syncTargets = new[]
        {
            (Prefix: kuyumcu_application.GibInvoiceNumber.ResolvePrefixForDocumentType(
                "EArsiv", profile.DefaultInvoicePrefix, profile.DefaultArchivePrefix), IsEArchive: true),
            (Prefix: kuyumcu_application.GibInvoiceNumber.ResolvePrefixForDocumentType(
                "EFatura", profile.DefaultInvoicePrefix, profile.DefaultArchivePrefix), IsEArchive: false)
        };

        foreach (var target in syncTargets.DistinctBy(x => x.Prefix, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                var syncResult = await adapter.QuerySeriesCounterAsync(
                    username,
                    password,
                    target.Prefix,
                    year,
                    target.IsEArchive,
                    timeoutCts.Token);
                if (syncResult.IsSuccess && syncResult.LastSerial.HasValue)
                {
                    EInvoiceProfileSettingsCodec.SetSeriesLastSerial(
                        settings,
                        kuyumcu_application.GibInvoiceNumber.BuildFromSerial(
                            target.Prefix,
                            new DateTime(year, 1, 1),
                            syncResult.LastSerial.Value));
                }
            }
            catch (OperationCanceledException)
            {
                // Portal yavaşsa profil sayacı korunur.
            }
        }

        profile.IntegratorCompanyCode = EInvoiceProfileSettingsCodec.Encode(settings);
        await _db.SaveChangesAsync(ct);
    }

    [HttpPost("uyumsoft/sync-series")]
    [Authorize]
    public async Task<IActionResult> SyncUyumsoftSeries([FromBody] SyncUyumsoftSeriesReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        if (req is null)
            return BadRequest(new { error = "İstek boş olamaz." });

        var tid = _tenant.TenantId;
        var bid = req.BranchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty)
            return BadRequest(new { error = "Şube seçilmedi." });

        var profile = await _db.EInvoiceProfiles
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == bid, ct);
        if (profile is null)
            return NotFound(new { error = "E-Fatura profili bulunamadı." });

        var providerCode = string.IsNullOrWhiteSpace(profile.ProviderCode) ? "edm" : profile.ProviderCode.Trim().ToLowerInvariant();
        if (!string.Equals(providerCode, "uyumsoft", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Seri senkronizasyonu yalnızca Uyumsoft profilleri için kullanılabilir." });

        var year = req.Year.GetValueOrDefault(DateTime.Now.Year);
        var isEArchive = req.IsEArchive.GetValueOrDefault(true);
        var prefix = kuyumcu_application.GibInvoiceNumber.ResolvePrefixForDocumentType(
            isEArchive ? "EArsiv" : "EFatura",
            req.InvoiceSeriesPrefix ?? profile.DefaultInvoicePrefix,
            req.ArchiveSeriesPrefix ?? profile.DefaultArchivePrefix);

        var adapter = _providerResolver.Resolve(providerCode);
        var syncResult = await adapter.QuerySeriesCounterAsync(
            profile.IntegratorUsername,
            profile.IntegratorSecretRef,
            prefix,
            year,
            isEArchive,
            ct);

        if (!syncResult.IsSuccess || !syncResult.LastSerial.HasValue)
        {
            return Ok(new
            {
                success = false,
                prefix,
                year,
                message = syncResult.ErrorMessage ?? "Uyumsoft portalından seri numarası alınamadı."
            });
        }

        var settings = EInvoiceProfileSettingsCodec.Decode(profile.IntegratorCompanyCode);
        EInvoiceProfileSettingsCodec.SetSeriesLastSerial(
            settings,
            kuyumcu_application.GibInvoiceNumber.BuildFromSerial(prefix, new DateTime(year, 1, 1), syncResult.LastSerial.Value));
        profile.IntegratorCompanyCode = EInvoiceProfileSettingsCodec.Encode(settings);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            success = true,
            prefix,
            year,
            lastSerial = syncResult.LastSerial,
            nextInvoiceNumber = syncResult.NextInvoiceNumber,
            message = $"Uyumsoft portalından son numara okundu: {syncResult.LastSerial}. Sıradaki: {syncResult.NextInvoiceNumber}"
        });
    }

    public sealed class SyncUyumsoftSeriesReq
    {
        public Guid? BranchId { get; set; }
        public int? Year { get; set; }
        public bool? IsEArchive { get; set; }
        public string? InvoiceSeriesPrefix { get; set; }
        public string? ArchiveSeriesPrefix { get; set; }
    }

    public sealed class CreateCollectionDraftReq
    {
        public Guid? BranchId { get; set; }
        public Guid? CustomerId { get; set; }
        public Guid? SupplierId { get; set; }
        public decimal AmountTl { get; set; }
        public string? Description { get; set; }
        public DateTime? TxDate { get; set; }
    }

    /// <summary>
    /// Tahsilat kaynaklı (satışsız) has altın taslak faturası oluşturur. Alıcı bilgileri
    /// müşteri/tedarikçi kaydından çözülür. Ödeme yöntemi Banka/Havale olan tahsilatlarda kullanılır.
    /// </summary>
    [HttpPost("collection-draft")]
    public async Task<IActionResult> CreateCollectionDraft([FromBody] CreateCollectionDraftReq req, CancellationToken ct)
    {
        if (req is null) return BadRequest(new { error = "İstek boş olamaz." });
        var tid = _tenant.TenantId;
        var bid = req.BranchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty) return BadRequest(new { error = "BranchId zorunludur." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && bid != _tenant.BranchId.Value)
            return BadRequest(new { error = "İşlem şubesi, oturum şubesi ile aynı olmalıdır." });
        if (req.AmountTl <= 0m) return BadRequest(new { error = "Tutar 0'dan büyük olmalıdır." });

        string buyerName = "";
        string? buyerTax = null, buyerAddress = null, buyerCity = null, buyerDistrict = null, buyerEmail = null;
        Guid? customerId = null;

        if (req.CustomerId.HasValue && req.CustomerId.Value != Guid.Empty)
        {
            var c = await _db.Customers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == req.CustomerId.Value && x.TenantId == tid && !x.IsDeleted, ct);
            if (c is null) return BadRequest(new { error = "Müşteri bulunamadı." });
            customerId = c.Id;
            buyerName = c.FullName;
            buyerTax = c.NationalId;
            buyerAddress = c.Address;
            buyerCity = c.City;
            buyerDistrict = c.District;
            buyerEmail = c.Email;
        }
        else if (req.SupplierId.HasValue && req.SupplierId.Value != Guid.Empty)
        {
            var s = await _db.Suppliers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == req.SupplierId.Value && x.TenantId == tid && !x.IsDeleted, ct);
            if (s is null) return BadRequest(new { error = "Tedarikçi bulunamadı." });
            buyerName = string.IsNullOrWhiteSpace(s.CompanyName) ? (s.ContactName ?? "") : s.CompanyName;
            buyerTax = s.TaxNumber;
            buyerAddress = s.Address;
            buyerCity = s.City;
            buyerDistrict = s.District;
            buyerEmail = s.Email;
        }
        else
        {
            return BadRequest(new { error = "Müşteri veya tedarikçi belirtilmelidir." });
        }

        try
        {
            var (invoiceId, documentId) = await _workflow.CreateCollectionDraftAsync(new CollectionDraftInput(
                tid,
                bid,
                customerId,
                buyerName,
                buyerTax,
                buyerAddress,
                buyerCity,
                buyerDistrict,
                buyerEmail,
                req.AmountTl,
                req.Description,
                (req.TxDate ?? DateTime.UtcNow).ToUniversalTime(),
                null), ct);
            return Ok(new { invoiceId, documentId });
        }
        catch (DbUpdateException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            return BadRequest(new { error = detail });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("outgoing")]
    public async Task<IActionResult> ListOutgoing(
        [FromQuery] Guid? branchId,
        [FromQuery] string? status,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery, Range(1, 1000)] int page = 1,
        [FromQuery, Range(1, 500)] int pageSize = 50,
        CancellationToken ct = default)
    {
        var tid = _tenant.TenantId;
        var q = _db.EInvoiceDocuments
            .AsNoTracking()
            .Where(x => x.TenantId == tid && x.Direction == "Outgoing");

        if (branchId.HasValue && branchId.Value != Guid.Empty)
            q = q.Where(x => x.BranchId == branchId.Value);
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(x => x.Status == status);
        if (from.HasValue)
            q = q.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue)
            q = q.Where(x => x.CreatedAt <= to.Value);

        var total = await q.CountAsync(ct);
        var docs = await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.InvoiceId,
                x.BranchId,
                BranchName = x.Branch != null ? (x.Branch.Name ?? "") : "",
                x.DocumentType,
                x.InvoiceNumber,
                x.Status,
                x.GrandTotal,
                x.Currency,
                x.RetryCount,
                x.LastError,
                x.IntegratorDocumentId,
                x.Uuid,
                x.Ettn,
                x.SubmittedAt,
                x.DeliveredAt,
                x.CancelledAt,
                x.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(new
        {
            total,
            page,
            pageSize,
            items = docs
        });
    }

    [HttpGet("incoming")]
    public async Task<IActionResult> ListIncoming(
        [FromQuery] Guid? branchId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery, Range(1, 1000)] int page = 1,
        [FromQuery, Range(1, 500)] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var q = _db.IncomingEInvoices.AsNoTracking().Where(x => x.TenantId == tid);

        if (branchId.HasValue && branchId.Value != Guid.Empty)
            q = q.Where(x => x.BranchId == branchId.Value);
        if (from.HasValue)
            q = q.Where(x => (x.IssueDate ?? x.FetchedAt) >= from.Value.Date);
        if (to.HasValue)
        {
            var toExclusive = to.Value.Date.AddDays(1);
            q = q.Where(x => (x.IssueDate ?? x.FetchedAt) < toExclusive);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.IssueDate ?? x.FetchedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.Uuid,
                x.InvoiceNumber,
                x.SenderName,
                x.SenderTaxNumber,
                x.DocumentType,
                x.Status,
                x.StatusDescription,
                x.PayableAmount,
                x.Currency,
                x.IssueDate,
                x.FetchedAt,
                x.ReceiverName,
                x.ReceiverTaxNumber,
                x.GibStatusDescription,
                x.ProfileId
            })
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("incoming/{id:guid}")]
    public async Task<IActionResult> GetIncoming(Guid id, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var doc = await _db.IncomingEInvoices.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (doc is null) return NotFound();
        return Ok(doc);
    }

    [HttpPost("incoming/sync")]
    public async Task<IActionResult> SyncIncoming(
        [FromQuery] Guid? branchId,
        [FromQuery] int days = 365,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var bid = branchId ?? _tenant.BranchId;
        if (!bid.HasValue || bid.Value == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var profile = await _db.EInvoiceProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == bid.Value && !x.IsDeleted, ct);
        if (profile is null)
            return BadRequest(new { error = "Bu şube için e-fatura entegrasyon profili bulunamadı. Önce ayarları kaydedin." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorUsername))
            return BadRequest(new { error = "EDM kullanıcı adı zorunludur." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorSecretRef))
            return BadRequest(new { error = "EDM şifresi zorunludur." });

        var adapter = _providerResolver.Resolve(string.IsNullOrWhiteSpace(profile.ProviderCode) ? "edm" : profile.ProviderCode);
        var endDate = (to ?? DateTime.Now).Date;
        var clampedDays = Math.Clamp(days, 1, 3650);
        var startDate = from?.Date ?? endDate.AddDays(-clampedDays);
        if (startDate > endDate)
            (startDate, endDate) = (endDate, startDate);

        var startUtc = DateTime.SpecifyKind(startDate, DateTimeKind.Local).ToUniversalTime();
        var endUtc = DateTime.SpecifyKind(endDate.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime();

        var result = await adapter.GetIncomingInvoicesAsync(
            new EInvoiceIncomingRequest(tid, bid.Value, startUtc, endUtc, 5000, profile.IntegratorUsername, profile.IntegratorSecretRef), ct);
        if (!result.IsSuccess)
        {
            var errMsg = result.ErrorMessage ?? "Gelen faturalar alınamadı.";
            // Raw EDM yanıtı varsa kısa önizlemeyi hata mesajına ekle
            if (!string.IsNullOrWhiteSpace(result.RawResponse))
            {
                var preview = result.RawResponse.Length > 600 ? result.RawResponse[..600] + "…" : result.RawResponse;
                errMsg += $" [EDM yanıtı: {preview}]";
            }
            return BadRequest(new { error = errMsg });
        }

        var added = 0;
        var updated = 0;
        foreach (var item in result.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Uuid)) continue;
            var existing = await _db.IncomingEInvoices.FirstOrDefaultAsync(x => x.TenantId == tid && x.Uuid == item.Uuid, ct);
            if (existing is null)
            {
                _db.IncomingEInvoices.Add(NewIncomingEInvoice(tid, bid.Value, item));
                added++;
            }
            else
            {
                existing.Status = Trunc(item.Status, 64);
                existing.StatusDescription = Trunc(item.StatusDescription, 400);
                existing.PayableAmount = item.PayableAmount;
                existing.Currency = string.IsNullOrWhiteSpace(item.Currency) ? existing.Currency : item.Currency;
                if (!string.IsNullOrWhiteSpace(item.SenderName)) existing.SenderName = Trunc(item.SenderName, 400);
                if (!string.IsNullOrWhiteSpace(item.ReceiverName)) existing.ReceiverName = Trunc(item.ReceiverName, 400);
                if (!string.IsNullOrWhiteSpace(item.ReceiverTaxNumber)) existing.ReceiverTaxNumber = Trunc(item.ReceiverTaxNumber, 16);
                if (!string.IsNullOrWhiteSpace(item.GibStatusDescription)) existing.GibStatusDescription = Trunc(item.GibStatusDescription, 400);
                if (!string.IsNullOrWhiteSpace(item.ProfileId)) existing.ProfileId = Trunc(item.ProfileId, 64);
                existing.FetchedAt = DateTime.UtcNow;
                updated++;
            }
        }
        await _db.SaveChangesAsync(ct);

        return Ok(new { added, updated, total = result.Items.Count });
    }

    /// <summary>
    /// Şubeye ait gelen fatura (IncomingEInvoice) kayıtlarını temizler ve EDM'den taze senkronizasyon yapar.
    /// </summary>
    [HttpPost("incoming/clear-and-sync")]
    public async Task<IActionResult> ClearAndSyncIncoming(
        [FromQuery] Guid? branchId,
        [FromQuery] int days = 365,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var bid = branchId ?? _tenant.BranchId;
        if (!bid.HasValue || bid.Value == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        // Mevcut kayıtları sil
        var existing = await _db.IncomingEInvoices
            .Where(x => x.TenantId == tid && x.BranchId == bid.Value)
            .ToListAsync(ct);
        _db.IncomingEInvoices.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);
        var deleted = existing.Count;

        var profile = await _db.EInvoiceProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == bid.Value && !x.IsDeleted, ct);
        if (profile is null)
            return BadRequest(new { error = "EDM profili bulunamadı." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorUsername) || string.IsNullOrWhiteSpace(profile.IntegratorSecretRef))
            return BadRequest(new { error = "EDM kullanıcı adı/şifresi eksik." });

        var adapter = _providerResolver.Resolve(string.IsNullOrWhiteSpace(profile.ProviderCode) ? "edm" : profile.ProviderCode);
        var endDate = (to ?? DateTime.Now).Date;
        var startDate = from?.Date ?? endDate.AddDays(-Math.Clamp(days, 1, 3650));
        var startUtc = DateTime.SpecifyKind(startDate, DateTimeKind.Local).ToUniversalTime();
        var endUtc = DateTime.SpecifyKind(endDate.AddDays(1).AddTicks(-1), DateTimeKind.Local).ToUniversalTime();

        var result = await adapter.GetIncomingInvoicesAsync(
            new EInvoiceIncomingRequest(tid, bid.Value, startUtc, endUtc, 5000, profile.IntegratorUsername, profile.IntegratorSecretRef), ct);
        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorMessage ?? "EDM sorgu hatası.", deleted });

        var added = 0;
        foreach (var item in result.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Uuid)) continue;
            _db.IncomingEInvoices.Add(NewIncomingEInvoice(tid, bid.Value, item));
            added++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted, added, total = result.Items.Count });
    }

    private IncomingEInvoice NewIncomingEInvoice(Guid tid, Guid bid, EInvoiceIncomingItem item) => new()
    {
        TenantId = tid,
        BranchId = bid,
        Uuid = item.Uuid,
        InvoiceNumber = item.InvoiceNumber ?? "",
        SenderName = Trunc(item.SenderName, 400),
        SenderTaxNumber = Trunc(item.SenderTaxNumber, 16),
        DocumentType = string.IsNullOrWhiteSpace(item.DocumentType) ? "EFatura" : item.DocumentType,
        Status = Trunc(item.Status, 64),
        StatusDescription = Trunc(item.StatusDescription, 400),
        PayableAmount = item.PayableAmount,
        Currency = string.IsNullOrWhiteSpace(item.Currency) ? "TRY" : item.Currency,
        IssueDate = item.IssueDate,
        EnvelopeIdentifier = Trunc(item.EnvelopeIdentifier, 128),
        ReceiverName = Trunc(item.ReceiverName, 400),
        ReceiverTaxNumber = Trunc(item.ReceiverTaxNumber, 16),
        GibStatusDescription = Trunc(item.GibStatusDescription, 400),
        ProfileId = Trunc(item.ProfileId, 64),
        RawContent = item.RawContent,
        FetchedAt = DateTime.UtcNow
    };

    private static string Trunc(string? value, int maxLen)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length <= maxLen ? v : v[..maxLen];
    }

    [HttpGet("outgoing/{id:guid}")]
    public async Task<IActionResult> GetOutgoing(Guid id, CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (doc is null) return NotFound();

        var outboxes = await _db.EInvoiceOutboxes.AsNoTracking()
            .Where(x => x.TenantId == tid && x.DocumentId == id)
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == doc.BranchId, ct);
        var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile.ProviderCode.Trim().ToLowerInvariant();

        return Ok(new
        {
            document = doc,
            providerCode,
            outbox = outboxes
        });
    }

    [HttpGet("outgoing/{id:guid}/ubl-preview")]
    [Authorize]
    public async Task<IActionResult> GetOutgoingUblPreview(Guid id, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (doc is null) return NotFound(new { error = "Belge bulunamadı." });

        var payload = await _db.EInvoiceOutboxes.AsNoTracking()
            .Where(x => x.TenantId == tid && x.DocumentId == id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            return BadRequest(new { error = "UBL payload bulunamadı. Belge için henüz gönderim kuyruğu oluşturulmamış olabilir." });

        var ublXml = ExtractUblXml(payload);
        if (string.IsNullOrWhiteSpace(ublXml))
            return BadRequest(new { error = "Payload içinde UBL bulunamadı (ublXml/ublBase64)." });

        var validation = ValidateUblRequiredProperties(ublXml);
        return Ok(new
        {
            documentId = doc.Id,
            doc.InvoiceNumber,
            doc.DocumentType,
            doc.Status,
            doc.IntegratorDocumentId,
            doc.Uuid,
            doc.Ettn,
            validation,
            ublXml
        });
    }

    [HttpPost("outgoing/{invoiceId:guid}/send")]
    [Authorize]
    public async Task<IActionResult> SendOutgoing(Guid invoiceId, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.InvoiceId == invoiceId, ct);
        if (doc is null) return NotFound(new { error = "Belge bulunamadı." });
        if (string.Equals(doc.Status, "Delivered", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Bu fatura zaten gönderildi ve onaylandı." });
        if (string.Equals(doc.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "İptal edilmiş fatura tekrar gönderilemez." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && doc.BranchId != _tenant.BranchId.Value)
            return BadRequest(new { error = "Seçili şube ile belge şubesi farklı. Lütfen belgeye ait şubeye geçin." });

        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == doc.BranchId, ct);
        if (profile is null)
            return BadRequest(new { error = "Bu şube için e-fatura entegrasyon profili bulunamadı. Önce ayarları kaydedin." });
        var credentialError = ValidateIntegratorCredentials(profile);
        if (credentialError is not null)
            return credentialError;

        var queued = await _workflow.QueueManualSendAsync(tid, invoiceId, null, ct);
        if (queued is null) return NotFound(new { error = "Belge bulunamadı." });
        return Ok(new { queued.Id, queued.InvoiceId, queued.Status, queued.RetryCount });
    }

    [HttpGet("outgoing/{invoiceId:guid}/send-preview")]
    [Authorize]
    public async Task<IActionResult> GetSendPreview(Guid invoiceId, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.InvoiceId == invoiceId, ct);
        if (doc is null) return NotFound(new { error = "Belge bulunamadı." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && doc.BranchId != _tenant.BranchId.Value)
            return BadRequest(new { error = "Seçili şube ile belge şubesi farklı. Lütfen belgeye ait şubeye geçin." });

        var draft = await _workflow.BuildManualDraftAsync(tid, invoiceId, ct);
        if (draft is null) return NotFound(new { error = "Önizleme verisi oluşturulamadı." });

        var branch = await _db.Branches.AsNoTracking()
            .Where(x => x.TenantId == tid && x.Id == doc.BranchId)
            .Select(x => new { x.Name, x.Address })
            .FirstOrDefaultAsync(ct);
        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == doc.BranchId, ct);

        return Ok(new
        {
            documentId = doc.Id,
            doc.InvoiceId,
            doc.InvoiceNumber,
            doc.Status,
            sender = new
            {
                branchName = branch?.Name,
                profile?.CompanyName,
                profile?.CompanyAddress,
                profile?.TaxNumber,
                profile?.TaxOffice,
                profile?.SenderLabel
            },
            draft
        });
    }

    [HttpPost("outgoing/{invoiceId:guid}/process-now")]
    [Authorize]
    public IActionResult ProcessOutgoingNow(Guid invoiceId)
    {
        if (!CanUseEInvoice())
            return Forbid();
        _workflow.ScheduleImmediateProcessing(_tenant.TenantId, invoiceId);
        return Ok(new { message = "Gönderim arka planda başlatıldı. Birkaç saniye sonra listeyi yenileyin." });
    }

    [HttpPost("outgoing/{invoiceId:guid}/send-preview")]
    [Authorize]
    public async Task<IActionResult> SendOutgoingPreview(Guid invoiceId, [FromBody] SendPreviewReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        if (req?.Draft is null)
            return BadRequest(new { error = "Önizleme bilgisi zorunludur." });
        var draft = req.Draft;
        if (string.IsNullOrWhiteSpace(draft.BuyerName))
            return BadRequest(new { error = "Alıcı adı/soyadı zorunludur." });
        var buyerTaxNo = new string((draft.BuyerTaxNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (buyerTaxNo.Length != 10 && buyerTaxNo.Length != 11)
            return BadRequest(new { error = "Alıcı TCKN/VKN 10 veya 11 hane olmalıdır." });
        if (draft.Lines is null || draft.Lines.Count == 0)
            return BadRequest(new { error = "Fatura satırı bulunamadı." });
        if (draft.Lines.Any(x => string.IsNullOrWhiteSpace(x.ProductName)))
            return BadRequest(new { error = "Satırlarda ürün adı zorunludur." });

        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.InvoiceId == invoiceId, ct);
        if (doc is null) return NotFound(new { error = "Belge bulunamadı." });
        if (string.Equals(doc.Status, "Delivered", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Bu fatura zaten gönderildi ve onaylandı." });
        if (string.Equals(doc.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "İptal edilmiş fatura tekrar gönderilemez." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && doc.BranchId != _tenant.BranchId.Value)
            return BadRequest(new { error = "Seçili şube ile belge şubesi farklı. Lütfen belgeye ait şubeye geçin." });

        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == doc.BranchId, ct);
        if (profile is null)
            return BadRequest(new { error = "Bu şube için e-fatura entegrasyon profili bulunamadı. Önce ayarları kaydedin." });
        var credentialError = ValidateIntegratorCredentials(profile);
        if (credentialError is not null)
            return credentialError;

        var normalizedDraft = draft with { BuyerTaxNumber = buyerTaxNo };
        var isEArchive = string.Equals(normalizedDraft.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase);
        if (isEArchive && !CanUseEArchive())
            return Forbid();
        if (!isEArchive && buyerTaxNo.Length != 10 && buyerTaxNo.Length != 11)
            return BadRequest(new { error = "e-Fatura için alıcı TCKN/VKN 10 veya 11 hane olmalıdır." });
        if (!isEArchive && string.IsNullOrWhiteSpace(normalizedDraft.BuyerEmail))
            return BadRequest(new { error = "e-Fatura için alıcı etiketi zorunludur. Alıcı E-Posta alanına EDM alıcı etiketi girin veya belge tipini E-Arşiv seçin." });

        var queued = await _workflow.QueueManualSendAsync(tid, invoiceId, normalizedDraft, ct);
        if (queued is null) return NotFound(new { error = "Belge bulunamadı." });
        return Ok(new { queued.Id, queued.InvoiceId, queued.Status, queued.RetryCount });
    }

    [HttpPost("outgoing/{id:guid}/cancel")]
    [Authorize]
    public async Task<IActionResult> CancelOutgoing(Guid id, [FromBody] CancelReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var ok = await _workflow.CancelDocumentAsync(tid, id, req.Reason ?? "Kullanıcı iptal talebi", ct);
        if (!ok) return BadRequest(new { error = "Belge iptal isteği gönderilemedi." });
        return Ok(new { success = true });
    }

    [HttpPost("outgoing/delete-selected")]
    [Authorize]
    public async Task<IActionResult> DeleteSelectedOutgoing([FromBody] DeleteSelectedReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var ids = (req.DocumentIds ?? [])
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return BadRequest(new { error = "Silinecek belge seçilmedi." });

        var selectedBranchId = _tenant.BranchId ?? Guid.Empty;
        var docs = new List<EInvoiceDocument>();
        var skippedByBranch = 0;
        foreach (var id in ids)
        {
            var doc = await _db.EInvoiceDocuments
                .FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
            if (doc is null || doc.IsDeleted)
                continue;
            if (selectedBranchId != Guid.Empty && doc.BranchId != selectedBranchId)
            {
                skippedByBranch++;
                continue;
            }
            docs.Add(doc);
        }
        if (docs.Count == 0)
            return Ok(new { deletedDocuments = 0, deletedOutboxes = 0, skippedByBranch });

        var deletedOutboxes = 0;
        foreach (var doc in docs)
        {
            doc.IsDeleted = true;
            var outboxes = await _db.EInvoiceOutboxes
                .Where(x => x.TenantId == tid && x.DocumentId == doc.Id && !x.IsDeleted)
                .ToListAsync(ct);
            foreach (var outbox in outboxes)
            {
                outbox.IsDeleted = true;
                deletedOutboxes++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            deletedDocuments = docs.Count,
            deletedOutboxes,
            skippedByBranch
        });
    }

    [HttpPost("manual/taxpayer-query")]
    [Authorize]
    public async Task<IActionResult> QueryManualTaxpayer([FromBody] ManualTaxpayerQueryReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var taxNo = NormalizeDigits(req.TaxNumber);
        var searchTitle = (req.Title ?? "").Trim();
        var hasValidTaxNo = taxNo.Length is 10 or 11;
        if (!hasValidTaxNo && searchTitle.Length < 3)
            return BadRequest(new { error = "TCKN/VKN 10 veya 11 hane olmalıdır ya da ünvan/ad soyad için en az 3 karakter girin." });

        var branchId = req.BranchId != Guid.Empty ? req.BranchId : (_tenant.BranchId ?? Guid.Empty);
        if (branchId == Guid.Empty)
            return BadRequest(new { error = "Şube seçimi bulunamadı." });

        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == _tenant.TenantId && x.BranchId == branchId, ct);
        if (profile is null)
            return BadRequest(new { error = "Bu şube için e-fatura entegrasyon profili bulunamadı. Önce E-Fatura ayarlarını kaydedin." });

        var providerCode = string.IsNullOrWhiteSpace(profile.ProviderCode) ? "edm" : profile.ProviderCode.Trim().ToLowerInvariant();
        var providerLabel = string.Equals(providerCode, "uyumsoft", StringComparison.OrdinalIgnoreCase) ? "Uyumsoft" : "EDM";
        if (string.IsNullOrWhiteSpace(profile.IntegratorUsername) || string.IsNullOrWhiteSpace(profile.IntegratorSecretRef))
            return BadRequest(new { error = $"{providerLabel} kullanıcı adı/şifresi eksik. E-Fatura ayarlarından kaydedin." });

        // TCKN/VKN yoksa önce ünvan ile ara; tek sonuç varsa doğrudan onunla devam et.
        var candidates = new List<ManualTaxpayerCandidate>();
        ManualTaxpayerCandidate? selectedFromTitleSearch = null;
        if (!hasValidTaxNo)
        {
            var searchAdapter = _providerResolver.Resolve(providerCode);
            IntegratorTaxpayerSearchResult searchResult;
            try
            {
                searchResult = await searchAdapter.SearchTaxpayersByTitleAsync(
                    profile.IntegratorUsername, profile.IntegratorSecretRef, searchTitle, ct);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"{providerLabel} ünvan araması başarısız: {ex.Message}" });
            }

            if (!searchResult.IsSuccess)
                return BadRequest(new { error = searchResult.Message ?? $"{providerLabel} ünvan araması başarısız." });

            candidates = searchResult.Candidates
                .Select(c => new ManualTaxpayerCandidate
                {
                    TaxNumber = c.TaxNo,
                    Title = c.Title ?? "",
                    ReceiverAlias = c.ReceiverAlias ?? "",
                    IsEInvoiceTaxpayer = c.IsEInvoiceTaxpayer,
                    DocumentType = c.IsEInvoiceTaxpayer ? "EFatura" : "EArsiv"
                })
                .ToList();

            if (candidates.Count == 0)
            {
                return Ok(new ManualTaxpayerQueryResponse
                {
                    TaxNumber = "",
                    Title = "",
                    Source = providerCode,
                    Message = searchResult.Message ?? $"{providerLabel}'de bu ünvan ile kayıt bulunamadı.",
                    Candidates = candidates
                });
            }

            // Birden fazla aday varsa seçim kullanıcıya bırakılır.
            if (candidates.Count > 1)
            {
                return Ok(new ManualTaxpayerQueryResponse
                {
                    TaxNumber = "",
                    Title = "",
                    Source = providerCode,
                    Message = $"{candidates.Count} kayıt bulundu. Listeden seçim yapın.",
                    Candidates = candidates
                });
            }

            selectedFromTitleSearch = candidates[0];
            taxNo = selectedFromTitleSearch.TaxNumber;
        }

        var title = selectedFromTitleSearch?.Title?.Trim() ?? "";
        string? receiverAlias = string.IsNullOrWhiteSpace(selectedFromTitleSearch?.ReceiverAlias)
            ? null
            : selectedFromTitleSearch!.ReceiverAlias!.Trim();
        var documentType = selectedFromTitleSearch?.DocumentType
            ?? (taxNo.Length == 10 ? "EFatura" : "EArsiv");
        var source = selectedFromTitleSearch is not null ? providerCode : "rule";
        var message = selectedFromTitleSearch is not null
            ? $"{providerLabel} ünvan araması ile mükellef bulundu."
            : "Mükellefiyet, TCKN/VKN kuralına göre tahmin edildi.";

        try
        {
            var adapter = _providerResolver.Resolve(providerCode);
            var integratorResult = await adapter.QueryTaxpayerAsync(profile.IntegratorUsername, profile.IntegratorSecretRef, taxNo, ct);
            if (integratorResult.IsSuccess)
            {
                source = providerCode.ToLowerInvariant();
                documentType = integratorResult.IsEInvoiceTaxpayer == true ? "EFatura" : "EArsiv";
                if (!string.IsNullOrWhiteSpace(integratorResult.Title))
                    title = integratorResult.Title.Trim();
                if (!string.IsNullOrWhiteSpace(integratorResult.ReceiverAlias))
                    receiverAlias = integratorResult.ReceiverAlias.Trim();
                message = integratorResult.Message ?? $"{providerLabel} sorgusu tamamlandı.";

                if (string.IsNullOrWhiteSpace(receiverAlias) && searchTitle.Length >= 3)
                {
                    var titleSearch = await adapter.SearchTaxpayersByTitleAsync(
                        profile.IntegratorUsername, profile.IntegratorSecretRef, searchTitle, ct);
                    var match = titleSearch.Candidates.FirstOrDefault(c => c.TaxNo == taxNo)
                                ?? titleSearch.Candidates.FirstOrDefault();
                    if (match is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(match.ReceiverAlias))
                            receiverAlias = match.ReceiverAlias.Trim();
                        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(match.Title))
                            title = match.Title.Trim();
                        if (match.IsEInvoiceTaxpayer)
                            documentType = "EFatura";
                    }
                }
            }
            else if (selectedFromTitleSearch is not null)
            {
                source = providerCode.ToLowerInvariant();
                message = string.IsNullOrWhiteSpace(integratorResult.Message)
                    ? $"{providerLabel} ünvan araması sonucu uygulandı; VKN detay sorgusu tamamlanamadı."
                    : $"{providerLabel} ünvan araması uygulandı. Detay sorgu: {integratorResult.Message}";
            }
            else if (searchTitle.Length >= 3)
            {
                var titleSearch = await adapter.SearchTaxpayersByTitleAsync(
                    profile.IntegratorUsername, profile.IntegratorSecretRef, searchTitle, ct);
                if (titleSearch.IsSuccess && titleSearch.Candidates.Count > 0)
                {
                    var exact = titleSearch.Candidates.FirstOrDefault(c => c.TaxNo == taxNo);
                    if (exact is not null)
                    {
                        source = providerCode.ToLowerInvariant();
                        title = exact.Title ?? searchTitle;
                        receiverAlias = exact.ReceiverAlias;
                        documentType = exact.IsEInvoiceTaxpayer ? "EFatura" : "EArsiv";
                        message = $"{providerLabel} ünvan araması ile mükellef bilgileri tamamlandı.";
                    }
                    else if (titleSearch.Candidates.Count > 1)
                    {
                        candidates = titleSearch.Candidates
                            .Select(c => new ManualTaxpayerCandidate
                            {
                                TaxNumber = c.TaxNo,
                                Title = c.Title ?? "",
                                ReceiverAlias = c.ReceiverAlias ?? "",
                                IsEInvoiceTaxpayer = c.IsEInvoiceTaxpayer,
                                DocumentType = c.IsEInvoiceTaxpayer ? "EFatura" : "EArsiv"
                            })
                            .ToList();
                        return Ok(new ManualTaxpayerQueryResponse
                        {
                            TaxNumber = taxNo,
                            Title = searchTitle,
                            Source = providerCode,
                            Message = $"{candidates.Count} kayıt bulundu. Listeden seçim yapın.",
                            Candidates = candidates
                        });
                    }
                    else
                    {
                        var only = titleSearch.Candidates[0];
                        source = providerCode.ToLowerInvariant();
                        taxNo = only.TaxNo;
                        title = only.Title ?? searchTitle;
                        receiverAlias = only.ReceiverAlias;
                        documentType = only.IsEInvoiceTaxpayer ? "EFatura" : "EArsiv";
                        message = $"{providerLabel} ünvan araması ile mükellef bulundu.";
                    }
                }
                else
                {
                    source = $"{providerCode.ToLowerInvariant()}-error";
                    title = searchTitle;
                    receiverAlias = null;
                    documentType = taxNo.Length == 10 ? "EFatura" : "EArsiv";
                    message = string.IsNullOrWhiteSpace(integratorResult.Message)
                        ? titleSearch.Message ?? $"{providerLabel} sorgusu başarısız oldu."
                        : $"{providerLabel} sorgusu başarısız: {integratorResult.Message}";
                }
            }
            else
            {
                source = $"{providerCode.ToLowerInvariant()}-error";
                title = searchTitle;
                receiverAlias = null;
                documentType = taxNo.Length == 10 ? "EFatura" : "EArsiv";
                message = string.IsNullOrWhiteSpace(integratorResult.Message)
                    ? $"{providerLabel} sorgusu başarısız oldu. Ünvan ve alıcı etiketi alınamadı."
                    : $"{providerLabel} sorgusu başarısız: {integratorResult.Message} Ünvan ve alıcı etiketi alınamadı.";
            }
        }
        catch (Exception ex)
        {
            if (selectedFromTitleSearch is not null)
            {
                source = providerCode.ToLowerInvariant();
                message = $"{providerLabel} ünvan araması uygulandı. Detay sorgu hatası: {ex.Message}";
            }
            else
            {
                source = $"{providerCode.ToLowerInvariant()}-error";
                title = searchTitle;
                receiverAlias = null;
                message = $"{providerLabel} sorgusu başarısız: {ex.Message} Ünvan ve alıcı etiketi alınamadı.";
            }
        }

        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(searchTitle) && selectedFromTitleSearch is null)
            title = searchTitle;

        return Ok(new ManualTaxpayerQueryResponse
        {
            TaxNumber = taxNo,
            Title = title,
            DocumentType = documentType,
            LiabilityType = documentType == "EFatura" ? "E-Fatura Mükellefi" : "E-Arşiv",
            IsEInvoiceTaxpayer = documentType == "EFatura",
            ReceiverAlias = receiverAlias ?? "",
            TitleFromEdm = (source == "edm" || source == "uyumsoft") && !string.IsNullOrWhiteSpace(title),
            ReceiverAliasFromEdm = (source == "edm" || source == "uyumsoft") && !string.IsNullOrWhiteSpace(receiverAlias),
            Source = source,
            Message = message,
            Candidates = candidates
        });
    }

    [HttpPost("manual/send")]
    [Authorize]
    public async Task<IActionResult> CreateManualAndSend([FromBody] ManualCreateAndSendReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        if (req?.Draft is null)
            return BadRequest(new { error = "Taslak bilgisi zorunludur." });

        var branchId = req.BranchId != Guid.Empty ? req.BranchId : (_tenant.BranchId ?? Guid.Empty);
        if (branchId == Guid.Empty)
            return BadRequest(new { error = "Şube seçimi bulunamadı." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && _tenant.BranchId.Value != branchId)
            return BadRequest(new { error = "Seçili profil şubesi ile belge şubesi farklı." });

        var buyerTaxNo = NormalizeDigits(req.Draft.BuyerTaxNumber);
        if (buyerTaxNo.Length != 10 && buyerTaxNo.Length != 11)
            return BadRequest(new { error = "Alıcı TCKN/VKN 10 veya 11 hane olmalıdır." });
        if (string.IsNullOrWhiteSpace(req.Draft.BuyerName))
            return BadRequest(new { error = "Alıcı ünvanı/adı zorunludur." });
        if (req.Draft.Lines is null || req.Draft.Lines.Count == 0)
            return BadRequest(new { error = "En az bir fatura satırı zorunludur." });

        var profile = await _db.EInvoiceProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == _tenant.TenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (profile is null)
            return BadRequest(new { error = "Bu şube için e-fatura entegrasyon profili bulunamadı. Önce ayarları kaydedin." });
        var credentialError = ValidateIntegratorCredentials(profile);
        if (credentialError is not null)
            return credentialError;

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği alınamadı." });

        Customer? customer = null;
        if (req.CustomerId.HasValue && req.CustomerId.Value != Guid.Empty)
        {
            customer = await _db.Customers.FirstOrDefaultAsync(
                x => x.TenantId == _tenant.TenantId && x.Id == req.CustomerId.Value, ct);
            if (customer is null)
                return BadRequest(new { error = "Seçilen müşteri/tedarikçi bulunamadı." });
        }

        var normalizedDocType = string.Equals(req.Draft.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase)
            ? "EArsiv"
            : "EFatura";
        if (normalizedDocType == "EArsiv" && !CanUseEArchive())
            return Forbid();
        if (normalizedDocType == "EFatura" && buyerTaxNo.Length != 10 && buyerTaxNo.Length != 11)
            return BadRequest(new { error = "e-Fatura için alıcı TCKN/VKN 10 veya 11 hane olmalıdır." });
        if (normalizedDocType == "EFatura" && string.IsNullOrWhiteSpace(req.Draft.BuyerEmail))
            return BadRequest(new { error = "e-Fatura için alıcı etiketi zorunludur." });

        var normalizedLines = req.Draft.Lines
            .Select((x, idx) => new ManualEInvoiceLineDraft(
                idx + 1,
                string.IsNullOrWhiteSpace(x.ProductName) ? "Ürün" : x.ProductName.Trim(),
                x.Barcode,
                x.ProductCode,
                x.Quantity <= 0 ? 1m : x.Quantity,
                string.IsNullOrWhiteSpace(x.UnitCode) ? "NIU" : x.UnitCode,
                x.UnitPrice < 0 ? 0m : x.UnitPrice,
                x.KdvRate < 0 ? 0m : x.KdvRate,
                x.KdvAmount,
                x.TotalAmount,
                x.Gram,
                x.Karat,
                x.Workmanship,
                x.ProductCategory,
                x.HasGoldEquivalent,
                x.StoneInfo,
                x.SerialNumber))
            .ToList();
        var normalizedDraft = new ManualEInvoiceDraft(
            normalizedDocType,
            req.Draft.BuyerName.Trim(),
            buyerTaxNo,
            req.Draft.BuyerAddress?.Trim(),
            req.Draft.BuyerCity?.Trim(),
            req.Draft.BuyerDistrict?.Trim(),
            req.Draft.BuyerPostalCode?.Trim(),
            req.Draft.IssueDateText?.Trim(),
            req.Draft.IssueTimeText?.Trim(),
            req.Draft.BuyerEmail?.Trim(),
            string.IsNullOrWhiteSpace(req.Draft.Currency) ? "TRY" : req.Draft.Currency,
            normalizedLines);

        var invoiceDateUtc = ResolveIssueDateUtc(req.Draft.IssueDateText, req.Draft.IssueTimeText);

        var grandTotal = normalizedLines.Sum(x =>
            x.TotalAmount ?? (Math.Round((x.Quantity <= 0 ? 1m : x.Quantity) * Math.Max(0m, x.UnitPrice), 2, MidpointRounding.AwayFromZero) +
                              Math.Round((Math.Round((x.Quantity <= 0 ? 1m : x.Quantity) * Math.Max(0m, x.UnitPrice), 2, MidpointRounding.AwayFromZero)) * ((x.KdvRate > 1 ? x.KdvRate : x.KdvRate * 100m) / 100m), 2, MidpointRounding.AwayFromZero)));

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var sale = new Sale
        {
            TenantId = _tenant.TenantId,
            BranchId = branchId,
            UserId = userId,
            CustomerId = customer?.Id,
            PaymentType = "ManualEInvoice",
            Items = new List<SaleItem>()
        };
        _db.Sales.Add(sale);

        var lineNo = 1;
        foreach (var line in normalizedLines)
        {
            var qty = line.Quantity <= 0 ? 1m : line.Quantity;
            var net = Math.Round(qty * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            var kdvRatePercent = line.KdvRate > 1 ? line.KdvRate : line.KdvRate * 100m;
            var kdvAmount = line.KdvAmount ?? Math.Round(net * (kdvRatePercent / 100m), 2, MidpointRounding.AwayFromZero);
            var total = line.TotalAmount ?? (net + kdvAmount);

            sale.Items.Add(new SaleItem
            {
                TenantId = _tenant.TenantId,
                LineNo = lineNo++,
                Kind = ResolveManualLineItemKind(line.Karat, line.ProductCategory, kdvRatePercent),
                ProductCode = string.IsNullOrWhiteSpace(line.ProductCode) ? $"MANUAL-{(req.ProductType ?? "ALTIN").Trim().ToUpperInvariant()}" : line.ProductCode.Trim(),
                ProductName = line.ProductName,
                Karat = line.Karat ?? req.ProductType ?? "",
                Category = line.ProductCategory,
                Quantity = qty,
                UnitPrice = line.UnitPrice,
                Discount = 0m,
                TaxRate = kdvRatePercent / 100m,
                LineTotal = total
            });
        }

        var invoice = new Invoice
        {
            TenantId = _tenant.TenantId,
            SaleId = sale.Id,
            BranchId = branchId,
            CustomerId = customer?.Id,
            InvoiceDate = invoiceDateUtc,
            GrandTotal = Math.Round(grandTotal, 2, MidpointRounding.AwayFromZero),
            PaymentType = "ManualEInvoice",
            IsExported = false
        };
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(ct);

        var queuedDoc = await _workflow.QueueInvoiceAsync(invoice, customer, ct);
        queuedDoc = await _workflow.QueueManualSendAsync(_tenant.TenantId, invoice.Id, normalizedDraft, ct) ?? queuedDoc;
        await tx.CommitAsync(ct);

        return Ok(new
        {
            queuedDoc.Id,
            queuedDoc.InvoiceId,
            queuedDoc.Status,
            queuedDoc.DocumentType,
            queuedDoc.InvoiceNumber,
            queuedDoc.RetryCount,
            queuedDoc.LastError,
            message = string.Equals(queuedDoc.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                ? "Manuel fatura oluşturuldu fakat EDM gönderimi başarısız oldu."
                : "Manuel fatura oluşturuldu ve gönderim kuyruğuna alındı."
        });
    }

    [HttpGet("ops/health")]
    [Authorize]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var now = DateTime.UtcNow;
        var pending = await _db.EInvoiceOutboxes.CountAsync(x => x.TenantId == tid && x.Status == "Pending", ct);
        var deadLetter = await _db.EInvoiceOutboxes.CountAsync(x => x.TenantId == tid && x.Status == "DeadLetter", ct);
        var failedDocs = await _db.EInvoiceDocuments.CountAsync(x => x.TenantId == tid && x.Status == "Failed", ct);
        var verifiedWebhooks = await _db.EInvoiceWebhookLogs.CountAsync(x => x.TenantId == tid && x.IsVerified, ct);
        var invalidWebhooks = await _db.EInvoiceWebhookLogs.CountAsync(x => x.TenantId == tid && !x.IsVerified, ct);
        var delayed = await _db.EInvoiceOutboxes.CountAsync(x => x.TenantId == tid && x.Status == "Pending" && x.NextAttemptAt < now.AddMinutes(-5), ct);
        return Ok(new
        {
            pendingOutbox = pending,
            delayedOutbox = delayed,
            deadLetterOutbox = deadLetter,
            failedDocuments = failedDocs,
            verifiedWebhooks,
            invalidWebhooks,
            checkedAtUtc = now
        });
    }

    [HttpPost("webhook/{providerCode}")]
    [AllowAnonymous]
    public async Task<IActionResult> ReceiveWebhook(string providerCode, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (_tenant.TenantId == Guid.Empty)
            return BadRequest(new { error = "Tenant gereklidir." });

        var payload = body.ValueKind == JsonValueKind.Undefined ? "{}" : body.GetRawText();
        var signature = Request.Headers.TryGetValue("X-Webhook-Signature", out var sig) ? sig.ToString() : "";
        var headers = Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await _workflow.ProcessWebhookAsync(
            _tenant.TenantId,
            _tenant.BranchId ?? Guid.Empty,
            providerCode,
            signature,
            payload,
            headers,
            ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.Message, logId = result.LogId });

        return Ok(new { success = true, result.LogId });
    }

    public sealed class CancelReq
    {
        public string? Reason { get; set; }
    }

    public sealed class DeleteSelectedReq
    {
        public List<Guid> DocumentIds { get; set; } = new();
    }

    public sealed class SaveEInvoiceProfileReq
    {
        public Guid BranchId { get; set; }
        public string ProviderCode { get; set; } = "edm";
        public bool IsActive { get; set; }
        public string CompanyName { get; set; } = "";
        public string? SoleProprietorName { get; set; }
        public string CompanyAddress { get; set; } = "";
        public string TaxNumber { get; set; } = "";
        public string TaxOffice { get; set; } = "";
        public string? SenderLabel { get; set; }
        public string? IntegratorUsername { get; set; }
        public string? IntegratorPassword { get; set; }
        public string DefaultInvoicePrefix { get; set; } = "AUR";
        public string DefaultArchivePrefix { get; set; } = "ARS";
        public decimal SpecialMatrahCraftedVatRatePercent { get; set; } = 20m;
        public decimal SpecialMatrahZiynetVatRatePercent { get; set; } = 20m;
        public decimal SalesInvoiceVatRatePercent { get; set; } = 20m;
        public bool AutoDraftEnabled { get; set; } = true;
        public string AutoDraftMatchMode { get; set; } = "ANY";
        public List<string>? AutoDraftAllowedPaymentMethods { get; set; }
        public decimal? AutoDraftMinTotal { get; set; }
        public decimal? AutoDraftMaxTotal { get; set; }
        public List<WorkmanshipRuleDto>? WorkmanshipRules { get; set; }
    }

    public sealed class SaveInvoiceSettingsReq
    {
        public Guid BranchId { get; set; }
        public decimal SpecialMatrahCraftedVatRatePercent { get; set; } = 20m;
        public decimal SpecialMatrahZiynetVatRatePercent { get; set; } = 20m;
        public decimal SalesInvoiceVatRatePercent { get; set; } = 20m;
        public bool AutoDraftEnabled { get; set; } = true;
        public string AutoDraftMatchMode { get; set; } = "ANY";
        public List<string>? AutoDraftAllowedPaymentMethods { get; set; }
        public decimal? AutoDraftMinTotal { get; set; }
        public decimal? AutoDraftMaxTotal { get; set; }
        public List<WorkmanshipRuleDto>? WorkmanshipRules { get; set; }
    }

    public sealed class TestEInvoiceConnectionReq
    {
        public Guid BranchId { get; set; }
        public string ProviderCode { get; set; } = "edm";
        public string TaxNumber { get; set; } = "";
        public string TaxOffice { get; set; } = "";
        public string CompanyAddress { get; set; } = "";
        public string? IntegratorUsername { get; set; }
        public string? IntegratorPassword { get; set; }
    }

    public sealed class EInvoiceProfileDto
    {
        public Guid BranchId { get; set; }
        public string ProviderCode { get; set; } = "edm";
        public bool IsActive { get; set; }
        public string CompanyName { get; set; } = "";
        public string? SoleProprietorName { get; set; }
        public string CompanyAddress { get; set; } = "";
        public string TaxNumber { get; set; } = "";
        public string TaxOffice { get; set; } = "";
        public string? SenderLabel { get; set; }
        public string? IntegratorUsername { get; set; }
        public bool HasIntegratorPassword { get; set; }
        public string DefaultInvoicePrefix { get; set; } = "AUR";
        public string DefaultArchivePrefix { get; set; } = "ARS";
        public decimal SpecialMatrahCraftedVatRatePercent { get; set; } = 20m;
        public decimal SpecialMatrahZiynetVatRatePercent { get; set; } = 20m;
        public decimal SalesInvoiceVatRatePercent { get; set; } = 20m;
        public bool AutoDraftEnabled { get; set; } = true;
        public string AutoDraftMatchMode { get; set; } = "ANY";
        public List<string> AutoDraftAllowedPaymentMethods { get; set; } = new();
        public decimal? AutoDraftMinTotal { get; set; }
        public decimal? AutoDraftMaxTotal { get; set; }
        public List<WorkmanshipRuleDto> WorkmanshipRules { get; set; } = new();
    }

    public sealed class WorkmanshipRuleDto
    {
        public string ProductType { get; set; } = EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCrafted;
        public string? Karat { get; set; }
        public decimal MinTotal { get; set; }
        public decimal MaxTotal { get; set; }
        public decimal Percentage { get; set; }
    }

    public sealed class SendPreviewReq
    {
        public ManualEInvoiceDraft? Draft { get; set; }
    }

    public sealed class ManualTaxpayerQueryReq
    {
        public Guid BranchId { get; set; }
        public string TaxNumber { get; set; } = "";
        public string? Title { get; set; }
    }

    public sealed class ManualTaxpayerQueryResponse
    {
        public string TaxNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string DocumentType { get; set; } = "EArsiv";
        public string LiabilityType { get; set; } = "E-Arşiv";
        public bool IsEInvoiceTaxpayer { get; set; }
        public string ReceiverAlias { get; set; } = "";
        public bool TitleFromEdm { get; set; }
        public bool ReceiverAliasFromEdm { get; set; }
        public string Source { get; set; } = "rule";
        public string Message { get; set; } = "";

        /// <summary>Ünvan aramasında bulunan mükellef adayları. Tek sonuçta doldurulmaz.</summary>
        public List<ManualTaxpayerCandidate> Candidates { get; set; } = new();
    }

    public sealed class ManualTaxpayerCandidate
    {
        public string TaxNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string ReceiverAlias { get; set; } = "";
        public bool IsEInvoiceTaxpayer { get; set; }
        public string DocumentType { get; set; } = "EArsiv";
    }

    public sealed class ManualCreateAndSendReq
    {
        public Guid BranchId { get; set; }
        public Guid? CustomerId { get; set; }
        public string? ProductType { get; set; }
        public string? CalculationMode { get; set; }
        public decimal? MarketUnitPrice { get; set; }
        public decimal? HasGoldPrice { get; set; }
        public decimal? Workmanship { get; set; }
        public decimal? Gram { get; set; }
        public decimal? TotalAmount { get; set; }
        public ManualEInvoiceDraft? Draft { get; set; }
    }

    private static EInvoiceProfileDto ToDto(kuyumcu_domain.Entities.EInvoiceProfile p)
    {
        var settings = EInvoiceProfileSettingsCodec.Decode(p.IntegratorCompanyCode);
        return new EInvoiceProfileDto
        {
            BranchId = p.BranchId,
            ProviderCode = p.ProviderCode,
            IsActive = p.IsActive,
            CompanyName = p.CompanyName,
            SoleProprietorName = p.SoleProprietorName,
            CompanyAddress = p.CompanyAddress,
            TaxNumber = p.TaxNumber,
            TaxOffice = p.TaxOffice,
            SenderLabel = p.SenderLabel,
            IntegratorUsername = p.IntegratorUsername,
            HasIntegratorPassword = !string.IsNullOrWhiteSpace(p.IntegratorSecretRef),
            DefaultInvoicePrefix = p.DefaultInvoicePrefix,
            DefaultArchivePrefix = p.DefaultArchivePrefix,
            SpecialMatrahCraftedVatRatePercent = settings.SpecialMatrahCraftedVatRatePercent,
            SpecialMatrahZiynetVatRatePercent = settings.SpecialMatrahZiynetVatRatePercent,
            SalesInvoiceVatRatePercent = settings.SalesInvoiceVatRatePercent,
            AutoDraftEnabled = settings.AutoDraftEnabled,
            AutoDraftMatchMode = settings.AutoDraftMatchMode,
            AutoDraftAllowedPaymentMethods = settings.AutoDraftAllowedPaymentMethods,
            AutoDraftMinTotal = settings.AutoDraftMinTotal,
            AutoDraftMaxTotal = settings.AutoDraftMaxTotal,
            WorkmanshipRules = settings.WorkmanshipRules
                .Select(x => new WorkmanshipRuleDto
                {
                    ProductType = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipProductType(x.ProductType),
                    Karat = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipSelector(x.ProductType, x.Karat)
                              ?? x.Karat,
                    MinTotal = x.MinTotal,
                    MaxTotal = x.MaxTotal,
                    Percentage = x.Percentage
                })
                .ToList()
        };
    }

    private static string? ExtractUblXml(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (TryFindJsonString(doc.RootElement, "ublXml", out var xml) && !string.IsNullOrWhiteSpace(xml))
                return xml;
            if (TryFindJsonString(doc.RootElement, "ublBase64", out var base64) && !string.IsNullOrWhiteSpace(base64))
            {
                var bytes = Convert.FromBase64String(base64);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryFindJsonString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                    return !string.IsNullOrWhiteSpace(value);
                }
                if (TryFindJsonString(p.Value, name, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (TryFindJsonString(child, name, out value))
                    return true;
        }
        return false;
    }

    private static bool IsUyumsoftProfile(EInvoiceProfile? profile)
        => string.Equals(profile?.ProviderCode, "uyumsoft", StringComparison.OrdinalIgnoreCase);

    private static IActionResult? ValidateIntegratorCredentials(EInvoiceProfile? profile)
    {
        if (profile is null)
            return null;

        var integratorName = IsUyumsoftProfile(profile) ? "Uyumsoft" : "EDM";
        if (string.IsNullOrWhiteSpace(profile.IntegratorUsername))
            return new BadRequestObjectResult(new { error = $"{integratorName} kullanıcı adı zorunludur." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorSecretRef))
            return new BadRequestObjectResult(new { error = $"{integratorName} şifresi zorunludur." });
        if (!IsUyumsoftProfile(profile) && string.IsNullOrWhiteSpace(profile.SenderLabel))
            return new BadRequestObjectResult(new { error = "EDM gönderici etiketi (SenderLabel) zorunludur. E-Fatura ayarlarında SenderLabel girin." });
        return null;
    }

    private static object ValidateUblRequiredProperties(string ublXml)
    {
        var required = new[]
        {
            "ÜRÜN ADI",
            "BARKOD",
            "ÜRÜN KODU",
            "GRAM",
            "AYAR",
            "İŞÇİLİK",
            "MİKTAR",
            "BİRİM FİYAT",
            "KDV ORANI",
            "KDV TUTARI",
            "TOPLAM TUTAR",
            "DÖVİZ TİPİ",
            "HAS ALTIN KARŞILIĞI",
            "ÜRÜN KATEGORİSİ",
            "TAŞ BİLGİSİ",
            "SERİ NUMARASI"
        };

        var x = XDocument.Parse(ublXml);
        var lines = x.Descendants().Where(e => e.Name.LocalName == "InvoiceLine").ToList();
        var missingByLine = new List<object>();

        foreach (var line in lines)
        {
            var lineId = line.Descendants().FirstOrDefault(e => e.Name.LocalName == "ID")?.Value ?? "?";
            var names = line.Descendants()
                .Where(e => e.Name.LocalName == "AdditionalItemProperty")
                .Select(p => p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim() ?? "")
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var missing = required.Where(r => !names.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                missingByLine.Add(new { lineId, missing });
        }

        return new
        {
            lineCount = lines.Count,
            requiredProperties = required,
            missingLineCount = missingByLine.Count,
            missingByLine
        };
    }

    private static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var chars = value.Where(char.IsDigit).ToArray();
        return new string(chars);
    }

    private static DateTime ResolveIssueDateUtc(string? issueDateText, string? issueTimeText)
    {
        var dateFormats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" };
        if (!DateTime.TryParseExact(issueDateText ?? string.Empty, dateFormats, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out var datePart) &&
            !DateTime.TryParse(issueDateText ?? string.Empty, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out datePart))
        {
            return DateTime.UtcNow;
        }

        if (!TimeSpan.TryParseExact(issueTimeText ?? string.Empty, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var timePart) &&
            !TimeSpan.TryParseExact(issueTimeText ?? string.Empty, @"hh\:mm", CultureInfo.InvariantCulture, out timePart))
        {
            timePart = TimeSpan.Zero;
        }

        var localDateTime = DateTime.SpecifyKind(datePart.Date.Add(timePart), DateTimeKind.Local);
        return localDateTime.ToUniversalTime();
    }

    private static ItemKind ResolveManualLineItemKind(string? karat, string? category, decimal kdvRatePercent)
    {
        var text = $"{karat} {category}".ToUpperInvariant();
        if (text.Contains("GUMUS") || text.Contains("GÜMÜŞ"))
            return ItemKind.Silver;
        if (Math.Abs(kdvRatePercent) < 0.001m)
            return ItemKind.CraftedGold;
        if (text.Contains("ZIYNET") || text.Contains("ZİYNET"))
            return ItemKind.Ziynet;
        return ItemKind.Product;
    }

    private bool CanUseEInvoice()
    {
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasPermissionClaim("perm_einvoice");
    }

    private bool CanUseEArchive()
    {
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasPermissionClaim("perm_earchive");
    }

    private bool HasPermissionClaim(string claimType)
    {
        var raw = User.FindFirstValue(claimType);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSoleProprietorTaxNumber(string? taxNumber)
    {
        var digits = new string((taxNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length == 11;
    }
}
