namespace GoHome.Web.Services;

public static class Phone
{
    /// <summary>Только цифры, кыргызский формат 996XXXXXXXXX. «0555 12-34-56» → «996555123456».</summary>
    public static string Normalize(string? raw)
    {
        var digits = new string((raw ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 10 && digits.StartsWith('0')) digits = "996" + digits[1..];
        else if (digits.Length == 9) digits = "996" + digits;
        return digits;
    }
}
