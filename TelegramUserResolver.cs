using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class TelegramUserResolver
{
    public static UserIdentity Resolve(HttpContext http, IConfiguration config)
    {
        var initData = http.Request.Headers["X-Telegram-Init-Data"].FirstOrDefault();
        var botToken = config["TELEGRAM_BOT_TOKEN"];

        if (!string.IsNullOrWhiteSpace(initData))
        {
            if (string.IsNullOrWhiteSpace(botToken) || IsValidInitData(initData, botToken))
            {
                var tgUser = ExtractTelegramUser(initData);
                if (tgUser is not null)
                {
                    return new UserIdentity($"tg_{tgUser.Id}", "telegram-miniapp", tgUser.Name, true, tgUser.Id, tgUser.Username, long.TryParse(tgUser.Id, out var chatId) ? chatId : null);
                }
            }

            if (!string.IsNullOrWhiteSpace(botToken))
            {
                return UserIdentity.Unauthorized();
            }
        }

        if (!string.IsNullOrWhiteSpace(botToken))
        {
            return UserIdentity.Unauthorized();
        }

        var devUserId = http.Request.Headers["X-Dev-User-Id"].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(devUserId)
            ? new UserIdentity($"dev_{devUserId}", "dev", "Local Dev", true)
            : new UserIdentity("dev_local", "local", "Local Dev", true);
    }

    private static bool IsValidInitData(string initData, string botToken)
    {
        var pairs = ParseQuery(initData);
        if (!pairs.TryGetValue("hash", out var hash)) return false;

        var dataCheckString = string.Join('\n', pairs
            .Where(pair => pair.Key != "hash")
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));

        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var calculatedHash = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString));
        var calculatedHashHex = Convert.ToHexString(calculatedHash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(calculatedHashHex),
            Encoding.UTF8.GetBytes(hash));
    }

    private static TelegramUser? ExtractTelegramUser(string initData)
    {
        var pairs = ParseQuery(initData);
        if (!pairs.TryGetValue("user", out var userJson)) return null;

        using var document = JsonDocument.Parse(userJson);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetRawText().Trim('"') : null;
        if (string.IsNullOrWhiteSpace(id)) return null;

        var name = root.TryGetProperty("first_name", out var firstName) ? firstName.GetString() ?? string.Empty : string.Empty;
        if (root.TryGetProperty("last_name", out var lastName) && !string.IsNullOrWhiteSpace(lastName.GetString()))
        {
            name = $"{name} {lastName.GetString()}".Trim();
        }

        var username = root.TryGetProperty("username", out var usernameElement) ? usernameElement.GetString() ?? string.Empty : string.Empty;
        return new TelegramUser(id, name, username);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace("+", " ")),
                parts => Uri.UnescapeDataString(parts[1].Replace("+", " ")));
    }
}
