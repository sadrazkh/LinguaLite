using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

if (builder.Environment.IsDevelopment() && !PostgresAppStore.HasConnectionString(builder.Configuration))
{
    builder.Services.AddSingleton<IAppStore, LocalFileAppStore>();
}
else
{
    builder.Services.AddSingleton<IAppStore, PostgresAppStore>();
}

builder.Services.AddHttpClient<OpenRouterCardService>();
builder.Services.AddHttpClient<TelegramBotService>();
builder.Services.AddHostedService<ReminderWorker>();

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            message = error?.Message ?? "خطای داخلی سرور رخ داد."
        });
    });
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/admin", StringComparison.OrdinalIgnoreCase)
        || context.Request.Path.Equals("/admin/", StringComparison.OrdinalIgnoreCase))
    {
        var htmlPath = Path.Combine(app.Environment.WebRootPath, "admin-panel", "index.html");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(await File.ReadAllTextAsync(htmlPath));
        return;
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/health/db", async (IAppStore store) =>
{
    await store.EnsureReadyAsync();
    return Results.Ok(new { status = "ok", database = store.ProviderName });
});

api.MapGet("/public-settings", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    var settings = await store.GetSettingsAsync();
    var publicBaseUrl = ResolvePublicBaseUrl(http, config, settings);
    return Results.Ok(new
    {
        TelegramBotUsername = ResolveBotUsername(config, settings),
        TelegramMiniAppUrl = ResolveMiniAppUrl(http, config, settings),
        PublicBaseUrl = publicBaseUrl,
        BotTokenConfigured = !string.IsNullOrWhiteSpace(config["TELEGRAM_BOT_TOKEN"])
    });
});

api.MapPost("/auth/browser-login", async (BrowserLoginRequest request, IAppStore store) =>
{
    var result = await store.RedeemBrowserLoginCodeAsync(request.Code);
    return result.Success
        ? Results.Ok(new { result.SessionToken, result.Profile })
        : Results.BadRequest(new { message = result.Message });
});

api.MapGet("/config", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var settings = await store.GetSettingsAsync();
    var openRouter = await OpenRouterOptions.FromAsync(config, store);
    var plan = await store.GetEffectivePlanAsync(profile.Plan);
    var cardUsage = await store.GetAiUsageAsync(profile.Id, profile.Plan, AiToolKind.Card);
    var dictionaryUsage = await store.GetAiUsageAsync(profile.Id, profile.Plan, AiToolKind.Dictionary);
    var correctionUsage = await store.GetAiUsageAsync(profile.Id, profile.Plan, AiToolKind.Correction);
    return Results.Ok(new
    {
        userId = profile.Id,
        profile.Source,
        profile.DisplayName,
        profile.TelegramId,
        profile.TelegramUsername,
        profile.LanguageLevel,
        profile.Plan,
        effectivePlan = plan,
        profile.IsActive,
        profile.Features,
        aiUsage = cardUsage,
        usage = new
        {
            card = cardUsage,
            dictionary = dictionaryUsage,
            correction = correctionUsage
        },
        storageProvider = store.ProviderName,
        adminEnabled = !string.IsNullOrWhiteSpace(config["ADMIN_TOKEN"]),
        openRouterModel = openRouter.DefaultModel,
        miniAppUrl = settings.TelegramMiniAppUrl,
        aiServerKeyConfigured = ResolveOpenRouterApiKeys(config).Count > 0,
        aiServerKeysCount = ResolveOpenRouterApiKeys(config).Count
    });
});

api.MapGet("/deck", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var deck = await store.GetDeckAsync(profile.Id);
    return Results.Ok(DeckSummary.From(deck));
});

api.MapGet("/cards/due", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var now = DateTimeOffset.UtcNow;
    var cards = (await store.GetDeckAsync(profile.Id)).Cards
        .Where(card => card.NextReviewAt <= now)
        .OrderBy(card => card.NextReviewAt)
        .ThenBy(card => card.Box)
        .Take(25)
        .ToList();

    return Results.Ok(cards.Select(FeedbackCardPresenter.ToReviewShape));
});

api.MapGet("/cards", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var deck = await store.GetDeckAsync(profile.Id);
    return Results.Ok(deck.Cards.OrderByDescending(card => card.CreatedAt).Select(FeedbackCardPresenter.ToReviewShape));
});

api.MapPost("/cards", async (HttpContext http, IConfiguration config, CreateCardRequest request, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (request.Type == CardType.Feedback && !profile.Features.FeedbackCards) return Results.Forbid();

    request = NormalizeFeedbackRequest(request);
    if (string.IsNullOrWhiteSpace(request.Front) || string.IsNullOrWhiteSpace(request.Back))
    {
        return Results.BadRequest(new { message = "روی کارت و پشت کارت را کامل وارد کنید." });
    }

    var plan = await store.GetEffectivePlanAsync(profile.Plan);
    if (!profile.Features.UnlimitedCards && plan.CardLimit > -1 && (await store.GetDeckAsync(profile.Id)).Cards.Count >= plan.CardLimit)
    {
        return Results.BadRequest(new { message = "سقف تعداد کارت‌های این پلن پر شده است." });
    }

    var card = new FlashCard
    {
        Id = Guid.NewGuid(),
        Front = request.Front.Trim(),
        Back = request.Back.Trim(),
        Example = request.Example?.Trim() ?? string.Empty,
        Prompt = request.Prompt?.Trim() ?? string.Empty,
        Answer = request.Answer?.Trim() ?? string.Empty,
        Notes = request.Notes?.Trim() ?? string.Empty,
        Type = request.Type,
        CreatedAt = DateTimeOffset.UtcNow,
        NextReviewAt = DateTimeOffset.UtcNow,
        Box = 1
    };

    await store.AddCardAsync(profile.Id, card);
    await store.RecordActivityAsync(profile.Id, ActivityKind.CardAdded);
    return Results.Created($"/api/cards/{card.Id}", FeedbackCardPresenter.ToReviewShape(card));
});

api.MapPut("/cards/{id:guid}", async (HttpContext http, IConfiguration config, Guid id, UpdateCardRequest updateRequest, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (updateRequest.Type == CardType.Feedback && !profile.Features.FeedbackCards)
    {
        return Results.Json(new { message = "این نوع کارت در پلن شما فعال نیست." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var normalized = NormalizeFeedbackRequest(new CreateCardRequest(
        updateRequest.Front,
        updateRequest.Back,
        updateRequest.Example,
        updateRequest.Prompt,
        updateRequest.Answer,
        updateRequest.Notes,
        updateRequest.Type));

    if (string.IsNullOrWhiteSpace(normalized.Front) || string.IsNullOrWhiteSpace(normalized.Back))
    {
        return Results.BadRequest(new { message = "روی کارت و پشت کارت را کامل وارد کنید." });
    }

    var card = await store.UpdateCardAsync(profile.Id, id, item =>
    {
        item.Front = normalized.Front.Trim();
        item.Back = normalized.Back.Trim();
        item.Example = normalized.Example?.Trim() ?? string.Empty;
        item.Prompt = normalized.Prompt?.Trim() ?? string.Empty;
        item.Answer = normalized.Answer?.Trim() ?? string.Empty;
        item.Notes = normalized.Notes?.Trim() ?? string.Empty;
        item.Type = normalized.Type;
    });

    return card is null
        ? Results.NotFound(new { message = "کارت پیدا نشد." })
        : Results.Ok(FeedbackCardPresenter.ToReviewShape(card));
});

api.MapPost("/cards/{id:guid}/review", async (
    HttpContext http,
    IConfiguration config,
    Guid id,
    ReviewRequest request,
    IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var card = await store.UpdateCardAsync(profile.Id, id, item =>
    {
        var now = DateTimeOffset.UtcNow;
        item.TotalReviews++;
        item.LastReviewedAt = now;

        if (request.Remembered)
        {
            item.CorrectReviews++;
            item.Box = Math.Min(5, item.Box + 1);
        }
        else
        {
            item.Box = 1;
        }

        item.NextReviewAt = now.Add(LeitnerSchedule.DelayFor(item.Box));
    });

    if (card is null) return Results.NotFound(new { message = "کارت پیدا نشد." });

    await store.RecordActivityAsync(profile.Id, ActivityKind.Review);
    return Results.Ok(card);
});

api.MapDelete("/cards/{id:guid}", async (HttpContext http, IConfiguration config, Guid id, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    return await store.DeleteCardAsync(profile.Id, id)
        ? Results.NoContent()
        : Results.NotFound(new { message = "کارت پیدا نشد." });
});

api.MapGet("/export", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (!profile.Features.ExportImport) return Results.Forbid();

    var deck = await store.GetDeckAsync(profile.Id);
    var json = JsonSerializer.Serialize(new ExportPayload(profile.Id, DateTimeOffset.UtcNow, deck.Cards), AppJsonOptions.CreateIndented());
    return Results.Text(json, "application/json; charset=utf-8", Encoding.UTF8);
});

api.MapPost("/import", async (HttpContext http, IConfiguration config, ImportRequest request, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (!profile.Features.ExportImport) return Results.Forbid();

    if (request.Cards.Count == 0)
    {
        return Results.BadRequest(new { message = "فایلی برای ایمپورت پیدا نشد." });
    }

    var count = await store.ImportCardsAsync(profile.Id, request.Cards, request.Mode);
    await store.RecordActivityAsync(profile.Id, ActivityKind.CardAdded, count);
    return Results.Ok(new { imported = count });
});

api.MapPost("/access/redeem", async (HttpContext http, IConfiguration config, RedeemCodeRequest request, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var result = await store.RedeemCodeAsync(profile.Id, request.Code);
    return result.Success ? Results.Ok(result.Profile) : Results.BadRequest(new { message = result.Message });
});

api.MapPut("/profile/preferences", async (HttpContext http, IConfiguration config, UpdateUserPreferencesRequest request, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var updated = await store.UpdateUserAsync(profile.Id, user =>
    {
        if (!string.IsNullOrWhiteSpace(request.LanguageLevel))
        {
            user.LanguageLevel = NormalizeLanguageLevel(request.LanguageLevel);
        }
    });

    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

api.MapPost("/ai/complete", async (AiCompleteRequest request, HttpContext http, IConfiguration config, IAppStore store, OpenRouterCardService ai) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (!profile.Features.Ai) return Results.Forbid();
    if (request.Type == CardType.Feedback && !profile.Features.FeedbackCards) return Results.Forbid();

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { message = "متن کارت یا فیدبک را وارد کنید." });
    }

    var apiKey = ResolveOpenRouterApiKey(http, config);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.BadRequest(new { message = "OPENROUTER_API_KEY را روی سرور بگذارید یا کلید را در تنظیمات اپ وارد کنید." });
    }

    var quota = await store.TryConsumeAiRequestAsync(profile.Id, profile.Plan, AiToolKind.Card);
    if (!quota.Allowed)
    {
        return Results.Json(new { message = quota.Message, usage = quota }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    request = request with { LanguageLevel = profile.LanguageLevel };
    var card = NormalizeFeedbackRequest(await ai.CompleteAsync(request, apiKey));
    await store.RecordActivityAsync(profile.Id, ActivityKind.AiCard);
    return Results.Ok(card);
});

api.MapPost("/ai/dictionary", async (DictionaryRequest request, HttpContext http, IConfiguration config, IAppStore store, OpenRouterCardService ai) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (!profile.Features.Dictionary)
    {
        return Results.Json(new { message = "دیکشنری هوشمند در پلن شما فعال نیست." }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { message = "کلمه یا عبارت را وارد کنید." });
    }

    var apiKey = ResolveOpenRouterApiKey(http, config);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.BadRequest(new { message = "کلید OpenRouter روی سرور یا تنظیمات اپ پیدا نشد." });
    }

    var quota = await store.TryConsumeAiRequestAsync(profile.Id, profile.Plan, AiToolKind.Dictionary);
    if (!quota.Allowed)
    {
        return Results.Json(new { message = quota.Message, usage = quota }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    request = request with { LanguageLevel = profile.LanguageLevel };
    var result = await ai.LookupDictionaryAsync(request, apiKey);
    await store.RecordActivityAsync(profile.Id, ActivityKind.AiDictionary);
    return Results.Ok(result);
});

api.MapPost("/ai/correction", async (CorrectionRequest request, HttpContext http, IConfiguration config, IAppStore store, OpenRouterCardService ai) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (!profile.Features.TextCorrection)
    {
        return Results.Json(new { message = "اصلاح متن در پلن شما فعال نیست." }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { message = "متن یا جمله را وارد کنید." });
    }

    var apiKey = ResolveOpenRouterApiKey(http, config);
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.BadRequest(new { message = "کلید OpenRouter روی سرور یا تنظیمات اپ پیدا نشد." });
    }

    var quota = await store.TryConsumeAiRequestAsync(profile.Id, profile.Plan, AiToolKind.Correction);
    if (!quota.Allowed)
    {
        return Results.Json(new { message = quota.Message, usage = quota }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    request = request with { LanguageLevel = profile.LanguageLevel };
    var result = await ai.CorrectTextAsync(request, apiKey);
    await store.RecordActivityAsync(profile.Id, ActivityKind.AiCorrection);
    return Results.Ok(result);
});

api.MapPost("/bot/webhook", async (JsonElement update, TelegramBotService bot) =>
{
    await bot.HandleUpdateAsync(update);
    return Results.Ok(new { ok = true });
});

var admin = api.MapGroup("/admin");

admin.MapGet("/users", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    return Results.Ok(await store.GetUsersAsync());
});

admin.MapGet("/user-metrics", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    return Results.Ok(await store.GetAdminUserMetricsAsync());
});

admin.MapPut("/users/{id}", async (HttpContext http, IConfiguration config, string id, AdminUpdateUserRequest request, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();

    var profile = await store.UpdateUserAsync(id, user =>
    {
        if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;
        if (!string.IsNullOrWhiteSpace(request.Plan)) user.Plan = request.Plan.Trim();
        if (request.Features is not null) user.Features = request.Features;
        if (request.RemindersEnabled.HasValue) user.RemindersEnabled = request.RemindersEnabled.Value;
        if (request.ReminderHour.HasValue) user.ReminderHour = request.ReminderHour.Value;
    });

    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

admin.MapPost("/codes", async (HttpContext http, IConfiguration config, CreateAccessCodeRequest request, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    var code = await store.CreateAccessCodeAsync(request);
    return Results.Ok(code);
});

admin.MapGet("/codes", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    return Results.Ok(await store.GetAccessCodesAsync());
});

admin.MapPut("/codes/{code}", async (HttpContext http, IConfiguration config, string code, UpdateAccessCodeRequest request, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    var updated = await store.UpdateAccessCodeAsync(code, request);
    return updated is null ? Results.NotFound(new { message = "کد پیدا نشد." }) : Results.Ok(updated);
});

admin.MapGet("/codes/usage", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    var users = await store.GetUsersAsync();
    var usage = users
        .Where(user => !string.IsNullOrWhiteSpace(user.AccessCode))
        .GroupBy(user => user.AccessCode.Trim().ToUpperInvariant())
        .Select(group => new AccessCodeUsage
        {
            Code = group.Key,
            UsersCount = group.Count(),
            Users = group
                .OrderByDescending(user => user.LastSeenAt)
                .Select(user => new AccessCodeUser
                {
                    UserId = user.Id,
                    DisplayName = user.DisplayName,
                    TelegramId = user.TelegramId,
                    TelegramUsername = user.TelegramUsername,
                    Plan = user.Plan,
                    LastSeenAt = user.LastSeenAt
                })
                .ToList()
        })
        .OrderByDescending(item => item.UsersCount)
        .ThenBy(item => item.Code)
        .ToList();

    return Results.Ok(usage);
});

admin.MapGet("/plans", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    return Results.Ok(await store.GetPlansAsync());
});

admin.MapPut("/plans/{id}", async (HttpContext http, IConfiguration config, string id, UpsertPlanRequest request, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();

    var plan = new PlanDefinition
    {
        Id = id,
        Name = request.Name,
        BadgeColor = string.IsNullOrWhiteSpace(request.BadgeColor) ? "#16a34a" : request.BadgeColor,
        BadgeTextColor = string.IsNullOrWhiteSpace(request.BadgeTextColor) ? "#ffffff" : request.BadgeTextColor,
        Features = request.Features,
        AiDailyLimit = request.AiDailyLimit,
        AiMonthlyLimit = request.AiMonthlyLimit,
        DictionaryDailyLimit = request.DictionaryDailyLimit,
        DictionaryMonthlyLimit = request.DictionaryMonthlyLimit,
        CorrectionDailyLimit = request.CorrectionDailyLimit,
        CorrectionMonthlyLimit = request.CorrectionMonthlyLimit,
        CardLimit = request.CardLimit,
        SortOrder = request.SortOrder,
        IsDefault = request.IsDefault,
        CreatedAt = DateTimeOffset.UtcNow
    };
    return Results.Ok(await store.UpsertPlanAsync(plan));
});

admin.MapDelete("/plans/{id}", async (HttpContext http, IConfiguration config, string id, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    return await store.DeletePlanAsync(id) ? Results.NoContent() : Results.BadRequest(new { message = "پلن پیش‌فرض یا پلن ناموجود حذف نشد." });
});

admin.MapGet("/settings", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    var settings = await store.GetSettingsAsync();
    var openRouter = await OpenRouterOptions.FromAsync(config, store);
    var publicBaseUrl = ResolvePublicBaseUrl(http, config, settings);
    return Results.Ok(new
    {
        settings,
        effectiveOpenRouter = openRouter,
        effectiveTelegram = new
        {
            publicBaseUrl,
            telegramMiniAppUrl = ResolveMiniAppUrl(http, config, settings),
            telegramBotUsername = ResolveBotUsername(config, settings),
            botTokenConfigured = !string.IsNullOrWhiteSpace(config["TELEGRAM_BOT_TOKEN"]),
            webhookUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? string.Empty : $"{publicBaseUrl}/api/bot/webhook"
        }
    });
});

admin.MapPut("/settings", async (HttpContext http, IConfiguration config, UpdateSettingsRequest request, IAppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();

    var settings = await store.UpdateSettingsAsync(current =>
    {
        if (request.OpenRouterModel is not null) current.OpenRouterModel = request.OpenRouterModel.Trim();
        if (request.OpenRouterReferer is not null) current.OpenRouterReferer = request.OpenRouterReferer.Trim();
        if (request.PublicBaseUrl is not null) current.PublicBaseUrl = request.PublicBaseUrl.Trim().TrimEnd('/');
        if (request.TelegramBotUsername is not null) current.TelegramBotUsername = request.TelegramBotUsername.Trim().TrimStart('@');
        if (request.TelegramMiniAppUrl is not null) current.TelegramMiniAppUrl = request.TelegramMiniAppUrl.Trim();
        if (request.BotEnabled.HasValue) current.BotEnabled = request.BotEnabled.Value;
        if (request.RemindersEnabled.HasValue) current.RemindersEnabled = request.RemindersEnabled.Value;
        if (request.ReminderHour.HasValue) current.ReminderHour = Math.Clamp(request.ReminderHour.Value, 0, 23);
    });

    return Results.Ok(settings);
});

admin.MapPost("/bot/set-webhook", async (HttpContext http, IConfiguration config, IAppStore store, TelegramBotService bot) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    var settings = await store.GetSettingsAsync();
    var baseUrl = ResolvePublicBaseUrl(http, config, settings);
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        return Results.BadRequest(new { message = "Public Base URL مشخص نیست. دامنه اپ را در تنظیمات ادمین یا PUBLIC_BASE_URL وارد کن." });
    }

    if (string.IsNullOrWhiteSpace(settings.PublicBaseUrl) || string.IsNullOrWhiteSpace(settings.TelegramMiniAppUrl))
    {
        await store.UpdateSettingsAsync(current =>
        {
            if (string.IsNullOrWhiteSpace(current.PublicBaseUrl)) current.PublicBaseUrl = baseUrl;
            if (string.IsNullOrWhiteSpace(current.TelegramMiniAppUrl)) current.TelegramMiniAppUrl = baseUrl;
        });
    }

    return Results.Ok(await bot.SetWebhookAsync($"{baseUrl}/api/bot/webhook"));
});

admin.MapGet("/bot/status", async (HttpContext http, IConfiguration config, IAppStore store, TelegramBotService bot) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    var settings = await store.GetSettingsAsync();
    var baseUrl = ResolvePublicBaseUrl(http, config, settings);
    var webhookUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : $"{baseUrl}/api/bot/webhook";
    return Results.Ok(await bot.GetStatusAsync(webhookUrl));
});

admin.MapPost("/broadcast", async (HttpContext http, IConfiguration config, AdminBroadcastRequest request, IAppStore store, TelegramBotService bot) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { message = "متن پیام را وارد کن." });
    }

    var users = ApplyBroadcastFilter(await store.GetUsersAsync(), request).ToList();
    var settings = await store.GetSettingsAsync();
    var sent = 0;
    var skipped = 0;
    var failed = 0;
    var errors = new List<string>();

    foreach (var user in users)
    {
        if (!user.TelegramChatId.HasValue)
        {
            skipped++;
            continue;
        }

        try
        {
            await bot.SendAdminMessageAsync(user, request.Message.Trim(), settings);
            sent++;
        }
        catch (Exception ex)
        {
            failed++;
            errors.Add($"{user.Id}: {ex.Message}");
        }
    }

    return Results.Ok(new AdminBroadcastResult(users.Count, sent, skipped, failed, errors.Take(20).ToList()));
});

app.Run();

static async Task<UserProfile?> RequireUserAsync(HttpContext http, IConfiguration config, IAppStore store)
{
    var sessionToken = http.Request.Headers["X-Session-Token"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(sessionToken))
    {
        var sessionProfile = await store.GetUserBySessionTokenAsync(sessionToken);
        if (sessionProfile is not null && sessionProfile.IsActive)
        {
            await store.RecordActivityAsync(sessionProfile.Id, ActivityKind.Seen);
            return sessionProfile;
        }
    }

    var identity = TelegramUserResolver.Resolve(http, config);
    if (!identity.IsAuthorized) return null;

    var profile = await store.GetOrCreateUserAsync(identity);
    if (!profile.IsActive) return null;

    await store.RecordActivityAsync(profile.Id, ActivityKind.Seen);
    return profile;
}

static bool IsAdmin(HttpContext http, IConfiguration config)
{
    var expected = config["ADMIN_TOKEN"];
    if (string.IsNullOrWhiteSpace(expected)) return false;

    var actual = http.Request.Headers["X-Admin-Token"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(actual)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
}

static IEnumerable<UserProfile> ApplyBroadcastFilter(IEnumerable<UserProfile> users, AdminBroadcastRequest request)
{
    var query = users;
    if (request.Audience.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        return query.OrderByDescending(user => user.LastSeenAt);
    }

    if (request.Audience.Equals("selected", StringComparison.OrdinalIgnoreCase))
    {
        var ids = (request.UserIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return query
            .Where(user => ids.Contains(user.Id))
            .OrderByDescending(user => user.LastSeenAt);
    }

    if (!string.IsNullOrWhiteSpace(request.Plan))
    {
        query = query.Where(user => user.Plan.Equals(request.Plan.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    if (request.IsActive.HasValue)
    {
        query = query.Where(user => user.IsActive == request.IsActive.Value);
    }

    if (!string.IsNullOrWhiteSpace(request.Source))
    {
        query = query.Where(user => user.Source.Equals(request.Source.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(request.AccessCode))
    {
        query = query.Where(user => user.AccessCode.Equals(request.AccessCode.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(request.Search))
    {
        var search = request.Search.Trim();
        query = query.Where(user =>
            user.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || user.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || user.TelegramId.Contains(search, StringComparison.OrdinalIgnoreCase)
            || user.TelegramUsername.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    return query.OrderByDescending(user => user.LastSeenAt);
}

static string? ResolveOpenRouterApiKey(HttpContext http, IConfiguration config)
{
    var apiKey = http.Request.Headers["X-OpenRouter-Api-Key"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(apiKey)) return apiKey.Trim();

    var keys = ResolveOpenRouterApiKeys(config);
    if (keys.Count == 0) return null;
    if (keys.Count == 1) return keys[0];
    return keys[RandomNumberGenerator.GetInt32(keys.Count)];
}

static string NormalizeLanguageLevel(string? value)
{
    var normalized = (value ?? "B1").Trim().ToUpperInvariant();
    return normalized is "A1" or "A2" or "B1" or "B2" or "C1" or "C2" ? normalized : "B1";
}

static List<string> ResolveOpenRouterApiKeys(IConfiguration config)
{
    var values = new[] { config["OPENROUTER_API_KEYS"], config["OPENROUTER_API_KEY"] };
    return values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => value!.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .ToList();
}

static string ResolvePublicBaseUrl(HttpContext http, IConfiguration config, AppSettingsState settings)
{
    var configured = FirstValue(
        settings.PublicBaseUrl,
        config["PUBLIC_BASE_URL"],
        config["APP_PUBLIC_BASE_URL"],
        config["CAPROVER_PUBLIC_URL"]);

    if (!string.IsNullOrWhiteSpace(configured))
    {
        return CleanUrl(configured);
    }

    var forwardedHost = FirstHeaderPart(http.Request.Headers["X-Forwarded-Host"].FirstOrDefault());
    var forwardedProto = FirstHeaderPart(http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault());
    var host = string.IsNullOrWhiteSpace(forwardedHost) ? http.Request.Host.Value : forwardedHost;
    if (string.IsNullOrWhiteSpace(host)) return string.Empty;

    var scheme = string.IsNullOrWhiteSpace(forwardedProto) ? http.Request.Scheme : forwardedProto;
    if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !IsLocalHost(host))
    {
        scheme = "https";
    }

    return CleanUrl($"{scheme}://{host}");
}

static string ResolveMiniAppUrl(HttpContext http, IConfiguration config, AppSettingsState settings)
{
    var configured = FirstValue(settings.TelegramMiniAppUrl, config["TELEGRAM_MINI_APP_URL"]);
    return string.IsNullOrWhiteSpace(configured) ? ResolvePublicBaseUrl(http, config, settings) : CleanUrl(configured);
}

static string ResolveBotUsername(IConfiguration config, AppSettingsState settings) =>
    FirstValue(settings.TelegramBotUsername, config["TELEGRAM_BOT_USERNAME"])?.Trim().TrimStart('@') ?? string.Empty;

static string? FirstValue(params string?[] values) =>
    values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

static string FirstHeaderPart(string? value) =>
    value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

static string CleanUrl(string value) => value.Trim().TrimEnd('/');

static bool IsLocalHost(string host) =>
    host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
    || host.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
    || host.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase);

static CreateCardRequest NormalizeFeedbackRequest(CreateCardRequest request)
{
    if (request.Type != CardType.Feedback) return request;

    var parsed = FeedbackCardPresenter.Parse(request.Front);
    if (string.IsNullOrWhiteSpace(parsed.Wrong))
    {
        parsed = FeedbackCardPresenter.Parse(request.Prompt ?? string.Empty);
    }

    var correct = !string.IsNullOrWhiteSpace(parsed.Correct)
        ? parsed.Correct
        : request.Answer ?? string.Empty;
    var front = !string.IsNullOrWhiteSpace(parsed.Wrong)
        ? $"Correct this: {parsed.Wrong}"
        : request.Front.Trim();
    var answer = string.IsNullOrWhiteSpace(correct) ? request.Answer : correct;
    var backParts = new[]
    {
        string.IsNullOrWhiteSpace(correct) ? string.Empty : $"Correct: {correct}",
        string.IsNullOrWhiteSpace(request.Back) && string.IsNullOrWhiteSpace(correct)
            ? "این فیدبک نیاز به تکمیل توضیح دارد."
            : request.Back
    }.Where(part => !string.IsNullOrWhiteSpace(part));

    return request with
    {
        Front = front,
        Back = string.Join("\n\n", backParts),
        Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? front : request.Prompt,
        Answer = answer
    };
}
