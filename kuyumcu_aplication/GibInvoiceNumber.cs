using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace kuyumcu_application;

public static class GibInvoiceNumber
{
    private static readonly XNamespace CbcNs =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private static readonly Regex DocumentNumberPattern = new(
        @"\b([A-Z]{3})(20\d{2})(\d{9})\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    public static string Build(string? prefix, DateTime issueDate, Guid invoiceId)
    {
        var normalizedPrefix = NormalizePrefix(prefix);
        var serial = (int)(BitConverter.ToUInt32(invoiceId.ToByteArray(), 0) % 999_999_999) + 1;
        var serial9 = serial.ToString("000000000", CultureInfo.InvariantCulture);
        return $"{normalizedPrefix}{issueDate:yyyy}{serial9}";
    }

    public static string BuildFromSerial(string? prefix, DateTime issueDate, int serial)
    {
        var normalizedPrefix = NormalizePrefix(prefix);
        var clamped = Math.Clamp(serial, 1, 999_999_999);
        return $"{normalizedPrefix}{issueDate:yyyy}{clamped:000000000}";
    }

    public static bool TryExtractSerialParts(string? value, out string prefix, out int year, out int serial)
    {
        prefix = string.Empty;
        year = 0;
        serial = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length != 16)
            return false;

        prefix = compact[..3];
        if (!prefix.All(static c => c is >= 'A' and <= 'Z'))
            return false;

        if (!int.TryParse(compact.AsSpan(3, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year))
            return false;

        return int.TryParse(compact.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out serial)
               && serial > 0;
    }

    public static bool IsTrustedOutboundNumber(string? value, string? prefix, int year, int lastKnownSerial)
    {
        if (lastKnownSerial <= 0)
            return false;

        if (!TryExtractSerialParts(value, out var numberPrefix, out var numberYear, out var serial))
            return false;

        return string.Equals(NormalizePrefix(prefix), numberPrefix, StringComparison.Ordinal)
               && numberYear == year
               && serial == lastKnownSerial + 1;
    }

    public static string Resolve(string? sourceInvoiceNo, DateTime issueDate, Guid invoiceId, string? prefixOverride = null)
    {
        var expectedPrefix = string.IsNullOrWhiteSpace(prefixOverride)
            ? null
            : NormalizePrefix(prefixOverride);

        if (TryCompact(sourceInvoiceNo, issueDate, out var existing))
        {
            if (!string.IsNullOrWhiteSpace(expectedPrefix)
                && !existing.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                return Build(expectedPrefix, issueDate, invoiceId);
            }

            return existing;
        }

        return Build(prefixOverride ?? ExtractPrefixCandidate(sourceInvoiceNo), issueDate, invoiceId);
    }

    public static string ResolvePrefixForDocumentType(
        string? documentType,
        string? invoicePrefix,
        string? archivePrefix)
        => NormalizePrefix(
            string.Equals(documentType, "EFatura", StringComparison.OrdinalIgnoreCase)
                ? invoicePrefix
                : archivePrefix);

    public static bool IsEArchiveDocumentType(string? documentType)
        => !string.Equals(documentType, "EFatura", StringComparison.OrdinalIgnoreCase);

    public static string? TryBuildNextSequential(
        string prefix,
        DateTime issueDate,
        IEnumerable<string?> existingNumbers)
    {
        var normalizedPrefix = NormalizePrefix(prefix);
        var maxSerial = 0L;
        var matchedAny = false;
        foreach (var number in existingNumbers)
        {
            if (!TryCompact(number, issueDate, out var compact))
                continue;
            if (!compact.StartsWith(normalizedPrefix, StringComparison.Ordinal))
                continue;

            matchedAny = true;
            if (long.TryParse(compact.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var serial)
                && serial > maxSerial
                && !IsLikelyGeneratedSerial((int)Math.Min(serial, int.MaxValue)))
            {
                maxSerial = serial;
            }
        }

        if (!matchedAny)
            return null;

        var next = maxSerial + 1;
        if (next <= 0 || next > 999_999_999)
            return null;

        return $"{normalizedPrefix}{issueDate:yyyy}{next:000000000}";
    }

    public static bool IsLikelyGeneratedSerial(int serial, int anchorSerial = 0)
    {
        if (serial <= 0)
            return true;

        if (anchorSerial > 0)
            return serial > anchorSerial + 500;

        return serial > 100_000;
    }

    public static int GetMaxSerial(string prefix, int year, IEnumerable<string?> numbers, int anchorSerial = 0)
    {
        var normalizedPrefix = NormalizePrefix(prefix);
        var maxSerial = 0;
        foreach (var number in numbers)
        {
            if (!TryExtractSerialParts(number, out var numberPrefix, out var numberYear, out var serial))
                continue;
            if (!string.Equals(numberPrefix, normalizedPrefix, StringComparison.Ordinal))
                continue;
            if (numberYear != year)
                continue;
            if (IsLikelyGeneratedSerial(serial, anchorSerial))
                continue;
            if (serial > maxSerial)
                maxSerial = serial;
        }

        return maxSerial;
    }

    public static IEnumerable<string> ExtractKnownDocumentNumbers(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsed = new List<string>();

        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var identity in doc.Descendants().Where(e => e.Name.LocalName == "InvoiceIdentity"))
            {
                var number = identity.Attribute("Number")?.Value;
                if (!string.IsNullOrWhiteSpace(number))
                    parsed.Add(number);
            }

            foreach (var documentId in doc.Descendants().Where(e => e.Name.LocalName == "DocumentId"))
            {
                if (!string.IsNullOrWhiteSpace(documentId.Value))
                    parsed.Add(documentId.Value);
            }

            foreach (var idElement in doc.Descendants().Where(e =>
                         string.Equals(e.Name.LocalName, "ID", StringComparison.OrdinalIgnoreCase)
                         && string.Equals(e.Name.NamespaceName, CbcNs.NamespaceName, StringComparison.Ordinal)))
            {
                if (string.IsNullOrWhiteSpace(idElement.Value) || idElement.Value.Length != 16)
                    continue;
                parsed.Add(idElement.Value);
            }

            foreach (var invoiceInfo in doc.Descendants().Where(e => e.Name.LocalName == "InvoiceInfo"))
            {
                var invoice = invoiceInfo.Elements().FirstOrDefault(e => e.Name.LocalName == "Invoice");
                if (invoice is null)
                    continue;

                var id = TryReadDocumentId(invoice.ToString(SaveOptions.DisableFormatting));
                if (!string.IsNullOrWhiteSpace(id))
                    parsed.Add(id);
            }
        }
        catch
        {
            // ham metin taramasına düş
        }

        foreach (var number in parsed)
        {
            if (seen.Add(number))
                yield return number;
        }

        foreach (var number in ScanDocumentNumbersFromText(xml))
        {
            if (seen.Add(number))
                yield return number;
        }
    }

    public static IEnumerable<string> ScanDocumentNumbersFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (Match match in DocumentNumberPattern.Matches(text))
            yield return match.Value;
    }

    public static bool IsValid(string? value, DateTime issueDate)
        => TryCompact(value, issueDate, out _);

    public static string EnsureInUbl(
        string ublInner,
        string? invoiceNumber,
        DateTime? issueDate,
        Guid invoiceId,
        string? prefixOverride = null)
    {
        var resolvedIssueDate = (issueDate ?? TryReadIssueDate(ublInner) ?? DateTime.Now.Date).Date;
        string targetId;
        if (TryCompact(invoiceNumber, resolvedIssueDate, out var fromInvoiceNumber))
        {
            targetId = fromInvoiceNumber;
        }
        else if (TryExtractSerialParts(invoiceNumber, out var extractedPrefix, out var extractedYear, out var extractedSerial)
                 && extractedYear == resolvedIssueDate.Year
                 && !IsLikelyGeneratedSerial(extractedSerial))
        {
            targetId = BuildFromSerial(extractedPrefix, resolvedIssueDate, extractedSerial);
        }
        else
        {
            targetId = Build(prefixOverride ?? ExtractPrefixCandidate(invoiceNumber), resolvedIssueDate, invoiceId);
        }

        var currentId = TryReadDocumentId(ublInner);
        var needsIdUpdate = !string.Equals(currentId, targetId, StringComparison.Ordinal)
                            || !IsValid(currentId, resolvedIssueDate);

        var result = needsIdUpdate ? ReplaceDocumentId(ublInner, targetId) : ublInner;
        return ReplaceIssueDate(result, resolvedIssueDate);
    }

    public static string? PatchPayloadJson(
        string? payloadJson,
        string? invoiceNumber,
        Guid invoiceEntityId,
        DateTime? issueDateOverride = null,
        string? prefixOverride = null)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return payloadJson;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return payloadJson;

            string? ublXml = null;
            if (TryReadJsonString(root, "ublXml", out var xml) && !string.IsNullOrWhiteSpace(xml))
                ublXml = xml;
            else if (TryReadJsonString(root, "ublBase64", out var base64) && !string.IsNullOrWhiteSpace(base64))
                ublXml = Encoding.UTF8.GetString(Convert.FromBase64String(base64));

            if (string.IsNullOrWhiteSpace(ublXml))
                return payloadJson;

            var issueDate = issueDateOverride?.Date ?? TryReadIssueDate(ublXml) ?? DateTime.Now.Date;
            var invoiceNo = TryReadJsonString(root, "invoiceNo", out var no) ? no : invoiceNumber;
            var payloadPrefix = TryReadJsonString(root, "seriesPrefix", out var seriesPrefix) ? seriesPrefix : null;
            var payloadDocType = TryReadJsonString(root, "documentType", out var documentType) ? documentType : null;
            var effectivePrefix = prefixOverride
                                  ?? payloadPrefix
                                  ?? (string.IsNullOrWhiteSpace(payloadDocType)
                                      ? null
                                      : ResolvePrefixForDocumentType(payloadDocType, invoiceNumber, invoiceNumber));
            string resolvedNumber;
            if (!string.IsNullOrWhiteSpace(invoiceNumber)
                && TryExtractSerialParts(invoiceNumber, out var explicitPrefix, out var explicitYear, out var explicitSerial)
                && explicitYear == issueDate.Year
                && !IsLikelyGeneratedSerial(explicitSerial))
            {
                resolvedNumber = BuildFromSerial(effectivePrefix ?? explicitPrefix, issueDate, explicitSerial);
            }
            else
            {
                resolvedNumber = Resolve(invoiceNo ?? invoiceNumber, issueDate, invoiceEntityId, effectivePrefix);
            }

            var fixedUbl = EnsureInUbl(
                ublXml,
                resolvedNumber,
                issueDate,
                invoiceEntityId,
                effectivePrefix ?? ExtractPrefixCandidate(invoiceNo ?? invoiceNumber));

            var resolvedFromUbl = TryReadDocumentId(fixedUbl) ?? resolvedNumber;
            if (string.Equals(fixedUbl, ublXml, StringComparison.Ordinal)
                && string.Equals(invoiceNo, resolvedFromUbl, StringComparison.Ordinal))
            {
                return payloadJson;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    switch (prop.Name)
                    {
                        case "ublXml":
                            writer.WriteString("ublXml", fixedUbl);
                            break;
                        case "ublBase64":
                            writer.WriteString("ublBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes(fixedUbl)));
                            break;
                        case "invoiceNo":
                            writer.WriteString("invoiceNo", resolvedFromUbl);
                            break;
                        default:
                            prop.WriteTo(writer);
                            break;
                    }
                }

                if (!root.TryGetProperty("ublXml", out _) && !root.TryGetProperty("ublBase64", out _))
                    writer.WriteString("ublXml", fixedUbl);

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return payloadJson;
        }
    }

    public static DateTime? TryReadIssueDate(string ublInner)
    {
        try
        {
            var root = XDocument.Parse(ublInner).Root;
            if (root is null)
                return null;

            var issueDateElement = root.Elements(CbcNs + "IssueDate").FirstOrDefault()
                                   ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "IssueDate");
            if (issueDateElement is null)
                return null;

            if (DateTime.TryParse(issueDateElement.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed.Date;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static string RegenerateUuid(string ublInner)
    {
        try
        {
            var doc = XDocument.Parse(ublInner);
            var root = doc.Root;
            if (root is null)
                return ublInner;

            var uuidElement = root.Elements(CbcNs + "UUID").FirstOrDefault()
                              ?? root.Elements().FirstOrDefault(e =>
                                  string.Equals(e.Name.LocalName, "UUID", StringComparison.OrdinalIgnoreCase));
            if (uuidElement is null)
                return ublInner;

            uuidElement.SetValue(Guid.NewGuid().ToString("D"));
            return StripXmlDeclaration(doc.ToString(SaveOptions.DisableFormatting));
        }
        catch
        {
            return Regex.Replace(
                ublInner,
                @"<cbc:UUID>[^<]*</cbc:UUID>",
                $"<cbc:UUID>{Guid.NewGuid():D}</cbc:UUID>",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
        }
    }

    public static string SanitizeEmbeddedInvoice(string ublInner)
        => StripXmlDeclaration(ublInner).Trim();

    public static DateTime? TryReadPayloadIssueDate(string? payloadJson)
    {
        var raw = ReadJsonString(payloadJson, "invoiceDateUtc");
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed.ToLocalTime().Date;

        return null;
    }

    public static string ReplaceIssueDate(string ublInner, DateTime issueDate)
    {
        var formatted = issueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            var doc = XDocument.Parse(ublInner);
            var root = doc.Root;
            if (root is null)
                return ReplaceIssueDateWithRegex(ublInner, formatted);

            var issueDateElement = root.Elements(CbcNs + "IssueDate").FirstOrDefault()
                                   ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "IssueDate");
            if (issueDateElement is null)
                return ublInner;

            if (string.Equals(issueDateElement.Value, formatted, StringComparison.Ordinal))
                return StripXmlDeclaration(doc.ToString(SaveOptions.DisableFormatting));

            issueDateElement.SetValue(formatted);
            return StripXmlDeclaration(doc.ToString(SaveOptions.DisableFormatting));
        }
        catch
        {
            return ReplaceIssueDateWithRegex(ublInner, formatted);
        }
    }

    private static string ReplaceIssueDateWithRegex(string ublInner, string formatted)
    {
        var replaced = Regex.Replace(
            ublInner,
            @"(<cbc:IssueDate>)[^<]*(</cbc:IssueDate>)",
            $"$1{formatted}$2",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        return StripXmlDeclaration(replaced);
    }

    private static string? ReadJsonString(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryReadJsonString(doc.RootElement, name, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryReadDocumentId(string ublInner)
    {
        try
        {
            var root = XDocument.Parse(ublInner).Root;
            if (root is null || !string.Equals(root.Name.LocalName, "Invoice", StringComparison.OrdinalIgnoreCase))
                return null;

            return FindInvoiceIdElement(root)?.Value;
        }
        catch
        {
            return TryReadDocumentIdWithRegex(ublInner);
        }
    }

    public static string ReplaceDocumentId(string ublInner, string documentId)
    {
        try
        {
            var doc = XDocument.Parse(ublInner);
            var root = doc.Root;
            if (root is null)
                return ReplaceDocumentIdWithRegex(ublInner, documentId);

            var idElement = FindInvoiceIdElement(root);
            if (idElement is null)
                return ReplaceDocumentIdWithRegex(ublInner, documentId);

            idElement.SetValue(documentId);
            var serialized = doc.ToString(SaveOptions.DisableFormatting);
            return StripXmlDeclaration(serialized);
        }
        catch
        {
            return StripXmlDeclaration(ReplaceDocumentIdWithRegex(ublInner, documentId));
        }
    }

    public static string StripXmlDeclaration(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return xml;

        var trimmed = xml.TrimStart();
        if (!trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var idx = trimmed.IndexOf("?>", StringComparison.Ordinal);
        return idx >= 0 ? trimmed[(idx + 2)..].TrimStart() : trimmed;
    }

    private static XElement? FindInvoiceIdElement(XElement invoiceRoot)
    {
        foreach (var child in invoiceRoot.Elements())
        {
            if (!string.Equals(child.Name.LocalName, "ProfileID", StringComparison.OrdinalIgnoreCase))
                continue;

            var documentId = child.ElementsAfterSelf()
                .FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "ID", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Name.NamespaceName, CbcNs.NamespaceName, StringComparison.Ordinal));
            if (documentId is not null)
                return documentId;
        }

        return invoiceRoot.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, "ID", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Name.NamespaceName, CbcNs.NamespaceName, StringComparison.Ordinal) &&
            !string.Equals(e.Parent?.Name.LocalName, "PartyIdentification", StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryReadDocumentIdWithRegex(string ublInner)
    {
        var match = Regex.Match(
            ublInner,
            @"<cbc:ProfileID>[\s\S]*?</cbc:ProfileID>\s*<cbc:ID>(?<id>[^<]+)</cbc:ID>",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string ReplaceDocumentIdWithRegex(string ublInner, string documentId)
    {
        var replaced = Regex.Replace(
            ublInner,
            @"(<cbc:ProfileID>[\s\S]*?</cbc:ProfileID>\s*<cbc:ID>)[^<]*(</cbc:ID>)",
            $"$1{documentId}$2",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        if (!string.Equals(replaced, ublInner, StringComparison.Ordinal))
            return replaced;

        var match = Regex.Match(
            ublInner,
            @"<cbc:ID>(?<id>[^<]+)</cbc:ID>",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        if (!match.Success)
            return ublInner;

        return ublInner[..match.Index] + $"<cbc:ID>{documentId}</cbc:ID>" + ublInner[(match.Index + match.Length)..];
    }

    private static bool TryCompact(string? value, DateTime issueDate, out string compact)
    {
        compact = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length != 16)
            return false;

        var prefix = compact[..3];
        if (!prefix.All(static c => c is >= 'A' and <= 'Z'))
            return false;

        if (!int.TryParse(compact.AsSpan(3, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            return false;
        if (year != issueDate.Year)
            return false;

        var serialPart = compact[7..];
        if (!serialPart.All(char.IsDigit))
            return false;

        if (serialPart == "000000000")
            return false;

        return true;
    }

    private static string ExtractPrefixCandidate(string? sourceInvoiceNo)
    {
        if (string.IsNullOrWhiteSpace(sourceInvoiceNo))
            return "AUR";

        var compact = new string(sourceInvoiceNo.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length == 16 && compact[..3].All(static c => c is >= 'A' and <= 'Z'))
            return compact[..3];

        var segment = sourceInvoiceNo.Split('-', ' ', '_')[0];
        return string.IsNullOrWhiteSpace(segment) ? "AUR" : segment;
    }

    private static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return "AUR";

        var sb = new StringBuilder(3);
        foreach (var c in prefix.Trim())
        {
            var mapped = MapToAsciiLetter(char.ToUpperInvariant(c));
            if (mapped != '\0' && sb.Length < 3)
                sb.Append(mapped);
        }

        while (sb.Length < 3)
            sb.Append('X');

        return sb.ToString();
    }

    private static char MapToAsciiLetter(char c) => c switch
    {
        >= 'A' and <= 'Z' => c,
        'Ç' => 'C',
        'Ğ' => 'G',
        'İ' or 'I' or 'ı' => 'I',
        'Ö' => 'O',
        'Ş' => 'S',
        'Ü' => 'U',
        _ => '\0'
    };

    private static bool TryReadJsonString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
            {
                value = direct.GetString();
                return true;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (TryReadJsonString(prop.Value, name, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryReadJsonString(item, name, out value))
                    return true;
            }
        }

        return false;
    }
}
