using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace ApolloBreakdowns.Api;

// Adapted from VPF_Algorithm_Bot/Api/MiniAppAuth.cs.
public sealed record AppUser(long Id, string Name, bool IsAdmin);

public sealed class TelegramAuth(IConfiguration config)
{
    public AppUser? Authenticate(HttpRequest request)
    {
        if (config["Telegram:Mode"] == "Demo")
        {
            return request.Headers["X-Demo-User-Id"].ToString() switch {
                "1" => new(1, "Адміністратор (демо)", true),
                "2" => new(2, "Оператор (демо)", false),
                _ => new(1, "Тестовий користувач", true)
            };
        }
        var raw = request.Headers["X-Telegram-Init-Data"].ToString();
        var user = Validate(raw, config["Telegram:BotToken"] ?? "", DateTimeOffset.UtcNow,
            TimeSpan.FromHours(config.GetValue("Telegram:InitDataMaxAgeHours", 24)));
        if (user is null) return null;
        var admins = config.GetSection("Admin:TelegramIds").Get<long[]>() ?? [];
        return user with { IsAdmin = admins.Contains(user.Id) };
    }

    public static AppUser? Validate(string raw, string token, DateTimeOffset now, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 16000 || string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var values = QueryHelpers.ParseQuery(raw);
            if (values.Any(x => x.Value.Count != 1) || !values.TryGetValue("hash", out var hash)) return null;
            var check = string.Join('\n', values.Where(x => x.Key != "hash")
                .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
            var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(token));
            var actual = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(check));
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(hash.ToString()))) return null;
            if (!values.TryGetValue("auth_date", out var date) || !long.TryParse(date, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
            var age = now - DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (age > maxAge || age < TimeSpan.FromMinutes(-5)) return null;
            var user = JsonSerializer.Deserialize<InitUser>(values["user"].ToString());
            return user is { Id: > 0 } ? new(user.Id, string.Join(' ', new[] { user.FirstName, user.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))), false) : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or KeyNotFoundException) { return null; }
    }

    private sealed record InitUser(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("first_name")] string? FirstName,
        [property: JsonPropertyName("last_name")] string? LastName);
}
