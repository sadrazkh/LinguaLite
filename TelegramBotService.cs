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
        if (command.StartsWith("/start login", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("/start=login", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
        {
            await SendBrowserLoginCodeAsync(profile, chatId, settings);
            return;
        }

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
            return new { ok = false, webhookUrl, message = "TELEGRAM_BOT_TOKEN تنظیم نشده است." };
        }

        using var response = await httpClient.PostAsJsonAsync($"https://api.telegram.org/bot{token}/setWebhook", new
        {
            url = webhookUrl,
            allowed_updates = new[] { "message" },
            drop_pending_updates = false
        });

        var body = await response.Content.ReadAsStringAsync();
        var telegramOk = IsTelegramOk(body);
        var commands = await SetMyCommandsAsync(token);

        return new
        {
            ok = response.IsSuccessStatusCode && telegramOk,
            status = (int)response.StatusCode,
            webhookUrl,
            message = telegramOk ? "Webhook ربات تنظیم شد." : TelegramDescription(body),
            body,
            commands
        };
    }

    public async Task<object> GetStatusAsync(string expectedWebhookUrl)
    {
        var token = configuration["TELEGRAM_BOT_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
        {
            return new
            {
                ok = false,
                expectedWebhookUrl,
                tokenConfigured = false,
                message = "TELEGRAM_BOT_TOKEN تنظیم نشده است."
            };
        }

        var me = await TelegramGetAsync(token, "getMe");
        var webhook = await TelegramGetAsync(token, "getWebhookInfo");

        return new
        {
            ok = me.Ok && webhook.Ok,
            expectedWebhookUrl,
            tokenConfigured = true,
            me,
            webhook
        };
    }

    public async Task SendReminderAsync(UserProfile user, int dueCount, AppSettingsState settings)
    {
        if (!user.TelegramChatId.HasValue) return;
        await SendMessageAsync(user.TelegramChatId.Value, $"وقت مرور زبان است. {dueCount} کارت آماده داری.", settings);
    }

    public async Task SendAdminMessageAsync(UserProfile user, string text, AppSettingsState settings)
    {
        if (!user.TelegramChatId.HasValue) return;
        if (string.IsNullOrWhiteSpace(configuration["TELEGRAM_BOT_TOKEN"]))
        {
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN تنظیم نشده است.");
        }
        await SendMessageAsync(user.TelegramChatId.Value, text, settings);
    }

    private static string StartText(UserProfile profile) =>
        $"سلام {profile.DisplayName}!\nاکانتت به LinguaLite وصل شد و کارت‌ها با همین شناسه تلگرام ذخیره می‌شوند.\nبرای ورود به نسخه مرورگر یا PWA دستور /login را بزن.";

    private static string HelpText() => """
        فرمان‌های ربات:
        /status وضعیت اکانت
        /login کد ورود PWA و مرورگر
        /due کارت‌های آماده مرور
        /code LL-XXXX فعال‌سازی کد پلن
        /remind_on روشن کردن یادآوری
        /remind_off خاموش کردن یادآوری
        /add word | معنی
        /feedback جمله غلط -> جمله درست
        """;

    private async Task SendStatusAsync(UserProfile profile, long chatId, AppSettingsState settings)
    {
        var summary = await store.GetDeckSummaryAsync(profile.Id);
        var activeCards = new { Count = summary.TotalCards };
        var due = summary.DueCards;
        await SendMessageAsync(chatId,
            $"وضعیت اکانت:\nپلن: {profile.Plan}\nکارت‌ها: {activeCards.Count}\nآماده مرور: {due}\nیادآوری: {(profile.RemindersEnabled ? "روشن" : "خاموش")}",
            settings);
    }

    private async Task SendDueAsync(UserProfile profile, long chatId, AppSettingsState settings)
    {
        var dueCount = (await store.GetDeckSummaryAsync(profile.Id)).DueCards;
        await SendMessageAsync(chatId,
            dueCount == 0 ? "فعلا کارت آماده مرور نداری." : $"امروز {dueCount} کارت برای مرور داری.",
            settings);
    }

    private async Task SendBrowserLoginCodeAsync(UserProfile profile, long chatId, AppSettingsState settings)
    {
        var code = await store.CreateBrowserLoginCodeAsync(profile.Id, TimeSpan.FromMinutes(10));
        await SendMessageAsync(chatId,
            $"کد ورود به نسخه مرورگر/PWA:\n<code>{code.Code}</code>\nروی کد بزن تا فقط همان کد کپی شود. این کد ۱۰ دقیقه اعتبار دارد و یک‌بارمصرف است.",
            settings,
            "HTML");
    }

    private async Task RedeemCodeAsync(UserProfile profile, long chatId, string code, AppSettingsState settings)
    {
        var result = await store.RedeemCodeAsync(profile.Id, code);
        if (result.Success && result.Profile is not null)
        {
            var plan = await store.GetEffectivePlanAsync(result.Profile.Plan);
            await SendMessageAsync(chatId, PlanActivationText(result.Profile, plan), settings);
            return;
        }

        await SendMessageAsync(chatId,
            result.Message,
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
            NextReviewAt = LeitnerSchedule.TodayUtc(),
            Notes = "Added from Telegram bot"
        });
        await store.RecordActivityAsync(profile.Id, ActivityKind.CardAdded);
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
            NextReviewAt = LeitnerSchedule.TodayUtc()
        });
        await store.RecordActivityAsync(profile.Id, ActivityKind.CardAdded);
        await SendMessageAsync(chatId, "کارت فیدبک اضافه شد.", settings);
    }

    private async Task SendMessageAsync(long chatId, string text, AppSettingsState settings, string? parseMode = null)
    {
        var token = configuration["TELEGRAM_BOT_TOKEN"];
        if (string.IsNullOrWhiteSpace(token)) return;

        var miniAppUrl = string.IsNullOrWhiteSpace(settings.TelegramMiniAppUrl)
            ? settings.PublicBaseUrl
            : settings.TelegramMiniAppUrl;

        miniAppUrl = miniAppUrl?.Trim();

        // اگر لینک بدون پروتکل ذخیره شده بود، HTTPS اضافه کن
        if (!string.IsNullOrWhiteSpace(miniAppUrl) &&
            !miniAppUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !miniAppUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            miniAppUrl = $"https://{miniAppUrl}";
        }

        var hasValidWebAppUrl =
            Uri.TryCreate(miniAppUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps;

        object? replyMarkup = hasValidWebAppUrl
            ? new
            {
                inline_keyboard = new[]
                {
                new object[]
                {
                    new { text = "باز کردن اپ", web_app = new { url = miniAppUrl } }
                }
                }
            }
            : null;

        object payload = string.IsNullOrWhiteSpace(parseMode)
            ? new { chat_id = chatId, text, reply_markup = replyMarkup }
            : new { chat_id = chatId, text, parse_mode = parseMode, reply_markup = replyMarkup };

        using var response = await httpClient.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", payload);

        var body = await response.Content.ReadAsStringAsync();

        // اگر با دکمه WebApp خطا خورد، دوباره بدون دکمه بفرست تا فانکشن‌های ربات نخوابن
        if (!response.IsSuccessStatusCode || !IsTelegramOk(body))
        {
            if (replyMarkup is not null)
            {
                object fallbackPayload = string.IsNullOrWhiteSpace(parseMode)
                    ? new { chat_id = chatId, text }
                    : new { chat_id = chatId, text, parse_mode = parseMode };

                using var fallbackResponse = await httpClient.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", fallbackPayload);

                var fallbackBody = await fallbackResponse.Content.ReadAsStringAsync();

                if (fallbackResponse.IsSuccessStatusCode && IsTelegramOk(fallbackBody))
                    return;

                throw new InvalidOperationException($"Telegram sendMessage failed: {TelegramDescription(fallbackBody)}");
            }

            throw new InvalidOperationException($"Telegram sendMessage failed: {TelegramDescription(body)}");
        }
    }


    //private async Task SendMessageAsync(long chatId, string text, AppSettingsState settings)
    //{
    //    var token = configuration["TELEGRAM_BOT_TOKEN"];
    //    if (string.IsNullOrWhiteSpace(token)) return;

    //    var miniAppUrl = string.IsNullOrWhiteSpace(settings.TelegramMiniAppUrl)
    //        ? settings.PublicBaseUrl
    //        : settings.TelegramMiniAppUrl;

    //    object? replyMarkup = string.IsNullOrWhiteSpace(miniAppUrl)
    //        ? null
    //        : new
    //        {
    //            inline_keyboard = new[]
    //            {
    //                new object[]
    //                {
    //                    new { text = "باز کردن اپ", web_app = new { url = miniAppUrl } }
    //                }
    //            }
    //        };

    //    using var response = await httpClient.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", new
    //    {
    //        chat_id = chatId,
    //        text,
    //        reply_markup = replyMarkup
    //    });

    //    var body = await response.Content.ReadAsStringAsync();
    //    if (!response.IsSuccessStatusCode || !IsTelegramOk(body))
    //    {
    //        throw new InvalidOperationException($"Telegram sendMessage failed: {TelegramDescription(body)}");
    //    }
    //}

    private async Task<TelegramApiRawResult> SetMyCommandsAsync(string token)
    {
        using var response = await httpClient.PostAsJsonAsync($"https://api.telegram.org/bot{token}/setMyCommands", new
        {
            commands = new[]
            {
                new { command = "start", description = "شروع و اتصال اکانت" },
                new { command = "login", description = "کد ورود PWA و مرورگر" },
                new { command = "status", description = "وضعیت اکانت" },
                new { command = "due", description = "کارت‌های آماده مرور" },
                new { command = "remind_on", description = "روشن کردن یادآوری" },
                new { command = "remind_off", description = "خاموش کردن یادآوری" }
            }
        });

        return await ReadRawTelegramResultAsync(response);
    }

    private async Task<TelegramApiRawResult> TelegramGetAsync(string token, string method)
    {
        using var response = await httpClient.GetAsync($"https://api.telegram.org/bot{token}/{method}");
        return await ReadRawTelegramResultAsync(response);
    }

    private static async Task<TelegramApiRawResult> ReadRawTelegramResultAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var ok = response.IsSuccessStatusCode && IsTelegramOk(body);
        return new TelegramApiRawResult(ok, (int)response.StatusCode, body, TelegramDescription(body));
    }

    private static bool IsTelegramOk(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string TelegramDescription(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("description", out var description))
            {
                return description.GetString() ?? body;
            }
        }
        catch
        {
            return body;
        }

        return body;
    }



    private static string PlanActivationText(UserProfile profile, PlanDefinition plan)
    {
        var featureList = EnabledFeatureLabels(profile.Features);
        var featuresText = featureList.Count == 0 ? "فعلا دسترسی ویژه‌ای فعال نیست." : string.Join("، ", featureList);

        return $"""
            کد فعال شد.
            پلن فعلی شما: {plan.Name}

            محدودیت‌های این پلن:
            کارت با AI: روزانه {FormatLimit(plan.AiDailyLimit)}، ماهانه {FormatLimit(plan.AiMonthlyLimit)}
            دیکشنری: روزانه {FormatLimit(plan.DictionaryDailyLimit)}، ماهانه {FormatLimit(plan.DictionaryMonthlyLimit)}
            اصلاح متن: روزانه {FormatLimit(plan.CorrectionDailyLimit)}، ماهانه {FormatLimit(plan.CorrectionMonthlyLimit)}
            سقف کارت‌ها: {FormatLimit(plan.CardLimit)}

            دسترسی‌های فعال:
            {featuresText}
            """;
    }

    private static string FormatLimit(int value) => value < 0 ? "نامحدود" : value.ToString();

    private static List<string> EnabledFeatureLabels(FeatureSet features)
    {
        var labels = new List<string>();
        if (features.Ai) labels.Add("تکمیل کارت با AI");
        if (features.Dictionary) labels.Add("دیکشنری هوشمند");
        if (features.TextCorrection) labels.Add("اصلاح متن");
        if (features.FeedbackCards) labels.Add("کارت فیدبک");
        if (features.ExportImport) labels.Add("ایمپورت و اکسپورت");
        if (features.UnlimitedCards) labels.Add("کارت نامحدود");
        return labels;
    }
}

public sealed record TelegramApiRawResult(bool Ok, int Status, string Body, string Description);
