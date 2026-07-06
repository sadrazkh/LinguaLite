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

builder.Services.AddSingleton<IAppStore, PostgresAppStore>();
builder.Services.AddHttpClient<OpenRouterCardService>();

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

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/health/db", async (IAppStore store) =>
{
    await store.EnsureReadyAsync();
    return Results.Ok(new { status = "ok", database = "postgres" });
});

api.MapGet("/config", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    if (!user.IsAuthorized) return Results.Unauthorized();

    var profile = await store.GetOrCreateUserAsync(user);
    return Results.Ok(new
    {
        userId = profile.Id,
        profile.Source,
        profile.DisplayName,
        profile.Plan,
        profile.IsActive,
        profile.Features,
        openRouterModel = OpenRouterOptions.From(config).DefaultModel,
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

    return Results.Ok(cards);
});

api.MapGet("/cards", async (HttpContext http, IConfiguration config, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var deck = await store.GetDeckAsync(profile.Id);
    return Results.Ok(deck.Cards.OrderByDescending(card => card.CreatedAt));
});

api.MapPost("/cards", async (HttpContext http, IConfiguration config, CreateCardRequest request, IAppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (request.Type == CardType.Feedback && !profile.Features.FeedbackCards) return Results.Forbid();

    if (string.IsNullOrWhiteSpace(request.Front) || string.IsNullOrWhiteSpace(request.Back))
    {
        return Results.BadRequest(new { message = "روی کارت و پشت کارت را کامل وارد کنید." });
    }

    if (!profile.Features.UnlimitedCards && (await store.GetDeckAsync(profile.Id)).Cards.Count >= 50)
    {
        return Results.Forbid();
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
    return Results.Created($"/api/cards/{card.Id}", card);
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
    return Results.Json(new ExportPayload(profile.Id, DateTimeOffset.UtcNow, deck.Cards), AppJsonOptions.CreateIndented());
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

    var card = await ai.CompleteAsync(request, apiKey);
    return Results.Ok(card);
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
