using System.Globalization;

namespace KUYUMCU.Price_Service.Services;

/// <summary>
/// Vomsis SystemDate alanı Türkiye yerel saatinde gelir; UTC sunucuda yanlış yorumlanmaması için dönüştürülür.
/// </summary>
public static class VomsisDateHelper
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "dd-MM-yyyy HH:mm:ss",
        "dd.MM.yyyy HH:mm:ss",
        "dd.MM.yyyy HH:mm"
    ];

    private static readonly Lazy<TimeZoneInfo> TurkeyTimeZone = new(ResolveTurkeyTimeZone);

    public static DateTime? ParseSystemDateToUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParseExact(value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return ConvertTurkeyLocalToUtc(exact);

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return ConvertTurkeyLocalToUtc(parsed);

        return null;
    }

    private static DateTime ConvertTurkeyLocalToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, TurkeyTimeZone.Value);
    }

    private static TimeZoneInfo ResolveTurkeyTimeZone()
    {
        foreach (var id in new[] { "Turkey Standard Time", "Europe/Istanbul" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "Turkey",
            TimeSpan.FromHours(3),
            "Turkey",
            "Turkey");
    }
}
