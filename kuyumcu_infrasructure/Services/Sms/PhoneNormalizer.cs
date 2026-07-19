using System.Text.RegularExpressions;

namespace kuyumcu_infrastructure.Services.Sms;

internal static class PhoneNormalizer
{
    public static bool TryNormalizeTurkishMobile(string? value, out string digits)
    {
        digits = Regex.Replace(value ?? "", "[^0-9]", "");
        if (digits.StartsWith("90", StringComparison.Ordinal) && digits.Length == 12)
            digits = "0" + digits[2..];
        return Regex.IsMatch(digits, "^05[0-9]{9}$");
    }

    public static string ToNetgsmGsm(string digits05)
    {
        if (digits05.StartsWith("0", StringComparison.Ordinal))
            return "9" + digits05;
        return digits05;
    }

    public static string Mask(string digits05)
    {
        if (digits05.Length != 11) return "05*********";
        return digits05[..4] + "***" + digits05[^2..];
    }
}
