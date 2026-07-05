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
builder.Services.AddSingleton<DeckStore>();
builder.Services.AddHttpClient<OpenRouterCardService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/me", (HttpContext http, IConfiguration config) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    return user.IsAuthorized
        ? Results.Ok(new { userId = user.StorageKey, source = user.Source })
        : Results.Unauthorized();
});

api.MapGet("/deck", async (HttpContext http, IConfiguration config, DeckStore store) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    if (!user.IsAuthorized) return Results.Unauthorized();

    var state = await store.LoadAsync(user.StorageKey);
    return Results.Ok(DeckSummary.From(state));
});

api.MapGet("/cards/due", async (HttpContext http, IConfiguration config, DeckStore store) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    if (!user.IsAuthorized) return Results.Unauthorized();

    var state = await store.LoadAsync(user.StorageKey);
    var now = DateTimeOffset.UtcNow;
    var cards = state.Cards
        .Where(card => card.NextReviewAt <= now)
        .OrderBy(card => card.NextReviewAt)
        .ThenBy(card => card.Box)
        .Take(25)
        .ToList();

    return Results.Ok(cards);
});

api.MapGet("/cards", async (HttpContext http, IConfiguration config, DeckStore store) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    if (!user.IsAuthorized) return Results.Unauthorized();

    var state = await store.LoadAsync(user.StorageKey);
    return Results.Ok(state.Cards.OrderByDescending(card => card.CreatedAt));
});

api.MapPost("/cards", async (HttpContext http, IConfiguration config, CreateCardRequest request, DeckStore store) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    if (!user.IsAuthorized) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(request.Front) || string.IsNullOrWhiteSpace(request.Back))
    {
        return Results.BadRequest(new { message = "لغت و معنی را کامل وارد کنید." });
    }

    var state = await store.LoadAsync(user.StorageKey);
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

    state.Cards.Add(card);
    await store.SaveAsync(user.StorageKey, state);
    return Results.Created($"/api/cards/{card.Id}", card);
});

api.MapPost("/cards/{id:guid}/review", async (
    HttpContext http,
    IConfiguration config,
    Guid id,
    ReviewRequest request,
    DeckStore store) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    if (!user.IsAuthorized) return Results.Unauthorized();

    var state = await store.LoadAsync(user.StorageKey);
    var card = state.Cards.FirstOrDefault(item => item.Id == id);
    if (card is null)
    {
        return Results.NotFound(new { message = "کارت پیدا نشد." });
    }

    var now = DateTimeOffset.UtcNow;
    card.TotalReviews++;
    card.LastReviewedAt = now;

    if (request.Remembered)
    {
        card.CorrectReviews++;
        card.Box = Math.Min(5, card.Box + 1);
    }
    else
    {
        card.Box = 1;
    }

    card.NextReviewAt = now.Add(LeitnerSchedule.DelayFor(card.Box));
    await store.SaveAsync(user.StorageKey, state);
    return Results.Ok(card);
});

api.MapDelete("/cards/{id:guid}", async (HttpContext http, IConfiguration config, Guid id, DeckStore store) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    if (!user.IsAuthorized) return Results.Unauthorized();

    var state = await store.LoadAsync(user.StorageKey);
    var removed = state.Cards.RemoveAll(card => card.Id == id);
    if (removed == 0)
    {
        return Results.NotFound(new { message = "کارت پیدا نشد." });
    }

    await store.SaveAsync(user.StorageKey, state);
    return Results.NoContent();
});

api.MapPost("/ai/complete", async (AiCompleteRequest request, HttpContext http, IConfiguration config, OpenRouterCardService ai) =>
{
    var user = TelegramUserResolver.Resolve(http, config);
    if (!user.IsAuthorized) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(request.Word))
    {
        return Results.BadRequest(new { message = "کلمه یا عبارت را وارد کنید." });
    }

    var apiKey = http.Request.Headers["X-OpenRouter-Api-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        apiKey = config["OPENROUTER_API_KEY"];
    }

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return Results.BadRequest(new { message = "API key را در تنظیمات وارد کنید یا OPENROUTER_API_KEY را روی سرور بگذارید." });
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

app.Run();

public sealed class DeckStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private string RootDataPath
    {
        get
        {
            var dataDir = configuration["DATA_DIR"];
            return string.IsNullOrWhiteSpace(dataDir)
                ? Path.Combine(environment.ContentRootPath, "App_Data")
                : dataDir;
        }
    }

    public async Task<DeckState> LoadAsync(string userKey)
    {
        await _gate.WaitAsync();
        try
        {
            var path = DataPath(userKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                var seeded = DeckState.CreateSeed();
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(seeded, _jsonOptions));
                return seeded;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DeckState>(stream, _jsonOptions) ?? new DeckState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string userKey, DeckState state)
    {
        await _gate.WaitAsync();
        try
        {
            var path = DataPath(userKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(path, json);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string DataPath(string userKey)
    {
        var safeUserKey = string.Concat(userKey.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return Path.Combine(RootDataPath, "users", safeUserKey, "deck.json");
    }
}

public sealed class OpenRouterCardService(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<CreateCardRequest> CompleteAsync(AiCompleteRequest request, string apiKey)
    {
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? "google/gemma-4-31b-it:free"
            : request.Model.Trim();
        var prompt = $"""
        You generate flashcard data for Persian-speaking English learners.
        Return only valid JSON with these keys:
        front, back, example, prompt, answer, notes, type.

        Rules:
        - front: the exact English word or phrase.
        - back: Persian meaning, short but useful.
        - example: one natural English example sentence.
        - prompt: one English recall question that asks the learner to use or explain the item.
        - answer: a concise ideal answer.
        - notes: Persian usage notes, collocations, register, common mistakes.
        - type: Word, Sentence, or Question.

        Learner item: {request.Word}
        Target language: English
        Explanation language: Persian
        """;

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        message.Headers.Add("HTTP-Referer", "https://lingualite.local");
        message.Headers.Add("X-OpenRouter-Title", "LinguaLite");
        message.Content = JsonContent.Create(new
        {
            model,
            temperature = 0.2,
            max_tokens = 900,
            response_format = new
            {
                type = "json_object"
            },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a precise flashcard generator. Return only valid JSON. No markdown."
                },
                new
                {
                    role = "user",
                    content = prompt
                }
            }
        }, options: JsonOptions);

        using var response = await httpClient.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenRouter request failed: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var jsonText = NormalizeJson(ExtractMessageContent(document.RootElement));
        var card = JsonSerializer.Deserialize<CreateCardRequest>(jsonText, JsonOptions);

        return card ?? throw new InvalidOperationException("AI response was empty.");
    }

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
    public static UserContext Resolve(HttpContext http, IConfiguration config)
    {
        var initData = http.Request.Headers["X-Telegram-Init-Data"].FirstOrDefault();
        var botToken = config["TELEGRAM_BOT_TOKEN"];

        if (!string.IsNullOrWhiteSpace(initData))
        {
            if (string.IsNullOrWhiteSpace(botToken) || IsValidInitData(initData, botToken))
            {
                var userId = ExtractTelegramUserId(initData);
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    return new UserContext($"tg_{userId}", "telegram", true);
                }
            }

            if (!string.IsNullOrWhiteSpace(botToken))
            {
                return new UserContext("unauthorized", "telegram", false);
            }
        }

        if (!string.IsNullOrWhiteSpace(botToken))
        {
            return new UserContext("unauthorized", "telegram", false);
        }

        var devUserId = http.Request.Headers["X-Dev-User-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(devUserId))
        {
            return new UserContext($"dev_{devUserId}", "dev", true);
        }

        return new UserContext("dev_local", "local", true);
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

    private static string? ExtractTelegramUserId(string initData)
    {
        var pairs = ParseQuery(initData);
        if (!pairs.TryGetValue("user", out var userJson)) return null;

        using var document = JsonDocument.Parse(userJson);
        return document.RootElement.TryGetProperty("id", out var id) ? id.GetRawText().Trim('"') : null;
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

public sealed record UserContext(string StorageKey, string Source, bool IsAuthorized);

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
    Question
}

public sealed record CreateCardRequest(
    string Front,
    string Back,
    string? Example,
    string? Prompt,
    string? Answer,
    string? Notes,
    CardType Type = CardType.Word);

public sealed record AiCompleteRequest(string Word, string? Model);

public sealed record ReviewRequest(bool Remembered);

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
