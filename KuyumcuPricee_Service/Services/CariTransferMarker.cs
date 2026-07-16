using System.Globalization;

namespace KUYUMCU.Price_Service.Services;

internal static class CariTransferMarker
{
    public const string Prefix = "[CARI_TRANSFER]";

    public static string BuildNote(
        Guid transferId,
        string role,
        string peerKind,
        Guid peerId,
        Guid peerBatchId,
        string peerName,
        string? userDescription)
    {
        static string Safe(string? raw) => (raw ?? "").Replace("|", "/").Replace(";", ",").Trim();
        var link = $"{Prefix}ID={transferId:D}|ROLE={Safe(role)}|PEER_KIND={Safe(peerKind)}|PEER_ID={peerId:D}|PEER_BATCH={peerBatchId:D}|PEER_NAME={Safe(peerName)}";
        var desc = (userDescription ?? "").Trim();
        return string.IsNullOrWhiteSpace(desc) ? link : $"{desc} {link}";
    }

    public static bool TryParse(string? note, out Guid transferId, out string role, out string peerKind, out Guid peerId, out Guid peerBatchId, out string peerName)
    {
        transferId = Guid.Empty;
        role = "";
        peerKind = "";
        peerId = Guid.Empty;
        peerBatchId = Guid.Empty;
        peerName = "";
        var text = note ?? "";
        var idx = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        var segment = text.Substring(idx + Prefix.Length).Trim();
        if (segment.StartsWith("|", StringComparison.Ordinal)) segment = segment[1..];

        foreach (var part in segment.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim().ToUpperInvariant();
            var val = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "ID":
                    Guid.TryParse(val, out transferId);
                    break;
                case "ROLE":
                    role = val;
                    break;
                case "PEER_KIND":
                    peerKind = val;
                    break;
                case "PEER_ID":
                    Guid.TryParse(val, out peerId);
                    break;
                case "PEER_BATCH":
                    Guid.TryParse(val, out peerBatchId);
                    break;
                case "PEER_NAME":
                    peerName = val;
                    break;
            }
        }

        return transferId != Guid.Empty;
    }

    /// <summary>
    /// Son işlemler açıklama sütunu: teknik [CARI_TRANSFER] etiketini kaldırır, karşı taraf adını gösterir.
    /// </summary>
    public static string FormatDisplayNote(string? note)
    {
        var text = (note ?? "").Trim();
        if (text.Length == 0) return "";

        var idx = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text;

        if (!TryParse(text, out _, out var role, out _, out _, out _, out var peerName))
            return StripTransferMarker(text);

        if (!string.IsNullOrWhiteSpace(peerName))
        {
            return string.Equals(role, "SOURCE", StringComparison.OrdinalIgnoreCase)
                ? $"Transfer → {peerName}"
                : string.Equals(role, "TARGET", StringComparison.OrdinalIgnoreCase)
                    ? $"Transfer ← {peerName}"
                    : $"Transfer: {peerName}";
        }

        return "Transfer";
    }

    public static string StripTransferMarker(string? note)
    {
        var text = (note ?? "").Trim();
        var idx = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text;
        var before = text[..idx].Trim();
        return string.IsNullOrWhiteSpace(before) ? "" : before;
    }
}
