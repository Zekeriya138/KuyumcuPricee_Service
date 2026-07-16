using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public static class CustomerFinanceHelper
{
    public static async Task<CustomerBalance> GetOrCreateBalanceAsync(
        AppDbContext db, Guid tenantId, Guid customerId, CancellationToken ct)
    {
        var bal = await db.CustomerBalances
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.CustomerId == customerId && !x.IsDeleted, ct);
        if (bal is not null) return bal;

        bal = new CustomerBalance
        {
            TenantId = tenantId,
            CustomerId = customerId
        };
        db.CustomerBalances.Add(bal);
        return bal;
    }

    public static async Task AddTransactionAsync(
        AppDbContext db,
        Guid tenantId,
        Guid customerId,
        Guid? branchId,
        string groupCode,
        string itemName,
        string? itemType,
        decimal quantity,
        int direction,
        decimal? gram,
        string? ayar,
        decimal? milyem,
        decimal? hasEq,
        decimal? unitPriceTl,
        decimal? totalPriceTl,
        string? refType,
        Guid? refId,
        string? note,
        DateTime? txDate,
        CancellationToken ct,
        string? cariDurumOverride = null,
        Guid? batchId = null)
    {
        var tx = new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = (groupCode ?? "").Trim().ToUpperInvariant(),
            ItemName = (itemName ?? "").Trim().ToUpperInvariant(),
            ItemType = string.IsNullOrWhiteSpace(itemType) ? null : itemType.Trim(),
            Quantity = quantity,
            Direction = direction >= 0 ? 1 : -1,
            Gram = gram,
            Ayar = ayar,
            Milyem = milyem,
            HasEquivalent = hasEq,
            UnitPriceTl = unitPriceTl,
            TotalPriceTl = totalPriceTl,
            CariDurum = string.IsNullOrWhiteSpace(cariDurumOverride)
                ? (direction >= 0 ? "Alacakli" : "Borclu")
                : cariDurumOverride.Trim(),
            RefType = refType,
            RefId = refId,
            Note = note,
            TxDate = txDate ?? DateTime.UtcNow,
            BatchId = batchId
        };
        db.CustomerTransactions.Add(tx);
        await Task.CompletedTask;
    }

    public static decimal MilyemFromAyar(string? ayar)
    {
        var a = (ayar ?? "").Trim().ToUpperInvariant();
        if (a.Contains("22") || a.Contains("916")) return 0.916m;
        if (a.Contains("24") || a.Contains("999")) return 1.000m;
        if (a.Contains("18") || a.Contains("750")) return 0.750m;
        if (a.Contains("14") || a.Contains("585")) return 0.585m;
        return 0.000m;
    }

    /// <summary>
    /// Has altın ziynet satışı/emaneti adet defterine (ZIYNET) değil, DOVIZ/HAS bakiyesine yazılmalı mı?
    /// Satır adı/kategorisi katalogdaki "Gram Altın" metninden önceliklidir.
    /// </summary>
    public static bool ShouldRouteHasAltinToDovizBalance(
        string? itemProductName,
        string? itemCategory,
        string? itemKarat,
        string? productName = null,
        string? productCategory = null,
        string? productKarat = null,
        string? productZiynetTipi = null)
    {
        if (IsKulceOr22AyarGramZiynet(itemProductName, itemCategory, itemKarat))
            return false;

        if (MatchesHasAltinLabel(itemProductName))
            return true;
        if (MatchesHasAltinLabel(itemCategory))
            return true;
        if (MatchesLegacyGramHasLabel(itemProductName))
            return true;

        if (IsKulceOr22AyarGramZiynet(productName, productCategory, productKarat, productZiynetTipi))
            return false;

        if (MatchesHasAltinLabel(productName) && !ContainsIndependentGramMarker(productName, productCategory, productZiynetTipi))
            return true;

        var karat = NormalizeFinanceToken(itemKarat ?? productKarat);
        var primaryName = NormalizeFinanceToken(itemProductName);
        if (string.IsNullOrEmpty(primaryName))
            primaryName = NormalizeFinanceToken(productName);

        if (!string.IsNullOrEmpty(primaryName)
            && MatchesHasAltinLabel(primaryName)
            && IsHasLikeKarat(karat)
            && !ContainsIndependentGramMarker(primaryName, itemCategory, productCategory))
            return true;

        return false;
    }

    internal static bool IsHasAltinZiynetAd(string? ad)
        => ShouldRouteHasAltinToDovizBalance(ad, null, null);

    private static bool MatchesHasAltinLabel(string? raw)
    {
        var s = NormalizeFinanceToken(raw);
        if (string.IsNullOrEmpty(s)) return false;
        if (s == "HAS") return true;
        return s.Contains("HAS ALTIN") || s.Contains("HASALTIN");
    }

    private static bool MatchesLegacyGramHasLabel(string? raw)
    {
        var s = NormalizeFinanceToken(raw);
        return s.Contains("GRAM ALTIN(HAS)") || s.Contains("GRAM ALTIN (HAS)");
    }

    private static bool IsHasLikeKarat(string? karat)
    {
        var k = NormalizeFinanceToken(karat);
        return k.Contains("24") || k.Contains("999") || k.Contains("HAS");
    }

    private static bool IsKulceOr22AyarGramZiynet(params string?[] parts)
    {
        foreach (var part in parts)
        {
            var s = NormalizeFinanceToken(part);
            if (string.IsNullOrEmpty(s)) continue;
            if (s.Contains("KULCE") || s.Contains("KÜLÇE")) return true;
            if ((s.Contains("22 AYAR") || s.Contains("22AYAR")) && (s.Contains("GR") || s.Contains("GRAM")))
                return true;
        }
        return false;
    }

    private static bool ContainsIndependentGramMarker(params string?[] parts)
    {
        foreach (var part in parts)
        {
            var s = NormalizeFinanceToken(part);
            if (string.IsNullOrEmpty(s)) continue;
            if (MatchesHasAltinLabel(s) || MatchesLegacyGramHasLabel(s)) continue;
            if (s == "GRAM" || s.Contains("GRAM ALTIN") || s.Contains("GRAM"))
                return true;
        }
        return false;
    }

    public const string RefSettleAlacak = "SETTLE_ALACAK";
    public const string RefSettleBorc = "SETTLE_BORC";

    public static bool IsSettleAlacakOffset(CustomerTransaction x)
        => string.Equals(x.RefType, RefSettleAlacak, StringComparison.OrdinalIgnoreCase);

    public static bool IsSettleBorcOffset(CustomerTransaction x)
        => string.Equals(x.RefType, RefSettleBorc, StringComparison.OrdinalIgnoreCase);

    public const string LedgerAlacak = "ALACAK";
    public const string LedgerBorc = "BORC";
    public const string LedgerSplitBorcFirst = "SPLIT";

    public static bool IsSplitBorcFirstMode(string? side)
        => string.Equals((side ?? "").Trim(), LedgerSplitBorcFirst, StringComparison.OrdinalIgnoreCase);

    public static bool IsLedgerAlacak(string? side)
        => string.Equals((side ?? "").Trim(), LedgerAlacak, StringComparison.OrdinalIgnoreCase);

    public static bool IsLedgerBorc(string? side)
        => string.Equals((side ?? "").Trim(), LedgerBorc, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeLedgerSide(string? side)
    {
        if (IsLedgerBorc(side)) return LedgerBorc;
        if (IsLedgerAlacak(side)) return LedgerAlacak;
        return "";
    }

    /// <summary>İşlem yönü: alacak sütunu (+) veya borç sütunu (−) net bakiye.</summary>
    public static string LedgerSideFromNetBalance(decimal netBalance)
        => netBalance < 0m ? LedgerBorc : LedgerAlacak;

    /// <summary>Kaynak sütundan düşüm: alacak → SETTLE_ALACAK, borç → SETTLE_BORC.</summary>
    public static (int Direction, string RefType, decimal BalanceDelta) BuildReductionLeg(string ledgerSide, decimal amount)
    {
        var qty = Math.Abs(amount);
        return IsLedgerBorc(ledgerSide)
            ? (1, RefSettleBorc, qty)
            : (-1, RefSettleAlacak, -qty);
    }

    /// <summary>Hedef sütuna ekleme: alacak → Direction +1, borç → Direction −1.</summary>
    public static (int Direction, string CariDurum, decimal BalanceDelta) BuildAdditionLeg(string ledgerSide, decimal amount)
    {
        var qty = Math.Abs(amount);
        return IsLedgerBorc(ledgerSide)
            ? (-1, "Borclu", -qty)
            : (1, "Alacakli", qty);
    }

    public static string ResolveVeresiyeLedgerSideAuto(decimal grossBorc, decimal grossAlacak)
    {
        var hasBorc = grossBorc > 0m;
        var hasAlacak = grossAlacak > 0m;
        if (hasBorc && !hasAlacak) return LedgerBorc;
        if (hasAlacak && !hasBorc) return LedgerAlacak;
        if (hasBorc && hasAlacak)
            return grossBorc >= grossAlacak ? LedgerBorc : LedgerAlacak;
        return LedgerBorc;
    }

    public static string ResolveEmanetLedgerSideAuto(decimal grossBorc, decimal grossAlacak)
    {
        var hasBorc = grossBorc > 0m;
        var hasAlacak = grossAlacak > 0m;
        if (hasAlacak && !hasBorc) return LedgerAlacak;
        if (hasBorc && !hasAlacak) return LedgerBorc;
        if (hasBorc && hasAlacak)
            return grossAlacak >= grossBorc ? LedgerAlacak : LedgerBorc;
        return LedgerAlacak;
    }

    /// <summary>Emanet döviz: alacak sütununa ekleme veya borç sütunundan düşüm.</summary>
    public static async Task ApplyEmanetDovizLegAsync(
        AppDbContext db,
        CustomerBalance bal,
        Guid tenantId,
        Guid customerId,
        Guid branchId,
        string unit,
        decimal amount,
        string? ledgerSideOverride,
        decimal? unitPriceTl,
        decimal? totalPriceTl,
        decimal? gram,
        string? ayar,
        decimal? hasEq,
        string refType,
        Guid? refId,
        string note,
        DateTime txDate,
        Guid batchId,
        CancellationToken ct,
        Action<CustomerBalance, string, decimal>? applyBalanceDelta = null)
    {
        var normalizedUnit = (unit ?? "TL").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedUnit)) normalizedUnit = "TL";
        var qty = decimal.Round(Math.Abs(amount), 6, MidpointRounding.AwayFromZero);
        if (qty <= 0m) return;

        var ledgerSide = NormalizeLedgerSide(ledgerSideOverride);
        if (string.IsNullOrEmpty(ledgerSide))
            ledgerSide = LedgerAlacak;

        if (IsLedgerAlacak(ledgerSide))
        {
            var (direction, cariDurum, balanceDelta) = BuildAdditionLeg(ledgerSide, qty);
            applyBalanceDelta?.Invoke(bal, normalizedUnit, balanceDelta);
            await AddTransactionAsync(
                db, tenantId, customerId, branchId,
                groupCode: "DOVIZ", itemName: normalizedUnit, itemType: null,
                quantity: qty, direction: direction,
                gram: gram, ayar: ayar, milyem: null, hasEq: hasEq,
                unitPriceTl: unitPriceTl, totalPriceTl: totalPriceTl,
                refType: refType, refId: refId, note: note, txDate: txDate, ct: ct,
                cariDurumOverride: cariDurum, batchId: batchId);
        }
        else
        {
            var (direction, settleRefType, balanceDelta) = BuildReductionLeg(ledgerSide, qty);
            applyBalanceDelta?.Invoke(bal, normalizedUnit, balanceDelta);
            await AddTransactionAsync(
                db, tenantId, customerId, branchId,
                groupCode: "DOVIZ", itemName: normalizedUnit, itemType: null,
                quantity: qty, direction: direction,
                gram: gram, ayar: ayar, milyem: null, hasEq: hasEq,
                unitPriceTl: unitPriceTl, totalPriceTl: totalPriceTl,
                refType: settleRefType, refId: refId, note: note, txDate: txDate, ct: ct,
                cariDurumOverride: "Borclu", batchId: batchId);
        }
    }

    public static void ApplyCustomerBalanceDelta(CustomerBalance b, string unit, decimal delta)
    {
        switch ((unit ?? "TL").Trim().ToUpperInvariant())
        {
            case "USD":
                b.BalanceUSD += delta;
                break;
            case "EUR":
                b.BalanceEUR += delta;
                break;
            case "GBP":
                b.BalanceGBP += delta;
                break;
            case "HAS":
                b.BalanceHAS += delta;
                break;
            case "GUMUS":
                break;
            default:
                b.BalanceTL += delta;
                break;
        }
    }

    public static async Task<(decimal Borc, decimal Alacak)> GetCustomerDovizGrossAsync(
        AppDbContext db,
        Guid tenantId,
        Guid customerId,
        Guid branchId,
        string unit,
        CancellationToken ct)
    {
        var normalizedUnit = (unit ?? "TL").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedUnit)) normalizedUnit = "TL";

        var rows = await db.CustomerTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CustomerId == customerId && x.BranchId == branchId
                        && !x.IsDeleted && !x.IsReversed && x.GroupCode == "DOVIZ"
                        && x.ItemName == normalizedUnit)
            .ToListAsync(ct);

        if (normalizedUnit == "HAS")
        {
            var hasZiynetRows = await db.CustomerTransactions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.CustomerId == customerId && x.BranchId == branchId
                            && !x.IsDeleted && !x.IsReversed && x.GroupCode == "ZIYNET")
                .ToListAsync(ct);
            var misclassified = hasZiynetRows
                .Where(x => IsHasAltinZiynetAd(NormalizeZiynetItemName(x.ItemName)))
                .ToList();
            rows = rows.Concat(misclassified).ToList();
        }

        return ComputeGrossColumns(rows);
    }

    public static async Task ApplyPurchaseVeresiyeDovizAsync(
        AppDbContext db,
        CustomerBalance bal,
        Guid tenantId,
        Guid customerId,
        Guid branchId,
        string unit,
        decimal unitAmount,
        decimal? totalPriceTl,
        Guid purchaseId,
        DateTime txDate,
        CancellationToken ct,
        string? ledgerSideOverride = null)
    {
        var normalizedUnit = (unit ?? "TL").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedUnit)) normalizedUnit = "TL";
        var qty = decimal.Round(Math.Abs(unitAmount), 6, MidpointRounding.AwayFromZero);
        if (qty <= 0m) return;

        decimal? unitPriceTl = normalizedUnit == "TL"
            ? 1m
            : (qty > 0m && totalPriceTl.HasValue && totalPriceTl.Value > 0m
                ? decimal.Round(totalPriceTl.Value / qty, 6, MidpointRounding.AwayFromZero)
                : null);

        if (IsSplitBorcFirstMode(ledgerSideOverride))
        {
            await ApplyPurchaseVeresiyeSplitBorcFirstAsync(
                db, bal, tenantId, customerId, branchId, normalizedUnit, qty, unitPriceTl, totalPriceTl,
                purchaseId, txDate, ct);
            return;
        }

        var ledgerSide = NormalizeLedgerSide(ledgerSideOverride);
        if (string.IsNullOrEmpty(ledgerSide))
            ledgerSide = LedgerAlacak;

        if (IsLedgerBorc(ledgerSide))
        {
            var (direction, refType, balanceDelta) = BuildReductionLeg(ledgerSide, qty);
            ApplyCustomerBalanceDelta(bal, normalizedUnit, balanceDelta);
            await AddTransactionAsync(
                db, tenantId, customerId, branchId,
                groupCode: "DOVIZ", itemName: normalizedUnit, itemType: null,
                quantity: qty, direction: direction,
                gram: normalizedUnit is "HAS" or "GUMUS" ? qty : null,
                ayar: normalizedUnit == "TL" ? null : normalizedUnit,
                milyem: null,
                hasEq: normalizedUnit == "HAS" ? qty : null,
                unitPriceTl: unitPriceTl, totalPriceTl: totalPriceTl,
                refType: refType, refId: purchaseId,
                note: "Musteriden alis veresiye - borc sütunu",
                txDate: txDate, ct: ct,
                cariDurumOverride: "Borclu");
            return;
        }

        var (addDir, cariDurum, addDelta) = BuildAdditionLeg(ledgerSide, qty);
        ApplyCustomerBalanceDelta(bal, normalizedUnit, addDelta);
        await AddTransactionAsync(
            db, tenantId, customerId, branchId,
            groupCode: "DOVIZ", itemName: normalizedUnit, itemType: null,
            quantity: qty, direction: addDir,
            gram: normalizedUnit is "HAS" or "GUMUS" ? qty : null,
            ayar: normalizedUnit == "TL" ? null : normalizedUnit,
            milyem: null,
            hasEq: normalizedUnit == "HAS" ? qty : null,
            unitPriceTl: unitPriceTl, totalPriceTl: totalPriceTl,
            refType: "PURCHASE", refId: purchaseId,
            note: "Musteriden alis veresiye - alacak kaydi",
            txDate: txDate, ct: ct,
            cariDurumOverride: cariDurum);
    }

    private static async Task ApplyPurchaseVeresiyeSplitBorcFirstAsync(
        AppDbContext db,
        CustomerBalance bal,
        Guid tenantId,
        Guid customerId,
        Guid branchId,
        string normalizedUnit,
        decimal qty,
        decimal? unitPriceTl,
        decimal? totalPriceTl,
        Guid purchaseId,
        DateTime txDate,
        CancellationToken ct)
    {
        var (grossBorc, _) = await GetCustomerDovizGrossAsync(db, tenantId, customerId, branchId, normalizedUnit, ct);
        var remaining = qty;

        var offsetBorc = Math.Min(grossBorc, remaining);
        if (offsetBorc > 0m)
        {
            var (direction, refType, balanceDelta) = BuildReductionLeg(LedgerBorc, offsetBorc);
            ApplyCustomerBalanceDelta(bal, normalizedUnit, balanceDelta);
            var settleTl = unitPriceTl.HasValue
                ? decimal.Round(offsetBorc * unitPriceTl.Value, 2, MidpointRounding.AwayFromZero)
                : totalPriceTl;
            await AddTransactionAsync(
                db, tenantId, customerId, branchId,
                groupCode: "DOVIZ", itemName: normalizedUnit, itemType: null,
                quantity: offsetBorc, direction: direction,
                gram: normalizedUnit is "HAS" or "GUMUS" ? offsetBorc : null,
                ayar: normalizedUnit == "TL" ? null : normalizedUnit,
                milyem: null,
                hasEq: normalizedUnit == "HAS" ? offsetBorc : null,
                unitPriceTl: unitPriceTl, totalPriceTl: settleTl,
                refType: refType, refId: purchaseId,
                note: "Musteriden hurda alisi veresiye - borc mahsubu",
                txDate: txDate, ct: ct,
                cariDurumOverride: "Borclu");
            remaining -= offsetBorc;
        }

        if (remaining <= 0m) return;

        var (addDir, cariDurum, addDelta) = BuildAdditionLeg(LedgerAlacak, remaining);
        ApplyCustomerBalanceDelta(bal, normalizedUnit, addDelta);
        var alacakTl = unitPriceTl.HasValue
            ? decimal.Round(remaining * unitPriceTl.Value, 2, MidpointRounding.AwayFromZero)
            : totalPriceTl;
        await AddTransactionAsync(
            db, tenantId, customerId, branchId,
            groupCode: "DOVIZ", itemName: normalizedUnit, itemType: null,
            quantity: remaining, direction: addDir,
            gram: normalizedUnit is "HAS" or "GUMUS" ? remaining : null,
            ayar: normalizedUnit == "TL" ? null : normalizedUnit,
            milyem: null,
            hasEq: normalizedUnit == "HAS" ? remaining : null,
            unitPriceTl: unitPriceTl, totalPriceTl: alacakTl,
            refType: "PURCHASE", refId: purchaseId,
            note: "Musteriden hurda alisi veresiye - alacak kaydi",
            txDate: txDate, ct: ct,
            cariDurumOverride: cariDurum);
    }

    /// <summary>
    /// Brüt borç/alacak: SETTLE_ALACAK ödemesi alacaktan düşer, SETTLE_BORC tahsilatı borçtan düşer;
    /// karşı sütuna yazılmaz.
    /// </summary>
    public static (decimal Borc, decimal Alacak) ComputeGrossColumns(IEnumerable<CustomerTransaction> rows)
    {
        var list = rows.ToList();
        var borc = list.Where(x => x.Direction < 0 && !IsSettleAlacakOffset(x)).Sum(x => Math.Abs(x.Quantity));
        borc -= list.Where(x => IsSettleBorcOffset(x)).Sum(x => Math.Abs(x.Quantity));

        var alacak = list.Where(x => x.Direction >= 0 && !IsSettleBorcOffset(x)).Sum(x => Math.Abs(x.Quantity));
        alacak -= list.Where(x => IsSettleAlacakOffset(x)).Sum(x => Math.Abs(x.Quantity));

        return (Math.Max(0m, borc), Math.Max(0m, alacak));
    }

    /// <summary>Finans özeti ile aynı ziynet ad normalizasyonu.</summary>
    public static string NormalizeZiynetItemName(string? raw)
    {
        var value = (raw ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value.Contains("KÜLÇE") || value.Contains("KULCE"))
            return "GRAM ALTIN(KÜLÇE)";
        if ((value.Contains("22 AYAR") || value.Contains("22AYAR")) &&
            (value.Contains("GR") || value.Contains("GRAM")))
            return "22 AYAR(GR)";
        if (value == "GRAM" || value.Contains("GRAM ALTIN (HAS)") || value.Contains("GRAM ALTIN(HAS)"))
            return "GRAM ALTIN(KÜLÇE)";
        return value;
    }

    public static string NormalizeZiynetTipDisplay(string? raw)
    {
        var txt = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(txt)) return "Yeni";
        if (string.Equals(txt, "yeni", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(txt, "new", StringComparison.OrdinalIgnoreCase))
            return "Yeni";
        if (string.Equals(txt, "eski", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(txt, "old", StringComparison.OrdinalIgnoreCase))
            return "Eski";
        return txt;
    }

    public static string NormalizeZiynetTipGroupingKey(string normalizedItemName, string? rawTip)
    {
        var item = (normalizedItemName ?? "").Trim().ToUpperInvariant();
        if (item == "GRAM ALTIN(KÜLÇE)" || item == "GRAM ALTIN(KULCE)")
            return "Yeni";
        return NormalizeZiynetTipDisplay(rawTip);
    }

    /// <summary>Defter satırı ile tahsilat/ödeme isteği aynı ziynet grubunda mı?</summary>
    public static bool ZiynetRowMatches(string? rowItemName, string? rowItemType, string? requestAd, string? requestTip)
    {
        var rowAd = NormalizeZiynetItemName(rowItemName);
        var reqAd = NormalizeZiynetItemName(requestAd);
        if (!string.Equals(rowAd, reqAd, StringComparison.OrdinalIgnoreCase))
            return false;
        var rowTip = NormalizeZiynetTipGroupingKey(rowAd, rowItemType);
        var reqTip = NormalizeZiynetTipGroupingKey(reqAd, requestTip);
        return string.Equals(rowTip, reqTip, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFinanceToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // Türkçe i-varyantlarını (noktasız ı / noktalı İ) önce sadeleştir; aksi halde
        // ToUpperInvariant "Has Altın" → "HAS ALTıN" üretir ve "HAS ALTIN" ile eşleşmez.
        var s = raw.Trim()
            .Replace('ı', 'i')
            .Replace('İ', 'i')
            .ToUpperInvariant();
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s;
    }
}
