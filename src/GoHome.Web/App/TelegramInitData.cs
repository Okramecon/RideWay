using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace GoHome.Web.App;

/// <summary>
/// Проверка initData Telegram Mini App:
/// secret = HMAC_SHA256(key: "WebAppData", data: botToken);
/// hash   = hex(HMAC_SHA256(key: secret, data: отсортированные "key=value" через \n, без hash)).
/// https://core.telegram.org/bots/webapps#validating-data-received-via-the-mini-app
/// </summary>
public static class TelegramInitData
{
    public record Result(long UserId, string? FirstName, string? LastName, string? StartParam);

    public static Result? Validate(string initData, string botToken, DateTimeOffset now, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(initData) || string.IsNullOrWhiteSpace(botToken)) return null;

        var fields = QueryHelpers.ParseQuery(initData).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        if (!fields.Remove("hash", out var hash) || string.IsNullOrEmpty(hash)) return null;

        var dataCheck = string.Join('\n', fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var computed = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheck));

        byte[] expected;
        try { expected = Convert.FromHexString(hash); }
        catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(computed, expected)) return null;

        if (!fields.TryGetValue("auth_date", out var authDateRaw) || !long.TryParse(authDateRaw, out var authDate)) return null;
        if (now - DateTimeOffset.FromUnixTimeSeconds(authDate) > maxAge) return null;

        if (!fields.TryGetValue("user", out var userJson)) return null;
        using var doc = JsonDocument.Parse(userJson);
        var root = doc.RootElement;
        return new Result(
            root.GetProperty("id").GetInt64(),
            root.TryGetProperty("first_name", out var fn) ? fn.GetString() : null,
            root.TryGetProperty("last_name", out var ln) ? ln.GetString() : null,
            fields.GetValueOrDefault("start_param"));
    }

    /// <summary>Сформировать подписанный initData (для тестов).</summary>
    public static string Sign(IDictionary<string, string> fields, string botToken)
    {
        var dataCheck = string.Join('\n', fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheck)));
        return string.Join('&', fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")) + $"&hash={hash}";
    }
}
