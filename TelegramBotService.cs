using System.Net.Http.Json;
using System.Text.Json;

public sealed class TelegramBotService(HttpClient httpClient, IConfiguration configuration, IAppStore store)
{
    public async Task HandleUpdateAsync(JsonElement update)
    {
        var settings = await store.GetSettingsAsync();
        if (!settings.BotEnabled) return;
        if (!update.TryGetProperty("message", out var message)) return;
        if (!message.TryGetProperty("chat", out var chat)) return;
        if (!chat.TryGetProperty("id", out var chatIdElement)) return;

        var chatId = chatIdElement.GetInt64();
        var text = message.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
        var from = message.TryGetProperty("from", out var fromElement) ? fromElement : chat;
        var telegramId = from.TryGetProperty("id", out var idElement) ? idElement.GetRawText().Trim('"') : chatId.ToString();
        var username = from.TryGetProperty("username", out var usernameElement) ? usernameElement.GetString() ?? string.Empty : string.Empty;
        var firstName = from.TryGetProperty("first_name", out var firstNameElement) ? firstNameElement.GetString() ?? string.Empty : string.Empty;
        var lastName = from.TryGetProperty("last_name", out var lastNameElement) ? lastNameElement.GetString() ?? string.Empty : string.Empty;
        var displayName = $"{firstName} {lastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName)) displayName = username;

        var profile = await store.GetOrCreateUserAsync(new UserIdentity(
            $"tg_{telegramId}",
            "telegram-bot",
            displayName,
            true,
            telegramId,
            username,
            chatId));

        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await SendMessageAsync(chatId, "خوش آمدی. از دکمه زیر مینی‌اپ را باز کن، یا با /due مرورهای امروز را ببین.", settings);
            return;
        }

        if (text.StartsWith("/due", StringComparison.OrdinalIgnoreCase))
        {
            var deck = await store.GetDeckAsync(profile.Id);
            var dueCount = deck.Cards.Count(card => card.NextReviewAt <= DateTimeOffset.UtcNow);
            await SendMessageAsync(chatId, dueCount == 0 ? "فعلا کارت آماده مرور نداری." : $"امروز {dueCount} کارت برای مرور داری.", settings);
            return;
        }

        if (text.StartsWith("/add ", StringComparison.OrdinalIgnoreCase))
        {
            await AddWordCardFromBotAsync(profile, chatId, text[5..], settings);
            return;
        }

        if (text.StartsWith("/feedback ", StringComparison.OrdinalIgnoreCase))
        {
            await AddFeedbackCardFromBotAsync(profile, chatId, text[10..], settings);
            return;
        }

        await SendMessageAsync(chatId, "دستورهای ربات:\n/start\n/due\n/add word | معنی\n/feedback غلط -> درست", settings);
    }

    public async Task<object> SetWebhookAsync(string webhookUrl)
    {
        var token = configuration["TELEGRAM_BOT_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
        {
            return new { ok = false, message = "TELEGRAM_BOT_TOKEN تنظیم نشده است." };
        }

        using var response = await httpClient.PostAsJsonAsync($"https://api.telegram.org/bot{token}/setWebhook", new
        {
            url = webhookUrl,
            allowed_updates = new[] { "message" }
        });
        var body = await response.Content.ReadAsStringAsync();
        return new { ok = response.IsSuccessStatusCode, status = (int)response.StatusCode, body };
    }

    public async Task SendReminderAsync(UserProfile user, int dueCount, AppSettingsState settings)
    {
        if (!user.TelegramChatId.HasValue) return;
        await SendMessageAsync(user.TelegramChatId.Value, $"وقت مرور زبان است. {dueCount} کارت آماده داری.", settings);
    }

    private async Task AddWordCardFromBotAsync(UserProfile profile, long chatId, string input, AppSettingsState settings)
    {
        var parts = input.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            await SendMessageAsync(chatId, "فرمت درست: /add resilient | تاب‌آور", settings);
            return;
        }

        await store.AddCardAsync(profile.Id, new FlashCard
        {
            Id = Guid.NewGuid(),
            Front = parts[0],
            Back = parts[1],
            Type = CardType.Word,
            Box = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            NextReviewAt = DateTimeOffset.UtcNow,
            Notes = "Added from Telegram bot"
        });
        await SendMessageAsync(chatId, "کارت لغت اضافه شد.", settings);
    }

    private async Task AddFeedbackCardFromBotAsync(UserProfile profile, long chatId, string input, AppSettingsState settings)
    {
        var parsed = FeedbackCardPresenter.Parse(input);
        if (string.IsNullOrWhiteSpace(parsed.Wrong))
        {
            await SendMessageAsync(chatId, "فرمت پیشنهادی: /feedback I programmer -> I am a programmer", settings);
            return;
        }

        await store.AddCardAsync(profile.Id, new FlashCard
        {
            Id = Guid.NewGuid(),
            Front = $"Correct this: {parsed.Wrong}",
            Back = string.IsNullOrWhiteSpace(parsed.Correct) ? "اصلاح و دلیل را در مینی‌اپ کامل کن." : parsed.Correct,
            Answer = parsed.Correct,
            Prompt = $"Correct this sentence: {parsed.Wrong}",
            Notes = "Feedback added from Telegram bot",
            Type = CardType.Feedback,
            Box = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            NextReviewAt = DateTimeOffset.UtcNow
        });
        await SendMessageAsync(chatId, "کارت فیدبک اضافه شد.", settings);
    }

    private async Task SendMessageAsync(long chatId, string text, AppSettingsState settings)
    {
        var token = configuration["TELEGRAM_BOT_TOKEN"];
        if (string.IsNullOrWhiteSpace(token)) return;

        var miniAppUrl = string.IsNullOrWhiteSpace(settings.TelegramMiniAppUrl)
            ? settings.PublicBaseUrl
            : settings.TelegramMiniAppUrl;

        object? replyMarkup = string.IsNullOrWhiteSpace(miniAppUrl)
            ? null
            : new
            {
                inline_keyboard = new[]
                {
                    new object[]
                    {
                        new { text = "باز کردن اپ", web_app = new { url = miniAppUrl } }
                    }
                }
            };

        await httpClient.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", new
        {
            chat_id = chatId,
            text,
            reply_markup = replyMarkup
        });
    }
}
