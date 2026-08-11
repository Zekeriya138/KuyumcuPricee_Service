using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace kuyumcu_infrastructure.Services;

/// <summary>
/// EDM validator uyumu için UBL düzenlemeleri.
/// Uyumsoft gönderimine uygulanmaz (ortak builder'da çağrılmaz).
/// </summary>
public static class EdmUblSchemaNormalizer
{
    private static readonly XNamespace CacNs = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace CbcNs = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    public static string Normalize(
        string xml,
        string? currency = null,
        int? lineCount = null,
        string? soleProprietorName = null,
        string? documentType = null)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null) return xml;

            EnsureProfileIdMatchesDocumentType(root, documentType);

            var signatureNodes = root.Elements().Where(e => e.Name.LocalName == "Signature").ToList();
            foreach (var node in signatureNodes)
                node.Remove();

            var cbcNs = CbcNs;
            var hasTaxCurrency = root.Elements().Any(e => e.Name == cbcNs + "TaxCurrencyCode");
            var hasLineCount = root.Elements().Any(e => e.Name == cbcNs + "LineCountNumeric");
            var resolvedCurrency = currency
                ?? root.Elements().FirstOrDefault(e => e.Name == cbcNs + "DocumentCurrencyCode")?.Value
                ?? "TRY";
            var resolvedLineCount = lineCount
                ?? root.Descendants().Count(e => e.Name.LocalName == "InvoiceLine");
            if (resolvedLineCount <= 0) resolvedLineCount = 1;

            var docCurrency = root.Elements().FirstOrDefault(e => e.Name == cbcNs + "DocumentCurrencyCode");
            if (docCurrency is not null && !hasTaxCurrency)
                docCurrency.AddAfterSelf(new XElement(cbcNs + "TaxCurrencyCode", resolvedCurrency));

            if (!hasLineCount)
            {
                var buyerRef = root.Elements().FirstOrDefault(e => e.Name == cbcNs + "BuyerReference");
                if (buyerRef is not null)
                    buyerRef.AddAfterSelf(new XElement(cbcNs + "LineCountNumeric", resolvedLineCount));
                else
                {
                    var taxCurrency = root.Elements().FirstOrDefault(e => e.Name == cbcNs + "TaxCurrencyCode");
                    if (taxCurrency is not null)
                        taxCurrency.AddAfterSelf(new XElement(cbcNs + "LineCountNumeric", resolvedLineCount));
                }
            }

            EnsureSupplierSoleProprietorDisplay(root, soleProprietorName);
            EnsureTcknPartiesHavePerson(root, soleProprietorName);

            // Gömülü UBL'de XML declaration olmamalı.
            return root.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return xml;
        }
    }

    public static string NormalizeBase64Ubl(
        string? ublBase64,
        string? currency = null,
        string? soleProprietorName = null,
        string? documentType = null)
    {
        if (string.IsNullOrWhiteSpace(ublBase64))
            return ublBase64 ?? string.Empty;

        try
        {
            var xml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ublBase64));
            xml = PatchProfileIdInXml(xml, documentType);
            var normalized = Normalize(xml, currency, soleProprietorName: soleProprietorName, documentType: documentType);
            normalized = PatchProfileIdInXml(normalized, documentType);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(normalized));
        }
        catch
        {
            return ublBase64;
        }
    }

    public static string PatchProfileIdInXml(string xml, string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType) || string.IsNullOrWhiteSpace(xml))
            return xml;

        var isEArchive = string.Equals(documentType, "EArsiv", StringComparison.OrdinalIgnoreCase);
        var expectedProfileId = isEArchive ? "EARSIVFATURA" : "TEMELFATURA";
        return Regex.Replace(
            xml,
            @"(<(?:cbc:)?ProfileID\b[^>]*>)[^<]*(</(?:cbc:)?ProfileID>)",
            m => $"{m.Groups[1].Value}{expectedProfileId}{m.Groups[2].Value}",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// EDM schematron: e-Fatura kanalında ProfileID=EARSIVFATURA geçersizdir (ve tersi).
    /// Outbox'taki bayat UBL ile güncel belge tipi uyuşmazsa gönderimden hemen önce düzeltir.
    /// </summary>
    private static void EnsureProfileIdMatchesDocumentType(XElement root, string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
            return;

        var isEArchive = string.Equals(documentType, "EArsiv", StringComparison.OrdinalIgnoreCase);
        var expectedProfileId = isEArchive ? "EARSIVFATURA" : "TEMELFATURA";

        var profileIdEl = root.Elements(CbcNs + "ProfileID").FirstOrDefault()
            ?? root.Elements().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, "ProfileID", StringComparison.OrdinalIgnoreCase));
        if (profileIdEl is null)
            return;

        if (!string.Equals(profileIdEl.Value, expectedProfileId, StringComparison.OrdinalIgnoreCase))
            profileIdEl.Value = expectedProfileId;
    }

    /// <summary>
    /// EDM Schematron: schemeID=TCKN ise cac:Person zorunludur (gönderici ve alıcı).
    /// </summary>
    private static void EnsureTcknPartiesHavePerson(XElement root, string? soleProprietorName)
    {
        foreach (var party in root.Descendants(CacNs + "Party").ToList())
        {
            if (!IsSupplierTcknParty(party))
                continue;

            if (party.Element(CacNs + "Person") is not null)
            {
                FixMisplacedSupplierPersonElement(party);
                continue;
            }

            var fallbackName =
                (!string.IsNullOrWhiteSpace(soleProprietorName) &&
                 party.Parent?.Name == CacNs + "AccountingSupplierParty")
                    ? soleProprietorName
                    : party.Element(CacNs + "PartyName")?.Element(CbcNs + "Name")?.Value;

            if (string.IsNullOrWhiteSpace(fallbackName))
                fallbackName = "AD SOYAD";

            var personNode = BuildPersonElement(fallbackName);
            InsertAfterSupplierPartyTaxScheme(party, personNode);
        }
    }

    /// <summary>
    /// EDM e-Arşiv PDF şablonu gönderici bloğunda Person elemanını göstermiyor.
    /// Şahıs firmasında firma adının altına ad soyadın görünmesi için PartyName'e ikinci satır eklenir.
    /// </summary>
    private static void EnsureSupplierSoleProprietorDisplay(XElement root, string? soleProprietorName)
    {
        var party = root.Descendants(CacNs + "AccountingSupplierParty").FirstOrDefault()?.Element(CacNs + "Party");
        if (party is null || !IsSupplierTcknParty(party))
            return;

        FixMisplacedSupplierPersonElement(party);

        var partyNameNode = party.Element(CacNs + "PartyName");
        var nameEl = partyNameNode?.Element(CbcNs + "Name");
        var personNode = party.Element(CacNs + "Person");

        if (personNode is null && !string.IsNullOrWhiteSpace(soleProprietorName))
        {
            personNode = BuildPersonElement(soleProprietorName);
            InsertAfterSupplierPartyTaxScheme(party, personNode);
        }

        if (personNode is null || nameEl is null)
            return;

        var personFull = BuildPersonFullName(personNode);
        if (string.IsNullOrWhiteSpace(personFull))
            return;

        var companyName = nameEl.Value?.Trim() ?? string.Empty;
        if (!ContainsPersonName(companyName, personFull))
        {
            // EDM e-Arşiv PDF şablonu gönderici Person elemanını göstermiyor; PartyName'e ikinci satır eklenir.
            nameEl.Value = string.IsNullOrWhiteSpace(companyName)
                ? personFull
                : $"{companyName}\n{personFull}";
        }

        if (!party.Elements(CacNs + "PartyLegalEntity").Any())
        {
            var legalEntity = new XElement(CacNs + "PartyLegalEntity",
                new XElement(CbcNs + "RegistrationName", personFull));
            InsertBeforeSupplierPerson(party, legalEntity);
        }
    }

    /// <summary>UBL-TR: Person, PostalAddress ve PartyTaxScheme'den sonra gelmelidir.</summary>
    private static void FixMisplacedSupplierPersonElement(XElement party)
    {
        var person = party.Element(CacNs + "Person");
        var postalAddress = party.Element(CacNs + "PostalAddress");
        if (person is null || postalAddress is null)
            return;

        var elements = party.Elements().ToList();
        if (elements.IndexOf(person) > elements.IndexOf(postalAddress))
            return;

        person.Remove();
        InsertAfterSupplierPartyTaxScheme(party, person);
    }

    private static void InsertAfterSupplierPartyTaxScheme(XElement party, XElement node)
    {
        var taxScheme = party.Element(CacNs + "PartyTaxScheme");
        if (taxScheme is null)
        {
            party.Element(CacNs + "PostalAddress")?.AddAfterSelf(node);
            return;
        }

        if (node.Name == CacNs + "Person")
        {
            var lastLegalEntity = party.Elements(CacNs + "PartyLegalEntity").LastOrDefault();
            if (lastLegalEntity is not null)
                lastLegalEntity.AddAfterSelf(node);
            else
                taxScheme.AddAfterSelf(node);
            return;
        }

        if (node.Name == CacNs + "PartyLegalEntity")
        {
            var person = party.Element(CacNs + "Person");
            if (person is not null)
                person.AddBeforeSelf(node);
            else
                taxScheme.AddAfterSelf(node);
            return;
        }

        taxScheme.AddAfterSelf(node);
    }

    private static void InsertBeforeSupplierPerson(XElement party, XElement node)
    {
        var person = party.Element(CacNs + "Person");
        if (person is not null)
        {
            person.AddBeforeSelf(node);
            return;
        }

        InsertAfterSupplierPartyTaxScheme(party, node);
    }

    private static bool IsSupplierTcknParty(XElement party)
    {
        foreach (var idEl in party.Elements(CacNs + "PartyIdentification").Elements(CbcNs + "ID"))
        {
            var scheme = idEl.Attribute("schemeID")?.Value;
            var digits = DigitsOnly(idEl.Value);
            if (string.Equals(scheme, "TCKN", StringComparison.OrdinalIgnoreCase) || digits.Length == 11)
                return true;
        }

        return false;
    }

    private static XElement BuildPersonElement(string personName)
    {
        var display = personName.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
        var parts = display.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var firstName = parts.Length > 0 ? parts[0] : "AD";
        var familyName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : firstName;
        if (string.IsNullOrWhiteSpace(familyName))
            familyName = "SOYAD";
        return new XElement(CacNs + "Person",
            new XElement(CbcNs + "FirstName", firstName),
            new XElement(CbcNs + "FamilyName", familyName));
    }

    private static string BuildPersonFullName(XElement personNode)
    {
        var firstName = personNode.Element(CbcNs + "FirstName")?.Value?.Trim();
        var familyName = personNode.Element(CbcNs + "FamilyName")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(familyName))
            return string.Empty;

        if (string.Equals(firstName, "AD", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(familyName, "SOYAD", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return $"{firstName} {familyName}".Trim();
    }

    private static bool ContainsPersonName(string companyName, string personFull)
    {
        if (string.IsNullOrWhiteSpace(companyName))
            return false;

        return companyName.Contains(personFull, StringComparison.OrdinalIgnoreCase)
               || companyName.Replace("\r", string.Empty).Split('\n').Any(line =>
                   string.Equals(line.Trim(), personFull, StringComparison.OrdinalIgnoreCase));
    }

    private static string DigitsOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return Regex.Replace(value, @"\D", string.Empty);
    }
}
