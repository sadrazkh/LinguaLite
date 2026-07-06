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

        var command = text.Trim();
        if (string.IsNullOrWhiteSpace(command) || command.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await SendMessageAsync(chatId, StartText(profile), settings);
            return;
        }

        if (command.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await SendMessageAsync(chatId, HelpText(), settings);
            return;
        }

        if (command.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
        {
            await SendStatusAsync(profile, chatId, settings);
            return;
        }

        if (command.StartsWith("/due", StringComparison.OrdinalIgnoreCase))
        {
            await SendDueAsync(profile, chatId, settings);
            return;
        }

        if (command.StartsWith("/code ", StringComparison.OrdinalIgnoreCase))
        {
            await RedeemCodeAsync(profile, chatId, command[6..], settings);
            return;
        }

        if (command.StartsWith("/remind_on", StringComparison.OrdinalIgnoreCase))
        {
            await SetReminderAsync(profile.Id, chatId, true, settings);
            return;
        }

        if (command.StartsWith("/remind_off", StringComparison.OrdinalIgnoreCase))
        {
            await SetReminderAsync(profile.Id, chatId, false, settings);
            return;
        }

        if (command.StartsWith("/add ", StringComparison.OrdinalIgnoreCase))
        {
            await AddWordCardFromBotAsync(profile, chatId, command[5..], settings);
            return;
        }

        if (command.StartsWith("/feedback ", StringComparison.OrdinalIgnoreCase))
        {
            await AddFeedbackCardFromBotAsync(profile, chatId, command[10..], settings);
            return;
        }

        await SendMessageAsync(chatId, HelpText(), settings);
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

    private static string StartText(UserProfile profile) =>
        $"سلام {profile.DisplayName}!\nاکانتت وصل شد و کارت‌ها با همین شناسه تلگرام ذخیره می‌شوند.\nبرای دیدن فرمان‌ها /help را بزن یا مینی‌اپ را باز کن.";

    private static string HelpText() => """
        فرمان‌های ربات:
        /status وضعیت اکانت
        /due کارت‌های آماده مرور
        /code LL-XXXX فعال‌سازی کد پلن
        /remind_on روشن کردن یادآوری
        /remind_off خاموش کردن یادآوری
        /add word | معنی
        /feedback جمله غلط -> جمله درست
        """;

    private async Task SendStatusAsync(UserProfile profile, long chatId, AppSettingsState settings)
    {
        var deck = await store.GetDeckAsync(profile.Id);
        var due = deck.Cards.Count(card => card.NextReviewAt <= DateTimeOffset.UtcNow);
        await SendMessageAsync(chatId,
            $"وضعیت اکانت:\nپلن: {profile.Plan}\nکارت‌ها: {deck.Cards.Count}\nآماده مرور: {due}\nیادآوری: {(profile.RemindersEnabled ? "روشن" : "خاموش")}",
            settings);
    }

    private async Task SendDueAsync(UserProfile profile, long chatId, AppSettingsState settings)
    {
        var deck = await store.GetDeckAsync(profile.Id);
        var dueCount = deck.Cards.Count(card => card.NextReviewAt <= DateTimeOffset.UtcNow);
        await SendMessageAsync(chatId,
            dueCount == 0 ? "فعلا کارت آماده مرور نداری." : $"امروز {dueCount} کارت برای مرور داری.",
            settings);
    }

    private async Task RedeemCodeAsync(UserProfile profile, long chatId, string code, AppSettingsState settings)
    {
        var result = await store.RedeemCodeAsync(profile.Id, code);
        await SendMessageAsync(chatId,
            result.Success ? $"کد فعال شد. پلن جدید: {result.Profile?.Plan}" : result.Message,
            settings);
    }

    private async Task SetReminderAsync(string userId, long chatId, bool enabled, AppSettingsState settings)
    {
        await store.UpdateUserAsync(userId, user => user.RemindersEnabled = enabled);
        await SendMessageAsync(chatId, enabled ? "یادآوری روشن شد." : "یادآوری خاموش شد.", settings);
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
            Back = string.IsNullOrWhiteSpace(parsed.Correct)
                ? "اصلاح و دلیل را در مینی‌اپ کامل کن."
                : $"Correct: {parsed.Correct}",
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
