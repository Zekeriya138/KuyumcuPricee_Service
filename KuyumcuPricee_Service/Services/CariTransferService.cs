using System.Globalization;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public sealed record CariTransferDovizLineReq(string Unit, decimal SignedAmount, string? LedgerSide = null);
public sealed record CariTransferZiynetLineReq(string Ad, string? Tip, decimal Adet, int OpenDirection);
public sealed record CariTransferIscilikliLineReq(Guid TransactionId);

public sealed record CariTransferProcessReq(
    string SourcePartyKind,
    Guid SourcePartyId,
    string TargetPartyKind,
    Guid TargetPartyId,
    Guid? BranchId,
    string? Description,
    DateTime? TxDate,
    List<CariTransferDovizLineReq>? DovizLines,
    List<CariTransferZiynetLineReq>? ZiynetLines,
    List<CariTransferIscilikliLineReq>? IscilikliLines);

public sealed record CariTransferProcessResult(bool Ok, string? Error, Guid? TransferId, Guid? SourceBatchId, Guid? TargetBatchId);

public sealed class CariTransferService
{
    private readonly AppDbContext _db;

    public CariTransferService(AppDbContext db) => _db = db;

    public async Task<CariTransferProcessResult> ProcessAsync(
        CariTransferProcessReq req, Guid tenantId, Guid branchId, Guid? userId, string? userName, CancellationToken ct)
    {
        var sourceKind = NormalizePartyKind(req.SourcePartyKind);
        var targetKind = NormalizePartyKind(req.TargetPartyKind);
        if (sourceKind is null || targetKind is null)
            return new CariTransferProcessResult(false, "Kaynak ve hedef taraf türü Customer veya Supplier olmalıdır.", null, null, null);
        if (req.SourcePartyId == Guid.Empty || req.TargetPartyId == Guid.Empty)
            return new CariTransferProcessResult(false, "Kaynak ve hedef cari seçilmelidir.", null, null, null);
        if (req.SourcePartyId == req.TargetPartyId && sourceKind == targetKind)
            return new CariTransferProcessResult(false, "Kaynak ve hedef cari aynı olamaz.", null, null, null);

        var doviz = (req.DovizLines ?? new()).Where(x => x.SignedAmount != 0m).ToList();
        var ziynet = (req.ZiynetLines ?? new()).Where(x => x.Adet > 0m && !string.IsNullOrWhiteSpace(x.Ad)).ToList();
        var iscilikli = (req.IscilikliLines ?? new()).Where(x => x.TransactionId != Guid.Empty).ToList();
        if (doviz.Count == 0 && ziynet.Count == 0 && iscilikli.Count == 0)
            return new CariTransferProcessResult(false, "Transfer için en az bir kalem seçilmelidir.", null, null, null);
        if (iscilikli.Count > 0 && (sourceKind != "Customer" || targetKind != "Customer"))
            return new CariTransferProcessResult(false, "İşçilikli ürün transferi yalnızca müşteri → müşteri arasında yapılabilir.", null, null, null);

        if (!await PartyExistsAsync(tenantId, branchId, sourceKind, req.SourcePartyId, ct))
            return new CariTransferProcessResult(false, "Kaynak cari bulunamadı.", null, null, null);
        if (!await PartyExistsAsync(tenantId, branchId, targetKind, req.TargetPartyId, ct))
            return new CariTransferProcessResult(false, "Hedef cari bulunamadı.", null, null, null);

        var transferId = Guid.NewGuid();
        var txDate = req.TxDate?.ToUniversalTime() ?? DateTime.UtcNow;
        var sourceBatchId = Guid.NewGuid();
        var targetBatchId = Guid.NewGuid();
        var sourceName = await ResolvePartyNameAsync(tenantId, branchId, sourceKind, req.SourcePartyId, ct);
        var targetName = await ResolvePartyNameAsync(tenantId, branchId, targetKind, req.TargetPartyId, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var line in doviz)
            {
                var unit = NormalizeUnit(line.Unit);
                var ledgerSide = ResolveLedgerSide(line.LedgerSide, line.SignedAmount);
                var amount = decimal.Round(Math.Abs(line.SignedAmount), 6, MidpointRounding.AwayFromZero);
                if (amount == 0m) continue;
                await ApplyDovizTransferAsync(
                    tenantId, branchId, userId, userName, transferId, txDate,
                    sourceKind, req.SourcePartyId, sourceBatchId, sourceName, targetKind, req.TargetPartyId, targetBatchId, targetName,
                    unit, amount, ledgerSide, req.Description, ct);
            }

            foreach (var line in ziynet)
            {
                var adet = decimal.Round(line.Adet, 3, MidpointRounding.AwayFromZero);
                if (adet <= 0m) continue;
                await ApplyZiynetTransferAsync(
                    tenantId, branchId, userId, userName, transferId, txDate,
                    sourceKind, req.SourcePartyId, sourceBatchId, sourceName, targetKind, req.TargetPartyId, targetBatchId, targetName,
                    line.Ad, line.Tip, adet, line.OpenDirection, req.Description, ct);
            }

            foreach (var line in iscilikli)
            {
                await ApplyIscilikliTransferAsync(
                    tenantId, branchId, userId, userName, transferId, txDate,
                    req.SourcePartyId, sourceBatchId, sourceName,
                    req.TargetPartyId, targetBatchId, targetName,
                    line.TransactionId, req.Description, ct);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new CariTransferProcessResult(true, null, transferId, sourceBatchId, targetBatchId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new CariTransferProcessResult(false, ex.Message, null, null, null);
        }
    }

    private async Task ApplyDovizTransferAsync(
        Guid tenantId, Guid branchId, Guid? userId, string? userName, Guid transferId, DateTime txDate,
        string sourceKind, Guid sourceId, Guid sourceBatchId, string sourceName,
        string targetKind, Guid targetId, Guid targetBatchId, string targetName,
        string unit, decimal amount, string ledgerSide, string? description, CancellationToken ct)
    {
        if (sourceKind == "Customer")
        {
            await ApplyCustomerDovizLegAsync(tenantId, branchId, userId, userName, transferId, txDate, sourceId, sourceBatchId, targetKind, targetId, targetBatchId, targetName, unit, amount, ledgerSide, isSource: true, description, ct);
            await ApplyCustomerDovizLegAsync(tenantId, branchId, userId, userName, transferId, txDate, targetId, targetBatchId, sourceKind, sourceId, sourceBatchId, sourceName, unit, amount, ledgerSide, isSource: false, description, ct);
        }
        else
        {
            await ApplySupplierDovizLegAsync(tenantId, branchId, userId, userName, transferId, txDate, sourceId, sourceBatchId, targetKind, targetId, targetBatchId, targetName, unit, amount, ledgerSide, isSource: true, description, ct);
            await ApplySupplierDovizLegAsync(tenantId, branchId, userId, userName, transferId, txDate, targetId, targetBatchId, sourceKind, sourceId, sourceBatchId, sourceName, unit, amount, ledgerSide, isSource: false, description, ct);
        }
    }

    private async Task ApplyCustomerDovizLegAsync(
        Guid tenantId, Guid branchId, Guid? userId, string? userName, Guid transferId, DateTime txDate,
        Guid customerId, Guid batchId, string peerKind, Guid peerId, Guid peerBatchId, string peerName,
        string unit, decimal amount, string ledgerSide, bool isSource, string? description, CancellationToken ct)
    {
        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, customerId, ct);
        int direction;
        string refType;
        decimal balanceDelta;
        string cariDurum;
        if (isSource)
        {
            (direction, refType, balanceDelta) = CustomerFinanceHelper.BuildReductionLeg(ledgerSide, amount);
            cariDurum = "Transfer";
        }
        else
        {
            (direction, cariDurum, balanceDelta) = CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, amount);
            refType = "TRANSFER";
        }

        ApplyCustomerBalanceDelta(bal, unit, balanceDelta);
        bal.UpdatedAt = DateTime.UtcNow;

        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = "DOVIZ",
            ItemName = unit,
            Quantity = amount,
            Direction = direction,
            TxDate = txDate,
            CariDurum = cariDurum,
            RefType = refType,
            RefId = transferId,
            Note = CariTransferMarker.BuildNote(transferId, isSource ? "SOURCE" : "TARGET", peerKind, peerId, peerBatchId, peerName, description),
            BatchId = batchId,
            UserId = userId,
            KullaniciAdi = userName
        });
    }

    private async Task ApplySupplierDovizLegAsync(
        Guid tenantId, Guid branchId, Guid? userId, string? userName, Guid transferId, DateTime txDate,
        Guid supplierId, Guid batchId, string peerKind, Guid peerId, Guid peerBatchId, string peerName,
        string unit, decimal amount, string ledgerSide, bool isSource, string? description, CancellationToken ct)
    {
        var bal = await GetOrCreateSupplierBalanceAsync(tenantId, supplierId, ct);
        decimal signedDelta;
        if (isSource)
            signedDelta = CustomerFinanceHelper.BuildReductionLeg(ledgerSide, amount).BalanceDelta;
        else
            signedDelta = CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, amount).BalanceDelta;

        ApplySupplierBalanceDelta(bal, unit, signedDelta);
        bal.UpdatedAt = DateTime.UtcNow;

        _db.SupplierTransactions.Add(new SupplierTransaction
        {
            TenantId = tenantId,
            SupplierId = supplierId,
            BranchId = branchId,
            TxType = "TRANSFER",
            SourceUnit = unit,
            SourceAmount = amount,
            TargetUnit = unit,
            TargetAmount = signedDelta,
            IsConverted = false,
            SourceUnitTlRate = 1m,
            TargetUnitTlRate = 1m,
            Description = CariTransferMarker.BuildNote(transferId, isSource ? "SOURCE" : "TARGET", peerKind, peerId, peerBatchId, peerName, description),
            TxDate = txDate,
            RefType = "TRANSFER",
            RefId = transferId,
            BatchId = batchId,
            UserId = userId,
            KullaniciAdi = userName
        });
    }

    private async Task ApplyZiynetTransferAsync(
        Guid tenantId, Guid branchId, Guid? userId, string? userName, Guid transferId, DateTime txDate,
        string sourceKind, Guid sourceId, Guid sourceBatchId, string sourceName,
        string targetKind, Guid targetId, Guid targetBatchId, string targetName,
        string ad, string? tip, decimal adet, int openDirection, string? description, CancellationToken ct)
    {
        if (openDirection == 0) openDirection = 1;
        var ledgerSide = openDirection >= 0
            ? CustomerFinanceHelper.LedgerAlacak
            : CustomerFinanceHelper.LedgerBorc;

        if (sourceKind == "Customer")
            await ApplyCustomerZiynetLegAsync(tenantId, branchId, userId, userName, transferId, txDate, sourceId, sourceBatchId, targetKind, targetId, targetBatchId, targetName, ad, tip, adet, ledgerSide, isSource: true, description, ct);
        else
            await ApplySupplierZiynetLegAsync(tenantId, branchId, userId, userName, transferId, txDate, sourceId, sourceBatchId, targetKind, targetId, targetBatchId, targetName, ad, tip, adet, ledgerSide, isSource: true, description, ct);

        if (targetKind == "Customer")
            await ApplyCustomerZiynetLegAsync(tenantId, branchId, userId, userName, transferId, txDate, targetId, targetBatchId, sourceKind, sourceId, sourceBatchId, sourceName, ad, tip, adet, ledgerSide, isSource: false, description, ct);
        else
            await ApplySupplierZiynetLegAsync(tenantId, branchId, userId, userName, transferId, txDate, targetId, targetBatchId, sourceKind, sourceId, sourceBatchId, sourceName, ad, tip, adet, ledgerSide, isSource: false, description, ct);
    }

    private Task ApplyCustomerZiynetLegAsync(
        Guid tenantId, Guid branchId, Guid? userId, string? userName, Guid transferId, DateTime txDate,
        Guid customerId, Guid batchId, string peerKind, Guid peerId, Guid peerBatchId, string peerName,
        string ad, string? tip, decimal adet, string ledgerSide, bool isSource, string? description, CancellationToken ct)
    {
        var keyAd = NormalizeZiynetKeyPart(ad);
        var keyTip = NormalizeZiynetKeyPart(string.IsNullOrWhiteSpace(tip) ? "Yeni" : tip);
        var role = isSource ? "SOURCE" : "TARGET";

        if (IsHasAltinZiynetAd(keyAd))
        {
            var balanceDelta = isSource
                ? CustomerFinanceHelper.BuildReductionLeg(ledgerSide, adet).BalanceDelta
                : CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, adet).BalanceDelta;
            return ApplyCustomerHasFromZiynetAsync(tenantId, branchId, userId, userName, transferId, txDate, customerId, batchId, peerKind, peerId, peerBatchId, peerName, balanceDelta, ledgerSide, isSource, role, description, ct);
        }

        int direction;
        string refType;
        string cariDurum;
        if (isSource)
        {
            (direction, refType, _) = CustomerFinanceHelper.BuildReductionLeg(ledgerSide, adet);
            cariDurum = "Transfer";
        }
        else
        {
            (direction, cariDurum, _) = CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, adet);
            refType = "TRANSFER";
        }

        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = "ZIYNET",
            ItemName = keyAd,
            ItemType = keyTip,
            Quantity = adet,
            Direction = direction,
            TxDate = txDate,
            CariDurum = cariDurum,
            RefType = refType,
            RefId = transferId,
            Note = CariTransferMarker.BuildNote(transferId, role, peerKind, peerId, peerBatchId, peerName, description),
            BatchId = batchId,
            UserId = userId,
            KullaniciAdi = userName
        });
        return Task.CompletedTask;
    }

    private async Task ApplyCustomerHasFromZiynetAsync(
        Guid tenantId, Guid branchId, Guid? userId, string? userName, Guid transferId, DateTime txDate,
        Guid customerId, Guid batchId, string peerKind, Guid peerId, Guid peerBatchId, string peerName,
        decimal balanceDelta, string ledgerSide, bool isSource, string role, string? description, CancellationToken ct)
    {
        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, customerId, ct);
        ApplyCustomerBalanceDelta(bal, "HAS", balanceDelta);
        bal.UpdatedAt = DateTime.UtcNow;
        var qty = Math.Abs(balanceDelta);
        int direction;
        string refType;
        if (isSource)
        {
            (direction, refType, _) = CustomerFinanceHelper.BuildReductionLeg(ledgerSide, qty);
        }
        else
        {
            (direction, _, _) = CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, qty);
            refType = "TRANSFER";
        }

        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = "DOVIZ",
            ItemName = "HAS",
            Quantity = qty,
            Direction = direction,
            HasEquivalent = qty,
            TxDate = txDate,
            CariDurum = "Transfer",
            RefType = refType,
            RefId = transferId,
            Note = CariTransferMarker.BuildNote(transferId, role, peerKind, peerId, peerBatchId, peerName, description),
            BatchId = batchId,
            UserId = userId,
            KullaniciAdi = userName
        });
    }

    private async Task ApplySupplierZiynetLegAsync(
        Guid tenantId, Guid branchId, Guid? userId, string? userName, Guid transferId, DateTime txDate,
        Guid supplierId, Guid batchId, string peerKind, Guid peerId, Guid peerBatchId, string peerName,
        string ad, string? tip, decimal adet, string ledgerSide, bool isSource, string? description, CancellationToken ct)
    {
        var normAd = (ad ?? "").Trim();
        var normTip = NormalizeOpeningZiynetTip(normAd, tip);
        var role = isSource ? "SOURCE" : "TARGET";
        decimal signedDelta = isSource
            ? CustomerFinanceHelper.BuildReductionLeg(ledgerSide, adet).BalanceDelta
            : CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, adet).BalanceDelta;

        if (IsHasAltinZiynetAd(normAd))
        {
            var bal = await GetOrCreateSupplierBalanceAsync(tenantId, supplierId, ct);
            ApplySupplierBalanceDelta(bal, "HAS", signedDelta);
            bal.UpdatedAt = DateTime.UtcNow;
            _db.SupplierTransactions.Add(new SupplierTransaction
            {
                TenantId = tenantId,
                SupplierId = supplierId,
                BranchId = branchId,
                TxType = "TRANSFER",
                SourceUnit = "HAS",
                SourceAmount = adet,
                TargetUnit = "HAS",
                TargetAmount = signedDelta,
                IsConverted = false,
                SourceUnitTlRate = 1m,
                TargetUnitTlRate = 1m,
                Description = CariTransferMarker.BuildNote(transferId, role, peerKind, peerId, peerBatchId, peerName, description),
                TxDate = txDate,
                RefType = "TRANSFER",
                RefId = transferId,
                BatchId = batchId,
                UserId = userId,
                KullaniciAdi = userName
            });
            return;
        }

        _db.SupplierTransactions.Add(new SupplierTransaction
        {
            TenantId = tenantId,
            SupplierId = supplierId,
            BranchId = branchId,
            TxType = "ZIYNET",
            SourceUnit = "ADET",
            SourceAmount = adet,
            TargetUnit = "ADET",
            TargetAmount = signedDelta,
            IsConverted = false,
            SourceUnitTlRate = 1m,
            TargetUnitTlRate = 1m,
            Description = BuildSupplierZiynetDescription(normAd, normTip, signedDelta, $"TRANSFER:{transferId:D}") + " " +
                          CariTransferMarker.BuildNote(transferId, role, peerKind, peerId, peerBatchId, peerName, description),
            TxDate = txDate,
            RefType = "TRANSFER",
            RefId = transferId,
            BatchId = batchId,
            UserId = userId,
            KullaniciAdi = userName
        });
    }

    private static string ResolveLedgerSide(string? ledgerSide, decimal signedAmount)
    {
        var normalized = CustomerFinanceHelper.NormalizeLedgerSide(ledgerSide);
        if (!string.IsNullOrEmpty(normalized))
            return normalized;
        return signedAmount < 0m ? CustomerFinanceHelper.LedgerBorc : CustomerFinanceHelper.LedgerAlacak;
    }

    private async Task ApplyIscilikliTransferAsync(
        Guid tenantId, Guid branchId, Guid? userId, string? userName, Guid transferId, DateTime txDate,
        Guid sourceCustomerId, Guid sourceBatchId, string sourceName,
        Guid targetCustomerId, Guid targetBatchId, string targetName,
        Guid transactionId, string? description, CancellationToken ct)
    {
        var baseTx = await _db.CustomerTransactions.FirstOrDefaultAsync(x =>
            x.Id == transactionId &&
            x.TenantId == tenantId &&
            x.CustomerId == sourceCustomerId &&
            !x.IsDeleted &&
            x.GroupCode == "ISCILIKLI", ct);
        if (baseTx is null)
            throw new InvalidOperationException("İşçilikli kaynak satırı bulunamadı.");

        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = sourceCustomerId,
            BranchId = branchId,
            GroupCode = "AUDIT",
            ItemName = "ISCILIKLI_GUNCELLEME",
            ItemType = baseTx.ItemName,
            Quantity = Math.Abs(baseTx.Quantity),
            Gram = baseTx.Gram,
            Ayar = baseTx.Ayar,
            HasEquivalent = baseTx.HasEquivalent,
            TotalPriceTl = baseTx.TotalPriceTl,
            Direction = baseTx.Direction >= 0 ? -1 : 1,
            TxDate = txDate,
            CariDurum = "Transfer",
            RefType = "TRANSFER",
            RefId = transferId,
            Note = CariTransferMarker.BuildNote(transferId, "SOURCE", "Customer", targetCustomerId, targetBatchId, targetName, description),
            BatchId = sourceBatchId,
            UserId = userId,
            KullaniciAdi = userName
        });
        baseTx.IsDeleted = true;

        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = targetCustomerId,
            BranchId = branchId,
            GroupCode = "ISCILIKLI",
            ItemName = baseTx.ItemName,
            ItemType = baseTx.ItemType,
            Quantity = baseTx.Quantity,
            Gram = baseTx.Gram,
            Ayar = baseTx.Ayar,
            HasEquivalent = baseTx.HasEquivalent,
            TotalPriceTl = baseTx.TotalPriceTl,
            Direction = baseTx.Direction,
            TxDate = txDate,
            CariDurum = baseTx.CariDurum,
            RefType = "TRANSFER",
            RefId = transferId,
            Note = CariTransferMarker.BuildNote(transferId, "TARGET", "Customer", sourceCustomerId, sourceBatchId, sourceName, description),
            BatchId = targetBatchId,
            UserId = userId,
            KullaniciAdi = userName
        });
    }

    private async Task<bool> PartyExistsAsync(Guid tenantId, Guid branchId, string kind, Guid id, CancellationToken ct)
    {
        if (kind == "Customer")
            return await _db.Customers.AnyAsync(x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        return await _db.Suppliers.AnyAsync(x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
    }

    private async Task<string> ResolvePartyNameAsync(Guid tenantId, Guid branchId, string kind, Guid id, CancellationToken ct)
    {
        if (kind == "Customer")
        {
            var c = await _db.Customers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId, ct);
            return c?.FullName ?? id.ToString();
        }
        var s = await _db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId, ct);
        return s?.CompanyName ?? id.ToString();
    }

    private async Task<SupplierBalance> GetOrCreateSupplierBalanceAsync(Guid tenantId, Guid supplierId, CancellationToken ct)
    {
        var bal = await _db.SupplierBalances.FirstOrDefaultAsync(x => x.SupplierId == supplierId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (bal is not null) return bal;
        bal = new SupplierBalance { TenantId = tenantId, SupplierId = supplierId };
        _db.SupplierBalances.Add(bal);
        return bal;
    }

    private static string? NormalizePartyKind(string? raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Equals("Customer", StringComparison.OrdinalIgnoreCase) || t.Equals("Musteri", StringComparison.OrdinalIgnoreCase) || t.Equals("Müşteri", StringComparison.OrdinalIgnoreCase))
            return "Customer";
        if (t.Equals("Supplier", StringComparison.OrdinalIgnoreCase) || t.Equals("Tedarikci", StringComparison.OrdinalIgnoreCase) || t.Equals("Tedarikçi", StringComparison.OrdinalIgnoreCase))
            return "Supplier";
        return null;
    }

    private static string NormalizeUnit(string? raw)
    {
        var u = (raw ?? "").Trim().ToUpperInvariant();
        return u switch
        {
            "TRY" or "TL" => "TL",
            "USD" => "USD",
            "EUR" => "EUR",
            "GBP" or "POUND" => "GBP",
            "HAS" or "GOLD" => "HAS",
            "GUMUS" or "GÜMÜŞ" or "SILVER" => "GUMUS",
            _ => "TL"
        };
    }

    private static void ApplyCustomerBalanceDelta(CustomerBalance b, string unit, decimal delta)
    {
        switch (unit)
        {
            case "USD": b.BalanceUSD += delta; break;
            case "EUR": b.BalanceEUR += delta; break;
            case "GBP": b.BalanceGBP += delta; break;
            case "HAS": b.BalanceHAS += delta; break;
            case "GUMUS": break;
            default: b.BalanceTL += delta; break;
        }
    }

    private static void ApplySupplierBalanceDelta(SupplierBalance b, string unit, decimal delta)
    {
        switch (unit)
        {
            case "USD": b.BalanceUSD += delta; break;
            case "EUR": b.BalanceEUR += delta; break;
            case "GBP": b.BalanceGBP += delta; break;
            case "HAS": b.BalanceHAS += delta; break;
            case "GUMUS": b.BalanceGUMUS += delta; break;
            default: b.BalanceTL += delta; break;
        }
    }

    private static string NormalizeZiynetKeyPart(string? raw) => (raw ?? "").Trim().ToUpperInvariant();

    private static bool IsHasAltinZiynetAd(string ad)
    {
        var s = (ad ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Contains("KÜLÇE") || s.Contains("KULCE") || s.Contains("22 AYAR") || s.Contains("22AYAR")) return false;
        return s.Contains("HAS ALTIN") || s.Contains("HASALTIN") || s.Contains("HAS ALTİN") || s == "HAS";
    }

    private static string NormalizeOpeningZiynetTip(string ad, string? tip)
    {
        if (IsGramHasZiynetAd(ad)) return "";
        var t = (tip ?? "").Trim();
        if (string.Equals(t, "eski", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "old", StringComparison.OrdinalIgnoreCase))
            return "Eski";
        return "Yeni";
    }

    private static bool IsGramHasZiynetAd(string ad)
    {
        var s = (ad ?? "").Trim().ToUpperInvariant();
        return s.Contains("KÜLÇE") || s.Contains("KULCE") || s.Contains("22 AYAR") || s.Contains("22AYAR") || (s.Contains("GRAM") && s.Contains("HAS"));
    }

    private static string BuildSupplierZiynetDescription(string ad, string tip, decimal adet, string reference)
    {
        static string Safe(string? raw) => (raw ?? "").Replace("|", "/").Replace(";", ",").Trim();
        return $"[ZIYNET]|AD={Safe(ad)}|TIP={Safe(tip)}|ADET={adet.ToString("0.###", CultureInfo.InvariantCulture)}|REF={Safe(reference)}";
    }
}
