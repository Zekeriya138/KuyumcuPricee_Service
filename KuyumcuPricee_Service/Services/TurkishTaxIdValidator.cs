namespace KUYUMCU.Price_Service.Services;

/// <summary>TCKN/VKN format ve kontrol hanesi doğrulaması.</summary>
public static class TurkishTaxIdValidator
{
    public static bool IsValid(string? taxNo)
    {
        var digits = NormalizeDigits(taxNo);
        return digits.Length switch
        {
            11 => IsValidTckn(digits),
            10 => IsValidVkn(digits),
            _ => false
        };
    }

    public static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(char.IsDigit).ToArray());
    }

    public static bool IsValidTckn(string digits)
    {
        if (digits.Length != 11 || digits[0] == '0') return false;
        if (!digits.All(char.IsDigit)) return false;

        var d = digits.Select(c => c - '0').ToArray();
        var oddSum = d[0] + d[2] + d[4] + d[6] + d[8];
        var evenSum = d[1] + d[3] + d[5] + d[7];
        var digit10 = ((oddSum * 7) - evenSum) % 10;
        if (digit10 < 0) digit10 += 10;
        if (d[9] != digit10) return false;

        var digit11 = d.Take(10).Sum() % 10;
        return d[10] == digit11;
    }

    public static bool IsValidVkn(string digits)
    {
        if (digits.Length != 10 || !digits.All(char.IsDigit)) return false;

        var v = digits.Select(c => c - '0').ToArray();
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var tmp = (v[i] + (9 - i)) % 10;
            var val = tmp == 0 ? 0 : (tmp * (int)Math.Pow(2, 9 - i)) % 9;
            if (tmp != 0 && val == 0) val = 9;
            sum += val;
        }

        var check = (10 - (sum % 10)) % 10;
        return v[9] == check;
    }
}
