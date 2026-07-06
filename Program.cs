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

api.MapGet("/config", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    if (!user.IsAuthorized) return Results.Unauthorized();

    var profile = await store.GetOrCreateUserAsync(user);
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
        aiServerKeyConfigured = !string.IsNullOrWhiteSpace(config["OPENROUTER_API_KEY"])
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

    return card is null
        ? Results.NotFound(new { message = "کارت پیدا نشد." })
        : Results.Ok(card);
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
    return Results.Ok(new { imported = count });
});

api.MapPost("/access/redeem", async (HttpContext http, IConfiguration config, RedeemCodeRequest request, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var result = await store.RedeemCodeAsync(profile.Id, request.Code);
    return result.Success ? Results.Ok(result.Profile) : Results.BadRequest(new { message = result.Message });
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

    var apiKey = http.Request.Headers["X-OpenRouter-Api-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        apiKey = config["OPENROUTER_API_KEY"];
    }

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.BadRequest(new { message = "OPENROUTER_API_KEY را روی سرور بگذارید یا کلید را در تنظیمات اپ وارد کنید." });
    }

    var quota = await store.TryConsumeAiRequestAsync(profile.Id, profile.Plan, AiToolKind.Card);
    if (!quota.Allowed)
    {
        return Results.Json(new { message = quota.Message, usage = quota }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var card = NormalizeFeedbackRequest(await ai.CompleteAsync(request, apiKey));
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

    return Results.Ok(await ai.LookupDictionaryAsync(request, apiKey));
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

    return Results.Ok(await ai.CorrectTextAsync(request, apiKey));
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
    return Results.Ok(new { settings, effectiveOpenRouter = openRouter });
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
    var baseUrl = settings.PublicBaseUrl.TrimEnd('/');
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        return Results.BadRequest(new { message = "اول Public Base URL را در تنظیمات ادمین وارد کن." });
    }

    return Results.Ok(await bot.SetWebhookAsync($"{baseUrl}/api/bot/webhook"));
});

app.Run();

static async Task<UserProfile?> RequireUserAsync(HttpContext http, IConfiguration config, IAppStore store)
{
    var identity = TelegramUserResolver.Resolve(http, config);
    if (!identity.IsAuthorized) return null;

    var profile = await store.GetOrCreateUserAsync(identity);
    return profile.IsActive ? profile : null;
}

static bool IsAdmin(HttpContext http, IConfiguration config)
{
    var expected = config["ADMIN_TOKEN"];
    if (string.IsNullOrWhiteSpace(expected)) return false;

    var actual = http.Request.Headers["X-Admin-Token"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(actual)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
}

static string? ResolveOpenRouterApiKey(HttpContext http, IConfiguration config)
{
    var apiKey = http.Request.Headers["X-OpenRouter-Api-Key"].FirstOrDefault();
    return string.IsNullOrWhiteSpace(apiKey) ? config["OPENROUTER_API_KEY"] : apiKey;
}

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
