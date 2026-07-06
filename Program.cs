using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});
builder.Services.AddSingleton<AppStore>();
builder.Services.AddHttpClient<OpenRouterCardService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/config", async (HttpContext http, IConfiguration config, AppStore store) =>
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

api.MapGet("/deck", async (HttpContext http, IConfiguration config, AppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var deck = await store.GetDeckAsync(profile.Id);
    return Results.Ok(DeckSummary.From(deck));
});

api.MapGet("/cards/due", async (HttpContext http, IConfiguration config, AppStore store) =>
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

api.MapGet("/cards", async (HttpContext http, IConfiguration config, AppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var deck = await store.GetDeckAsync(profile.Id);
    return Results.Ok(deck.Cards.OrderByDescending(card => card.CreatedAt));
});

api.MapPost("/cards", async (HttpContext http, IConfiguration config, CreateCardRequest request, AppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(request.Front) || string.IsNullOrWhiteSpace(request.Back))
    {
        return Results.BadRequest(new { message = "روی کارت و پشت کارت را کامل وارد کنید." });
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
    AppStore store) =>
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

api.MapDelete("/cards/{id:guid}", async (HttpContext http, IConfiguration config, Guid id, AppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    return await store.DeleteCardAsync(profile.Id, id)
        ? Results.NoContent()
        : Results.NotFound(new { message = "کارت پیدا نشد." });
});

api.MapGet("/export", async (HttpContext http, IConfiguration config, AppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (!profile.Features.ExportImport) return Results.Forbid();

    var deck = await store.GetDeckAsync(profile.Id);
    return Results.Json(new ExportPayload(profile.Id, DateTimeOffset.UtcNow, deck.Cards), new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    });
});

api.MapPost("/import", async (HttpContext http, IConfiguration config, ImportRequest request, AppStore store) =>
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

api.MapPost("/access/redeem", async (HttpContext http, IConfiguration config, RedeemCodeRequest request, AppStore store) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();

    var result = await store.RedeemCodeAsync(profile.Id, request.Code);
    return result.Success ? Results.Ok(result.Profile) : Results.BadRequest(new { message = result.Message });
});

api.MapPost("/ai/complete", async (AiCompleteRequest request, HttpContext http, IConfiguration config, AppStore store, OpenRouterCardService ai) =>
{
    var profile = await RequireUserAsync(http, config, store);
    if (profile is null) return Results.Unauthorized();
    if (!profile.Features.Ai) return Results.Forbid();

    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { message = "متن کارت را وارد کنید." });
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

    try
    {
        var card = await ai.CompleteAsync(request, apiKey);
        return Results.Ok(card);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

var admin = api.MapGroup("/admin");

admin.MapGet("/users", async (HttpContext http, IConfiguration config, AppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    return Results.Ok(await store.GetUsersAsync());
});

admin.MapPut("/users/{id}", async (HttpContext http, IConfiguration config, string id, AdminUpdateUserRequest request, AppStore store) =>
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

admin.MapPost("/codes", async (HttpContext http, IConfiguration config, CreateAccessCodeRequest request, AppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    var code = await store.CreateAccessCodeAsync(request);
    return Results.Ok(code);
});

admin.MapGet("/codes", async (HttpContext http, IConfiguration config, AppStore store) =>
{
    if (!IsAdmin(http, config)) return Results.Unauthorized();
    return Results.Ok(await store.GetAccessCodesAsync());
});

app.Run();

static async Task<UserProfile?> RequireUserAsync(HttpContext http, IConfiguration config, AppStore store)
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

public sealed class AppStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = AppJsonOptions.CreateIndented();

    private string DatabasePath
    {
        get
        {
            var dataDir = configuration["DATA_DIR"];
            var root = string.IsNullOrWhiteSpace(dataDir)
                ? Path.Combine(environment.ContentRootPath, "App_Data")
                : dataDir;
            return Path.Combine(root, "database.json");
        }
    }

    public async Task<UserProfile> GetOrCreateUserAsync(UserIdentity identity)
    {
        return await MutateAsync(db =>
        {
            if (!db.Users.TryGetValue(identity.StorageKey, out var profile))
            {
                profile = new UserProfile
                {
                    Id = identity.StorageKey,
                    Source = identity.Source,
                    DisplayName = identity.DisplayName,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow
                };
                db.Users[profile.Id] = profile;
                db.Decks[profile.Id] = DeckState.CreateSeed();
            }
            else
            {
                profile.Source = identity.Source;
                profile.DisplayName = string.IsNullOrWhiteSpace(identity.DisplayName) ? profile.DisplayName : identity.DisplayName;
                profile.LastSeenAt = DateTimeOffset.UtcNow;
            }

            return profile;
        });
    }

    public async Task<DeckState> GetDeckAsync(string userId)
    {
        var db = await LoadAsync();
        return db.Decks.TryGetValue(userId, out var deck) ? deck : new DeckState();
    }

    public async Task AddCardAsync(string userId, FlashCard card)
    {
        await MutateAsync(db =>
        {
            if (!db.Decks.TryGetValue(userId, out var deck))
            {
                deck = new DeckState();
                db.Decks[userId] = deck;
            }

            deck.Cards.Add(card);
            return true;
        });
    }

    public async Task<FlashCard?> UpdateCardAsync(string userId, Guid cardId, Action<FlashCard> update)
    {
        return await MutateAsync(db =>
        {
            if (!db.Decks.TryGetValue(userId, out var deck)) return null;
            var card = deck.Cards.FirstOrDefault(item => item.Id == cardId);
            if (card is null) return null;
            update(card);
            return card;
        });
    }

    public async Task<bool> DeleteCardAsync(string userId, Guid cardId)
    {
        return await MutateAsync(db => db.Decks.TryGetValue(userId, out var deck) && deck.Cards.RemoveAll(card => card.Id == cardId) > 0);
    }

    public async Task<int> ImportCardsAsync(string userId, List<FlashCard> cards, ImportMode mode)
    {
        return await MutateAsync(db =>
        {
            if (!db.Decks.TryGetValue(userId, out var deck))
            {
                deck = new DeckState();
                db.Decks[userId] = deck;
            }

            if (mode == ImportMode.Replace)
            {
                deck.Cards.Clear();
            }

            var existing = deck.Cards.Select(card => card.Id).ToHashSet();
            var imported = 0;
            foreach (var card in cards)
            {
                if (card.Id == Guid.Empty || existing.Contains(card.Id))
                {
                    card.Id = Guid.NewGuid();
                }

                card.CreatedAt = card.CreatedAt == default ? DateTimeOffset.UtcNow : card.CreatedAt;
                card.NextReviewAt = card.NextReviewAt == default ? DateTimeOffset.UtcNow : card.NextReviewAt;
                deck.Cards.Add(card);
                imported++;
            }

            return imported;
        });
    }

    public async Task<List<UserProfile>> GetUsersAsync()
    {
        var db = await LoadAsync();
        return db.Users.Values.OrderByDescending(user => user.LastSeenAt).ToList();
    }

    public async Task<UserProfile?> UpdateUserAsync(string id, Action<UserProfile> update)
    {
        return await MutateAsync(db =>
        {
            if (!db.Users.TryGetValue(id, out var user)) return null;
            update(user);
            return user;
        });
    }

    public async Task<AccessCode> CreateAccessCodeAsync(CreateAccessCodeRequest request)
    {
        return await MutateAsync(db =>
        {
            var code = new AccessCode
            {
                Code = string.IsNullOrWhiteSpace(request.Code) ? GenerateCode() : request.Code.Trim().ToUpperInvariant(),
                Plan = string.IsNullOrWhiteSpace(request.Plan) ? "Free" : request.Plan.Trim(),
                Features = request.Features ?? FeatureSet.AllEnabled(),
                MaxUses = Math.Max(1, request.MaxUses ?? 1),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.AccessCodes[code.Code] = code;
            return code;
        });
    }

    public async Task<List<AccessCode>> GetAccessCodesAsync()
    {
        var db = await LoadAsync();
        return db.AccessCodes.Values.OrderByDescending(code => code.CreatedAt).ToList();
    }

    public async Task<RedeemResult> RedeemCodeAsync(string userId, string codeText)
    {
        return await MutateAsync(db =>
        {
            var normalized = codeText.Trim().ToUpperInvariant();
            if (!db.AccessCodes.TryGetValue(normalized, out var code))
            {
                return RedeemResult.Fail("کد پیدا نشد.");
            }

            if (code.Uses >= code.MaxUses)
            {
                return RedeemResult.Fail("ظرفیت استفاده این کد تمام شده است.");
            }

            if (!db.Users.TryGetValue(userId, out var user))
            {
                return RedeemResult.Fail("کاربر پیدا نشد.");
            }

            user.Plan = code.Plan;
            user.Features = code.Features;
            user.IsActive = true;
            user.AccessCode = code.Code;
            code.Uses++;
            return RedeemResult.Ok(user);
        });
    }

    private async Task<AppDatabase> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            if (!File.Exists(DatabasePath))
            {
                var created = new AppDatabase();
                await SaveUnlockedAsync(created);
                return created;
            }

            await using var stream = File.OpenRead(DatabasePath);
            return await JsonSerializer.DeserializeAsync<AppDatabase>(stream, _jsonOptions) ?? new AppDatabase();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> MutateAsync<T>(Func<AppDatabase, T> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            AppDatabase db;
            if (File.Exists(DatabasePath))
            {
                await using var read = File.OpenRead(DatabasePath);
                db = await JsonSerializer.DeserializeAsync<AppDatabase>(read, _jsonOptions) ?? new AppDatabase();
            }
            else
            {
                db = new AppDatabase();
            }

            var result = mutate(db);
            await SaveUnlockedAsync(db);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveUnlockedAsync(AppDatabase db)
    {
        var json = JsonSerializer.Serialize(db, _jsonOptions);
        await File.WriteAllTextAsync(DatabasePath, json);
    }

    private static string GenerateCode()
    {
        Span<byte> bytes = stackalloc byte[5];
        RandomNumberGenerator.Fill(bytes);
        return $"LL-{Convert.ToHexString(bytes)}";
    }
}

public sealed class OpenRouterCardService(HttpClient httpClient, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = AppJsonOptions.CreateCompact();

    public async Task<CreateCardRequest> CompleteAsync(AiCompleteRequest request, string apiKey)
    {
        var options = OpenRouterOptions.From(configuration);
        var type = request.Type ?? CardType.Word;
        var prompt = type == CardType.Feedback
            ? FeedbackPrompt(request.Text)
            : StandardPrompt(request.Text, type);

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        message.Headers.Add("HTTP-Referer", options.Referer);
        message.Headers.Add("X-OpenRouter-Title", "LinguaLite");
        message.Content = JsonContent.Create(new
        {
            model = options.DefaultModel,
            temperature = 0.2,
            max_tokens = 900,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = "Return only valid JSON. No markdown. No extra text." },
                new { role = "user", content = prompt }
            }
        }, options: JsonOptions);

        using var response = await httpClient.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenRouter error: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var jsonText = NormalizeJson(ExtractMessageContent(document.RootElement));
        return JsonSerializer.Deserialize<CreateCardRequest>(jsonText, JsonOptions)
            ?? throw new InvalidOperationException("پاسخ مدل خالی بود.");
    }

    private static string StandardPrompt(string text, CardType type) => $"""
        Create a flashcard for a Persian-speaking English learner.
        Return JSON with: front, back, example, prompt, answer, notes, type.
        type must be "{type}" unless another type is clearly better.
        front: exact English item.
        back: Persian meaning/explanation.
        example: one natural English sentence.
        prompt: one recall question.
        answer: ideal short answer.
        notes: Persian notes about usage, register, collocations and common mistakes.
        Learner input: {text}
        """;

    private static string FeedbackPrompt(string text) => $"""
        The learner writes a mistake or teacher feedback. Build a feedback flashcard.
        Return JSON with: front, back, example, prompt, answer, notes, type.
        type must be "Feedback".
        front: show the wrong form and corrected form in a compact way.
        back: Persian explanation of the grammar/vocabulary issue.
        example: a correct natural English example.
        prompt: ask the learner to correct or explain the mistake.
        answer: the corrected sentence or best answer.
        notes: Persian explanation, pattern, and one warning about common mistakes.
        Learner feedback/mistake: {text}
        """;

    private static string ExtractMessageContent(JsonElement root)
    {
        return root.TryGetProperty("choices", out var choices)
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            ? content.GetString() ?? "{}"
            : "{}";
    }

    private static string NormalizeJson(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text.Trim('`').Trim();
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                text = text[4..].Trim();
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}

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
                    return new UserIdentity($"tg_{tgUser.Id}", "telegram", tgUser.Name, true);
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

        return new TelegramUser(id, name);
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

public static class AppJsonOptions
{
    public static JsonSerializerOptions CreateIndented() => Create(true);
    public static JsonSerializerOptions CreateCompact() => Create(false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record UserIdentity(string StorageKey, string Source, string DisplayName, bool IsAuthorized)
{
    public static UserIdentity Unauthorized() => new("unauthorized", "telegram", string.Empty, false);
}

public sealed record TelegramUser(string Id, string Name);

public sealed record OpenRouterOptions(string DefaultModel, string Referer)
{
    public static OpenRouterOptions From(IConfiguration config) => new(
        config["OPENROUTER_MODEL"] ?? config["OpenRouter:DefaultModel"] ?? "google/gemma-4-31b-it:free",
        config["OPENROUTER_REFERER"] ?? config["OpenRouter:Referer"] ?? "https://lingualite.local");
}

public sealed class AppDatabase
{
    public Dictionary<string, UserProfile> Users { get; set; } = [];
    public Dictionary<string, DeckState> Decks { get; set; } = [];
    public Dictionary<string, AccessCode> AccessCodes { get; set; } = [];
}

public sealed class UserProfile
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string Plan { get; set; } = "Free";
    public FeatureSet Features { get; set; } = FeatureSet.AllEnabled();
    public string AccessCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class FeatureSet
{
    public bool Ai { get; set; } = true;
    public bool ExportImport { get; set; } = true;
    public bool FeedbackCards { get; set; } = true;
    public bool UnlimitedCards { get; set; } = true;

    public static FeatureSet AllEnabled() => new();
}

public sealed class AccessCode
{
    public string Code { get; set; } = string.Empty;
    public string Plan { get; set; } = "Free";
    public FeatureSet Features { get; set; } = FeatureSet.AllEnabled();
    public int MaxUses { get; set; } = 1;
    public int Uses { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DeckState
{
    public List<FlashCard> Cards { get; set; } = [];

    public static DeckState CreateSeed()
    {
        var now = DateTimeOffset.UtcNow;
        return new DeckState
        {
            Cards =
            [
                new()
                {
                    Id = Guid.NewGuid(),
                    Front = "resilient",
                    Back = "تاب‌آور، مقاوم",
                    Example = "She stayed resilient after every setback.",
                    Prompt = "How would you describe someone who recovers quickly?",
                    Answer = "Resilient.",
                    Notes = "صفت پرکاربرد برای آدم‌ها، سیستم‌ها و کسب‌وکارها.",
                    Type = CardType.Word,
                    Box = 1,
                    CreatedAt = now.AddDays(-2),
                    NextReviewAt = now.AddMinutes(-10)
                }
            ]
        };
    }
}

public sealed class FlashCard
{
    public Guid Id { get; set; }
    public string Front { get; set; } = string.Empty;
    public string Back { get; set; } = string.Empty;
    public string Example { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public CardType Type { get; set; } = CardType.Word;
    public int Box { get; set; } = 1;
    public int TotalReviews { get; set; }
    public int CorrectReviews { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextReviewAt { get; set; }
    public DateTimeOffset? LastReviewedAt { get; set; }
}

public enum CardType
{
    Word,
    Sentence,
    Question,
    Feedback
}

public enum ImportMode
{
    Merge,
    Replace
}

public sealed record CreateCardRequest(
    string Front,
    string Back,
    string? Example,
    string? Prompt,
    string? Answer,
    string? Notes,
    CardType Type = CardType.Word);

public sealed record AiCompleteRequest(string Text, CardType? Type);
public sealed record ReviewRequest(bool Remembered);
public sealed record ImportRequest(List<FlashCard> Cards, ImportMode Mode = ImportMode.Merge);
public sealed record ExportPayload(string UserId, DateTimeOffset ExportedAt, List<FlashCard> Cards);
public sealed record RedeemCodeRequest(string Code);
public sealed record AdminUpdateUserRequest(bool? IsActive, string? Plan, FeatureSet? Features);
public sealed record CreateAccessCodeRequest(string? Code, string? Plan, FeatureSet? Features, int? MaxUses);
public sealed record RedeemResult(bool Success, string Message, UserProfile? Profile)
{
    public static RedeemResult Ok(UserProfile profile) => new(true, string.Empty, profile);
    public static RedeemResult Fail(string message) => new(false, message, null);
}

public sealed record DeckSummary(
    int TotalCards,
    int DueCards,
    int LearnedCards,
    double Accuracy,
    IReadOnlyDictionary<int, int> Boxes)
{
    public static DeckSummary From(DeckState state)
    {
        var now = DateTimeOffset.UtcNow;
        var totalReviews = state.Cards.Sum(card => card.TotalReviews);
        var correctReviews = state.Cards.Sum(card => card.CorrectReviews);

        return new DeckSummary(
            state.Cards.Count,
            state.Cards.Count(card => card.NextReviewAt <= now),
            state.Cards.Count(card => card.Box >= 4),
            totalReviews == 0 ? 0 : Math.Round((double)correctReviews / totalReviews * 100, 1),
            Enumerable.Range(1, 5).ToDictionary(box => box, box => state.Cards.Count(card => card.Box == box)));
    }
}

public static class LeitnerSchedule
{
    public static TimeSpan DelayFor(int box) => box switch
    {
        <= 1 => TimeSpan.FromMinutes(10),
        2 => TimeSpan.FromHours(8),
        3 => TimeSpan.FromDays(1),
        4 => TimeSpan.FromDays(3),
        _ => TimeSpan.FromDays(7)
    };
}
