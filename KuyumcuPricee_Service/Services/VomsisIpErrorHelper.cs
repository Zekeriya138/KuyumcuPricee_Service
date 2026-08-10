using System.Text.RegularExpressions;

namespace KUYUMCU.Price_Service.Services;

internal static class VomsisIpErrorHelper
{
    private static readonly Regex JsonMessageIpRegex = new(
        @"""message""\s*:\s*""[^""]*?:\s*(\d{1,3}(?:\.\d{1,3}){3})""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TrailingIpRegex = new(
        @":\s*(\d{1,3}(?:\.\d{1,3}){3})\s*(?:""|$|\])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsIpBlockedError(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           (message.Contains("ip_not_found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("IP adresi", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ip address", StringComparison.OrdinalIgnoreCase));

    public static string? TryExtractRejectedIp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var withoutUrlSuffix = raw;
        var urlIdx = withoutUrlSuffix.IndexOf(" [URL:", StringComparison.OrdinalIgnoreCase);
        if (urlIdx >= 0)
            withoutUrlSuffix = withoutUrlSuffix[..urlIdx];

        var jsonMatch = JsonMessageIpRegex.Match(withoutUrlSuffix);
        if (jsonMatch.Success)
            return jsonMatch.Groups[1].Value;

        var trailingMatch = TrailingIpRegex.Match(withoutUrlSuffix);
        if (trailingMatch.Success)
            return trailingMatch.Groups[1].Value;

        foreach (Match match in Regex.Matches(withoutUrlSuffix, @"\b(\d{1,3}(?:\.\d{1,3}){3})\b"))
        {
            var ip = match.Groups[1].Value;
            if (IsIgnoredIp(ip))
                continue;
            return ip;
        }

        return null;
    }

    public static string BuildBlockedMessage(string rawMessage)
    {
        var rejectedIp = TryExtractRejectedIp(rawMessage);
        var ipPart = rejectedIp is not null
            ? $"Vomsis reddettiği internet çıkış IP'si: {rejectedIp}\n\n"
            : "";

        return ipPart +
               "Vomsis yalnızca panelde kayıtlı IP'lerden erişime izin verir.\n" +
               "127.0.0.1 veya modem iç IP'si değil; Vomsis'e giden gerçek dış IP kaydedilmelidir.\n" +
               "Yukarıdaki IP'yi Vomsis paneline ekleyip tekrar deneyin.\n\n" +
               "Teknik: " + TrimDebugSuffix(rawMessage);
    }

    private static string TrimDebugSuffix(string raw)
    {
        var urlIdx = raw.IndexOf(" [URL:", StringComparison.OrdinalIgnoreCase);
        return urlIdx >= 0 ? raw[..urlIdx].Trim() : raw.Trim();
    }

    private static bool IsIgnoredIp(string ip)
        => ip.StartsWith("127.", StringComparison.Ordinal) ||
           ip.StartsWith("10.", StringComparison.Ordinal) ||
           ip.StartsWith("192.168.", StringComparison.Ordinal) ||
           string.Equals(ip, "172.213.185.78", StringComparison.Ordinal);
}
