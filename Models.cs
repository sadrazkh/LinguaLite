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

public sealed record UserIdentity(string StorageKey, string Source, string DisplayName, bool IsAuthorized)
{
    public static UserIdentity Unauthorized() => new("unauthorized", "telegram", string.Empty, false);
}

public sealed record TelegramUser(string Id, string Name);
public sealed record CreateCardRequest(string Front, string Back, string? Example, string? Prompt, string? Answer, string? Notes, CardType Type = CardType.Word);
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

public sealed record DeckSummary(int TotalCards, int DueCards, int LearnedCards, double Accuracy, IReadOnlyDictionary<int, int> Boxes)
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
