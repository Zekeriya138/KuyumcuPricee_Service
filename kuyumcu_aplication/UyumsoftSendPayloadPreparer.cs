using System.Text;
using System.Text.Json;

namespace kuyumcu_application;

public static class UyumsoftSendPayloadPreparer
{
    public static (string InvoiceNumber, string PayloadJson) PrepareForSend(
        string? payloadJson,
        string documentType,
        string? defaultInvoicePrefix,
        string? defaultArchivePrefix,
        DateTime issueDate,
        Guid invoiceEntityId,
        string? preferredInvoiceNumber = null,
        int lastKnownSeriesSerial = 0)
    {
        var prefix = GibInvoiceNumber.ResolvePrefixForDocumentType(
            documentType,
            defaultInvoicePrefix,
            defaultArchivePrefix);

        string? invoiceNumber = null;
        if (lastKnownSeriesSerial > 0)
        {
            invoiceNumber = GibInvoiceNumber.BuildFromSerial(prefix, issueDate, lastKnownSeriesSerial + 1);
        }
        else if (GibInvoiceNumber.IsTrustedOutboundNumber(
                     preferredInvoiceNumber,
                     prefix,
                     issueDate.Year,
                     lastKnownSeriesSerial))
        {
            invoiceNumber = preferredInvoiceNumber;
        }

        var patched = payloadJson ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(invoiceNumber))
        {
            patched = GibInvoiceNumber.PatchPayloadJson(
                patched,
                invoiceNumber,
                invoiceEntityId,
                issueDate,
                prefix) ?? patched;

            patched = InjectJsonFields(
                patched,
                prefix,
                documentType,
                defaultInvoicePrefix,
                defaultArchivePrefix,
                lastKnownSeriesSerial,
                invoiceNumber);
        }
        else
        {
            patched = InjectJsonFields(
                patched,
                prefix,
                documentType,
                defaultInvoicePrefix,
                defaultArchivePrefix,
                lastKnownSeriesSerial);
        }

        return (invoiceNumber ?? string.Empty, patched);
    }

    private static string InjectJsonFields(
        string json,
        string prefix,
        string documentType,
        string? defaultInvoicePrefix,
        string? defaultArchivePrefix,
        int lastKnownSeriesSerial,
        string invoiceNumber)
    {
        var fields = new List<(string Name, string? Value)>
        {
            ("seriesPrefix", prefix),
            ("documentType", documentType),
            ("invoiceSeriesPrefix", defaultInvoicePrefix),
            ("archiveSeriesPrefix", defaultArchivePrefix),
            ("resolvedInvoiceNumber", invoiceNumber)
        };

        if (lastKnownSeriesSerial > 0)
            fields.Add(("lastKnownSeriesSerial", lastKnownSeriesSerial.ToString()));

        return InjectJsonFields(json, fields.ToArray());
    }

    private static string InjectJsonFields(
        string json,
        string prefix,
        string documentType,
        string? defaultInvoicePrefix,
        string? defaultArchivePrefix,
        int lastKnownSeriesSerial)
    {
        var fields = new List<(string Name, string? Value)>
        {
            ("seriesPrefix", prefix),
            ("documentType", documentType),
            ("invoiceSeriesPrefix", defaultInvoicePrefix),
            ("archiveSeriesPrefix", defaultArchivePrefix),
            ("resolvedInvoiceNumber", null)
        };

        if (lastKnownSeriesSerial > 0)
            fields.Add(("lastKnownSeriesSerial", lastKnownSeriesSerial.ToString()));

        return InjectJsonFields(json, fields.ToArray());
    }

    private static string InjectJsonFields(string json, params (string Name, string? Value)[] fields)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (fields.Any(x =>
                                string.Equals(x.Name, prop.Name, StringComparison.OrdinalIgnoreCase)
                                && x.Name.Equals("resolvedInvoiceNumber", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var replacement = fields.FirstOrDefault(x =>
                            string.Equals(x.Name, prop.Name, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrWhiteSpace(replacement.Value))
                        {
                            writer.WriteString(prop.Name, replacement.Value);
                            written.Add(prop.Name);
                            continue;
                        }

                        prop.WriteTo(writer);
                        written.Add(prop.Name);
                    }
                }

                foreach (var field in fields)
                {
                    if (written.Contains(field.Name))
                        continue;

                    if (string.IsNullOrWhiteSpace(field.Value))
                        continue;

                    if (field.Name.Equals("lastKnownSeriesSerial", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(field.Value, out var serial))
                    {
                        writer.WriteNumber(field.Name, serial);
                    }
                    else
                    {
                        writer.WriteString(field.Name, field.Value);
                    }
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return json;
        }
    }
}
