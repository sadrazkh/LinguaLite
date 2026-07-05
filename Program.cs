using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.AddSingleton<DeckStore>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/deck", async (DeckStore store) =>
{
    var state = await store.LoadAsync();
    return Results.Ok(DeckSummary.From(state));
});

api.MapGet("/cards/due", async (DeckStore store) =>
{
    var state = await store.LoadAsync();
    var now = DateTimeOffset.UtcNow;
    var cards = state.Cards
        .Where(card => card.NextReviewAt <= now)
        .OrderBy(card => card.NextReviewAt)
        .ThenBy(card => card.Box)
        .Take(25)
        .ToList();

    return Results.Ok(cards);
});

api.MapGet("/cards", async (DeckStore store) =>
{
    var state = await store.LoadAsync();
    return Results.Ok(state.Cards.OrderByDescending(card => card.CreatedAt));
});

api.MapPost("/cards", async (CreateCardRequest request, DeckStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Front) || string.IsNullOrWhiteSpace(request.Back))
    {
        return Results.BadRequest(new { message = "لغت و معنی را کامل وارد کنید." });
    }

    var state = await store.LoadAsync();
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
    await store.SaveAsync(state);
    return Results.Created($"/api/cards/{card.Id}", card);
});

api.MapPost("/cards/{id:guid}/review", async (Guid id, ReviewRequest request, DeckStore store) =>
{
    var state = await store.LoadAsync();
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
    await store.SaveAsync(state);
    return Results.Ok(card);
});

api.MapDelete("/cards/{id:guid}", async (Guid id, DeckStore store) =>
{
    var state = await store.LoadAsync();
    var removed = state.Cards.RemoveAll(card => card.Id == id);
    if (removed == 0)
    {
        return Results.NotFound(new { message = "کارت پیدا نشد." });
    }

    await store.SaveAsync(state);
    return Results.NoContent();
});

app.Run();

public sealed class DeckStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private string DataPath
    {
        get
        {
            var dataDir = configuration["DATA_DIR"];
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                dataDir = Path.Combine(environment.ContentRootPath, "App_Data");
            }

            return Path.Combine(dataDir, "deck.json");
        }
    }

    public async Task<DeckState> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
            if (!File.Exists(DataPath))
            {
                var seeded = DeckState.CreateSeed();
                await File.WriteAllTextAsync(DataPath, JsonSerializer.Serialize(seeded, _jsonOptions));
                return seeded;
            }

            await using var stream = File.OpenRead(DataPath);
            return await JsonSerializer.DeserializeAsync<DeckState>(stream, _jsonOptions) ?? new DeckState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DeckState state)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            await File.WriteAllTextAsync(DataPath, json);
        }
        finally
        {
            _gate.Release();
        }
    }
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
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    Front = "I am looking forward to it.",
                    Back = "مشتاقانه منتظرش هستم.",
                    Example = "Thanks for the invitation. I am looking forward to it.",
                    Prompt = "What do you say when you are excited about a future event?",
                    Answer = "I am looking forward to it.",
                    Type = CardType.Sentence,
                    Box = 2,
                    CreatedAt = now.AddDays(-4),
                    NextReviewAt = now.AddMinutes(-5)
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    Front = "What does 'concise' mean?",
                    Back = "کوتاه، دقیق و بدون اضافه‌گویی",
                    Example = "Keep your answer concise.",
                    Prompt = "Answer in Persian.",
                    Answer = "کوتاه و مفید.",
                    Type = CardType.Question,
                    Box = 1,
                    CreatedAt = now.AddDays(-1),
                    NextReviewAt = now.AddMinutes(-1)
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
