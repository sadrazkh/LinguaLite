using System.Text.Json;

public sealed class UserProfile
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TelegramId { get; set; } = string.Empty;
    public string TelegramUsername { get; set; } = string.Empty;
    public long? TelegramChatId { get; set; }
    public bool IsActive { get; set; } = true;
    public string Plan { get; set; } = "Free";
    public FeatureSet Features { get; set; } = FeatureSet.AllEnabled();
    public string AccessCode { get; set; } = string.Empty;
    public bool RemindersEnabled { get; set; } = true;
    public int? ReminderHour { get; set; }
    public DateTimeOffset? LastReminderAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class FeatureSet
{
    public bool Ai { get; set; } = true;
    public bool Dictionary { get; set; } = true;
    public bool TextCorrection { get; set; } = true;
    public bool ExportImport { get; set; } = true;
    public bool FeedbackCards { get; set; } = true;
    public bool UnlimitedCards { get; set; } = true;

    public static FeatureSet AllEnabled() => new();
}

public sealed class PlanDefinition
{
    public string Id { get; set; } = "free";
    public string Name { get; set; } = "Free";
    public string BadgeColor { get; set; } = "#16a34a";
    public string BadgeTextColor { get; set; } = "#ffffff";
    public FeatureSet Features { get; set; } = FeatureSet.AllEnabled();
    public int AiDailyLimit { get; set; } = 20;
    public int AiMonthlyLimit { get; set; } = 300;
    public int DictionaryDailyLimit { get; set; } = 30;
    public int DictionaryMonthlyLimit { get; set; } = 600;
    public int CorrectionDailyLimit { get; set; } = 15;
    public int CorrectionMonthlyLimit { get; set; } = 300;
    public int CardLimit { get; set; } = 200;
    public int SortOrder { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static List<PlanDefinition> Defaults() =>
    [
        new()
        {
            Id = "free",
            Name = "Free",
            BadgeColor = "#16a34a",
            BadgeTextColor = "#ffffff",
            Features = FeatureSet.AllEnabled(),
            AiDailyLimit = 20,
            AiMonthlyLimit = 300,
            DictionaryDailyLimit = 30,
            DictionaryMonthlyLimit = 600,
            CorrectionDailyLimit = 15,
            CorrectionMonthlyLimit = 300,
            CardLimit = 200,
            SortOrder = 0,
            IsDefault = true,
            CreatedAt = DateTimeOffset.UtcNow
        },
        new()
        {
            Id = "pro",
            Name = "Pro",
            BadgeColor = "#2563eb",
            BadgeTextColor = "#ffffff",
            Features = FeatureSet.AllEnabled(),
            AiDailyLimit = -1,
            AiMonthlyLimit = -1,
            DictionaryDailyLimit = -1,
            DictionaryMonthlyLimit = -1,
            CorrectionDailyLimit = -1,
            CorrectionMonthlyLimit = -1,
            CardLimit = -1,
            SortOrder = 1,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow
        }
    ];
}

public sealed class AiUsageSummary
{
    public string Tool { get; set; } = "card";
    public int Today { get; set; }
    public int ThisMonth { get; set; }
    public int DailyLimit { get; set; }
    public int MonthlyLimit { get; set; }
    public bool Allowed { get; set; } = true;
    public string Message { get; set; } = string.Empty;
}

public sealed class BrowserLoginCode
{
    public string Code { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class AdminUserMetrics
{
    public string UserId { get; set; } = string.Empty;
    public int TotalCards { get; set; }
    public int DueCards { get; set; }
    public int RequestsToday { get; set; }
    public int ActiveMinutesToday { get; set; }
    public int CardsAddedToday { get; set; }
    public int ReviewsToday { get; set; }
    public int AiCardToday { get; set; }
    public int AiDictionaryToday { get; set; }
    public int AiCorrectionToday { get; set; }
}

public enum AiToolKind
{
    Card,
    Dictionary,
    Correction
}

public enum ActivityKind
{
    Seen,
    CardAdded,
    Review,
    AiCard,
    AiDictionary,
    AiCorrection
}

public sealed class AppSettingsState
{
    public string OpenRouterModel { get; set; } = string.Empty;
    public string OpenRouterReferer { get; set; } = string.Empty;
    public string PublicBaseUrl { get; set; } = string.Empty;
    public string TelegramBotUsername { get; set; } = string.Empty;
    public string TelegramMiniAppUrl { get; set; } = string.Empty;
    public bool BotEnabled { get; set; } = true;
    public bool RemindersEnabled { get; set; } = true;
    public int ReminderHour { get; set; } = 9;
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

public sealed class AccessCodeUsage
{
    public string Code { get; set; } = string.Empty;
    public int UsersCount { get; set; }
    public List<AccessCodeUser> Users { get; set; } = [];
}

public sealed class AccessCodeUser
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TelegramId { get; set; } = string.Empty;
    public string TelegramUsername { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public DateTimeOffset LastSeenAt { get; set; }
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

public sealed record UserIdentity(
    string StorageKey,
    string Source,
    string DisplayName,
    bool IsAuthorized,
    string TelegramId = "",
    string TelegramUsername = "",
    long? TelegramChatId = null)
{
    public static UserIdentity Unauthorized() => new("unauthorized", "telegram", string.Empty, false);
}

public sealed record TelegramUser(string Id, string Name, string Username);
public sealed record CreateCardRequest(string Front, string Back, string? Example, string? Prompt, string? Answer, string? Notes, CardType Type = CardType.Word);
public sealed record AiCompleteRequest(string Text, CardType? Type);
public sealed record DictionaryRequest(string Text);
public sealed record CorrectionRequest(string Text);
public sealed record DictionaryResult(string Word, string Pronunciation, string PartOfSpeech, string PersianMeaning, string EnglishDefinition, string[] Synonyms, string[] Examples, string Notes);
public sealed record CorrectionIssue(string Original, string Corrected, string Reason, string Severity);
public sealed record CorrectionResult(string Original, string Corrected, string PersianTranslation, string OverallNote, CorrectionIssue[] Issues, string[] BetterAlternatives);
public sealed record ReviewRequest(bool Remembered);
public sealed record ImportRequest(List<FlashCard> Cards, ImportMode Mode = ImportMode.Merge);
public sealed record ExportPayload(string UserId, DateTimeOffset ExportedAt, List<FlashCard> Cards);
public sealed record RedeemCodeRequest(string Code);
public sealed record BrowserLoginRequest(string Code);
public sealed record BrowserLoginResult(bool Success, string Message, string SessionToken, UserProfile? Profile)
{
    public static BrowserLoginResult Ok(string sessionToken, UserProfile profile) => new(true, string.Empty, sessionToken, profile);
    public static BrowserLoginResult Fail(string message) => new(false, message, string.Empty, null);
}
public sealed record AdminUpdateUserRequest(bool? IsActive, string? Plan, FeatureSet? Features, bool? RemindersEnabled, int? ReminderHour);
public sealed record CreateAccessCodeRequest(string? Code, string? Plan, FeatureSet? Features, int? MaxUses);
public sealed record UpdateAccessCodeRequest(string? Plan, FeatureSet? Features, int? MaxUses);
public sealed record UpdateCardRequest(string Front, string Back, string? Example, string? Prompt, string? Answer, string? Notes, CardType Type = CardType.Word);
public sealed record UpsertPlanRequest(string Id, string Name, string? BadgeColor, string? BadgeTextColor, FeatureSet Features, int AiDailyLimit, int AiMonthlyLimit, int DictionaryDailyLimit, int DictionaryMonthlyLimit, int CorrectionDailyLimit, int CorrectionMonthlyLimit, int CardLimit, int SortOrder, bool IsDefault);
public sealed record UpdateSettingsRequest(string? OpenRouterModel, string? OpenRouterReferer, string? PublicBaseUrl, string? TelegramBotUsername, string? TelegramMiniAppUrl, bool? BotEnabled, bool? RemindersEnabled, int? ReminderHour);
public sealed record TelegramWebhookRequest(JsonElement Update);
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
