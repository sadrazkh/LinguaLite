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
    public string LanguageLevel { get; set; } = "B1";
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
        var today = LeitnerSchedule.TodayUtc(now);
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
                    NextReviewAt = today
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
    public bool IsArchived { get; set; }
    public string SourcePackageId { get; set; } = string.Empty;
    public string SourcePackageCardId { get; set; } = string.Empty;
}

public sealed class LearningPackage
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> RequiredPlans { get; set; } = [];
    public bool IsPublished { get; set; } = true;
    public int SortOrder { get; set; }
    public List<PackageCard> Cards { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static List<LearningPackage> Defaults() =>
    [
        new()
        {
            Id = "essential-50",
            Title = "۵۰ واژه کاربردی انگلیسی",
            Description = "یک بسته شروع سریع از واژه‌های پرتکرار برای مکالمه، مطالعه و مرور روزانه.",
            RequiredPlans = [],
            IsPublished = true,
            SortOrder = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Cards = PackageCard.Essential50()
        }
    ];
}

public sealed class PackageCard
{
    public string Id { get; set; } = string.Empty;
    public string Front { get; set; } = string.Empty;
    public string Back { get; set; } = string.Empty;
    public string Example { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public CardType Type { get; set; } = CardType.Word;

    public FlashCard ToFlashCard(string packageId) => new()
    {
        Id = Guid.NewGuid(),
        Front = Front,
        Back = Back,
        Example = Example,
        Prompt = Prompt,
        Answer = Answer,
        Notes = Notes,
        Type = Type,
        Box = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        NextReviewAt = LeitnerSchedule.TodayUtc(),
        SourcePackageId = packageId,
        SourcePackageCardId = Id
    };

    public static List<PackageCard> Essential50()
    {
        string note = "واژه پرتکرار؛ با مثال یاد بگیر و در جمله خودت استفاده کن.";
        return
        [
            Word("ability", "توانایی", "She has the ability to solve problems quickly.", note),
            Word("achieve", "به دست آوردن، موفق شدن", "You can achieve your goal with daily practice.", note),
            Word("advice", "نصیحت، توصیه", "I need your advice about this job.", note),
            Word("agree", "موافق بودن", "I agree with your idea.", note),
            Word("allow", "اجازه دادن", "This app allows you to review cards every day.", note),
            Word("almost", "تقریبا", "I almost finished the lesson.", note),
            Word("although", "اگرچه", "Although it was hard, I kept trying.", note),
            Word("appear", "ظاهر شدن، به نظر رسیدن", "He appears calm before the exam.", note),
            Word("available", "در دسترس", "The package is available for your plan.", note),
            Word("avoid", "اجتناب کردن", "Avoid repeating the same mistake.", note),
            Word("benefit", "فایده، سود", "One benefit of reading is better vocabulary.", note),
            Word("challenge", "چالش", "Speaking is a useful challenge for learners.", note),
            Word("common", "رایج، مشترک", "This is a common word in daily English.", note),
            Word("compare", "مقایسه کردن", "Compare these two sentences.", note),
            Word("complete", "کامل کردن", "Complete the card before saving it.", note),
            Word("consider", "در نظر گرفتن", "Consider the context before you answer.", note),
            Word("continue", "ادامه دادن", "Continue practicing for ten minutes.", note),
            Word("create", "ساختن، ایجاد کردن", "Create a new sentence with this word.", note),
            Word("decide", "تصمیم گرفتن", "I decided to study every morning.", note),
            Word("describe", "توصیف کردن", "Describe your daily routine in English.", note),
            Word("develop", "توسعه دادن، پیشرفت کردن", "You develop fluency by speaking often.", note),
            Word("difference", "تفاوت", "What is the difference between these words?", note),
            Word("difficult", "سخت، دشوار", "This grammar point is difficult at first.", note),
            Word("effective", "موثر", "Short daily review is very effective.", note),
            Word("effort", "تلاش", "Your effort will improve your English.", note),
            Word("especially", "به‌خصوص", "I like podcasts, especially short ones.", note),
            Word("explain", "توضیح دادن", "Can you explain this sentence?", note),
            Word("focus", "تمرکز کردن", "Focus on one skill at a time.", note),
            Word("improve", "بهبود دادن، بهتر شدن", "I want to improve my pronunciation.", note),
            Word("include", "شامل بودن", "The lesson includes ten new words.", note),
            Word("increase", "افزایش دادن", "Reading can increase your vocabulary.", note),
            Word("instead", "به جای آن", "Use this phrase instead of the old one.", note),
            Word("knowledge", "دانش", "Vocabulary knowledge helps you read faster.", note),
            Word("method", "روش", "The Leitner method helps memory.", note),
            Word("necessary", "ضروری، لازم", "Practice is necessary for progress.", note),
            Word("notice", "متوجه شدن", "Notice the article before the noun.", note),
            Word("opportunity", "فرصت", "Every conversation is an opportunity to learn.", note),
            Word("particular", "خاص، مشخص", "Pay attention to this particular phrase.", note),
            Word("possible", "ممکن", "It is possible to learn step by step.", note),
            Word("practice", "تمرین کردن، تمرین", "Practice the sentence out loud.", note),
            Word("prefer", "ترجیح دادن", "I prefer simple examples.", note),
            Word("prepare", "آماده کردن", "Prepare five cards for tomorrow.", note),
            Word("provide", "فراهم کردن", "The app provides examples and notes.", note),
            Word("purpose", "هدف", "The purpose of review is long-term memory.", note),
            Word("realize", "فهمیدن، متوجه شدن", "I realized my mistake after feedback.", note),
            Word("receive", "دریافت کردن", "You receive a reminder from the bot.", note),
            Word("remember", "به خاطر سپردن", "Try to remember the answer first.", note),
            Word("require", "نیاز داشتن، لازم داشتن", "This package requires a Pro plan.", note),
            Word("suggest", "پیشنهاد دادن", "The teacher suggested a better sentence.", note),
            Word("useful", "مفید", "This example is useful for speaking.", note)
        ];
    }

    private static PackageCard Word(string front, string back, string example, string notes) => new()
    {
        Id = front.ToLowerInvariant().Replace(" ", "-"),
        Front = front,
        Back = back,
        Example = example,
        Prompt = $"What does \"{front}\" mean?",
        Answer = back,
        Notes = notes,
        Type = CardType.Word
    };
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
public sealed record AiCompleteRequest(string Text, CardType? Type, string? LanguageLevel = null);
public sealed record DictionaryRequest(string Text, string? LanguageLevel = null);
public sealed record CorrectionRequest(string Text, string? LanguageLevel = null);
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
public sealed record UpdateUserPreferencesRequest(string? LanguageLevel);
public sealed record PackageCardRequest(string Id, string Front, string Back, string? Example, string? Prompt, string? Answer, string? Notes, CardType Type = CardType.Word);
public sealed record UpsertPackageRequest(string Id, string Title, string Description, List<string>? RequiredPlans, bool IsPublished, int SortOrder, List<PackageCardRequest> Cards);
public sealed record PackageImportRequest(int Count);
public sealed record PackageImportResult(string PackageId, int Requested, int Added, int SkippedDuplicate, int SkippedAccess, string Message);
public sealed record ArchiveCardRequest(bool Archived);
public sealed record AdminBroadcastRequest(
    string Message,
    string Audience = "filtered",
    List<string>? UserIds = null,
    string? Plan = null,
    bool? IsActive = null,
    string? Source = null,
    string? AccessCode = null,
    string? Search = null);
public sealed record AdminBroadcastResult(int Matched, int Sent, int Skipped, int Failed, List<string> Errors);
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
        var activeCards = state.Cards.Where(card => !card.IsArchived).ToList();
        var totalReviews = activeCards.Sum(card => card.TotalReviews);
        var correctReviews = activeCards.Sum(card => card.CorrectReviews);

        return new DeckSummary(
            activeCards.Count,
            activeCards.Count(card => LeitnerSchedule.IsDue(card, now)),
            activeCards.Count(card => card.Box >= 4),
            totalReviews == 0 ? 0 : Math.Round((double)correctReviews / totalReviews * 100, 1),
            Enumerable.Range(1, 5).ToDictionary(box => box, box => activeCards.Count(card => card.Box == box)));
    }
}

public static class LeitnerSchedule
{
    public static int DelayDaysFor(int box) => box switch
    {
        <= 1 => 1,
        2 => 1,
        3 => 3,
        4 => 7,
        _ => 30
    };

    public static DateTimeOffset TodayUtc(DateTimeOffset? now = null)
    {
        var utcDate = (now ?? DateTimeOffset.UtcNow).UtcDateTime.Date;
        return new DateTimeOffset(utcDate, TimeSpan.Zero);
    }

    public static DateTimeOffset NextReviewAtFor(int box, DateTimeOffset? now = null) =>
        TodayUtc(now).AddDays(DelayDaysFor(box));

    public static bool IsDue(FlashCard card, DateTimeOffset? now = null) =>
        card.NextReviewAt.UtcDateTime.Date <= TodayUtc(now).UtcDateTime.Date;
}
