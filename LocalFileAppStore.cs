using System.Security.Cryptography;
using System.Text.Json;

public sealed class LocalFileAppStore(IWebHostEnvironment environment, IConfiguration configuration) : IAppStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = AppJsonOptions.CreateIndented();

    public string ProviderName => "local-file";

    private string DatabasePath
    {
        get
        {
            var configured = configuration["LOCAL_DATA_PATH"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            return Path.Combine(environment.ContentRootPath, "App_Data", "local-database.json");
        }
    }

    public async Task EnsureReadyAsync()
    {
        await MutateAsync(db => db);
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
                    TelegramId = identity.TelegramId,
                    TelegramUsername = identity.TelegramUsername,
                    TelegramChatId = identity.TelegramChatId,
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
                if (!string.IsNullOrWhiteSpace(identity.TelegramId)) profile.TelegramId = identity.TelegramId;
                if (!string.IsNullOrWhiteSpace(identity.TelegramUsername)) profile.TelegramUsername = identity.TelegramUsername;
                if (identity.TelegramChatId.HasValue) profile.TelegramChatId = identity.TelegramChatId;
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

                card.CreatedAt = card.CreatedAt == default ? DateTimeOffset.UtcNow : card.CreatedAt.ToUniversalTime();
                card.NextReviewAt = card.NextReviewAt == default ? DateTimeOffset.UtcNow : card.NextReviewAt.ToUniversalTime();
                card.LastReviewedAt = card.LastReviewedAt?.ToUniversalTime();
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

    public async Task<List<PlanDefinition>> GetPlansAsync()
    {
        return await MutateAsync(db =>
        {
            EnsurePlans(db);
            return db.Plans.OrderBy(plan => plan.SortOrder).ThenBy(plan => plan.Name).ToList();
        });
    }

    public async Task<PlanDefinition> UpsertPlanAsync(PlanDefinition plan)
    {
        return await MutateAsync(db =>
        {
            EnsurePlans(db);
            plan.Id = NormalizePlanId(plan.Id);
            plan.Name = string.IsNullOrWhiteSpace(plan.Name) ? plan.Id : plan.Name.Trim();
            plan.CreatedAt = plan.CreatedAt == default ? DateTimeOffset.UtcNow : plan.CreatedAt;
            if (plan.IsDefault)
            {
                foreach (var item in db.Plans)
                {
                    item.IsDefault = false;
                }
            }

            var index = db.Plans.FindIndex(item => item.Id == plan.Id);
            if (index >= 0) db.Plans[index] = plan;
            else db.Plans.Add(plan);
            return plan;
        });
    }

    public async Task<bool> DeletePlanAsync(string id)
    {
        return await MutateAsync(db =>
        {
            EnsurePlans(db);
            return db.Plans.RemoveAll(plan => plan.Id == NormalizePlanId(id) && !plan.IsDefault) > 0;
        });
    }

    public async Task<PlanDefinition> GetEffectivePlanAsync(string planName)
    {
        var plans = await GetPlansAsync();
        var normalized = NormalizePlanId(planName);
        return plans.FirstOrDefault(plan => plan.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || plan.Name.Equals(planName, StringComparison.OrdinalIgnoreCase))
            ?? plans.FirstOrDefault(plan => plan.IsDefault)
            ?? PlanDefinition.Defaults()[0];
    }

    public async Task<AiUsageSummary> GetAiUsageAsync(string userId, string planName)
    {
        var plan = await GetEffectivePlanAsync(planName);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var db = await LoadAsync();
        var usage = db.AiUsage.Where(item => item.UserId == userId).ToList();
        var summary = new AiUsageSummary
        {
            Today = usage.Where(item => item.UsageDate == today).Sum(item => item.Count),
            ThisMonth = usage.Where(item => item.UsageDate >= monthStart).Sum(item => item.Count),
            DailyLimit = plan.AiDailyLimit,
            MonthlyLimit = plan.AiMonthlyLimit
        };
        ApplyAiAllowance(summary);
        return summary;
    }

    public async Task<AiUsageSummary> TryConsumeAiRequestAsync(string userId, string planName)
    {
        return await MutateAsync(db =>
        {
            EnsurePlans(db);
            var plan = EffectivePlan(db, planName);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var userUsage = db.AiUsage.Where(item => item.UserId == userId).ToList();
            var summary = new AiUsageSummary
            {
                Today = userUsage.Where(item => item.UsageDate == today).Sum(item => item.Count),
                ThisMonth = userUsage.Where(item => item.UsageDate >= monthStart).Sum(item => item.Count),
                DailyLimit = plan.AiDailyLimit,
                MonthlyLimit = plan.AiMonthlyLimit
            };
            ApplyAiAllowance(summary);
            if (!summary.Allowed) return summary;

            var item = db.AiUsage.FirstOrDefault(row => row.UserId == userId && row.UsageDate == today);
            if (item is null)
            {
                item = new LocalAiUsage { UserId = userId, UsageDate = today, Count = 0 };
                db.AiUsage.Add(item);
            }

            item.Count++;
            summary.Today++;
            summary.ThisMonth++;
            ApplyAiAllowance(summary);
            return summary;
        });
    }

    public async Task<AppSettingsState> GetSettingsAsync()
    {
        var db = await LoadAsync();
        return db.Settings;
    }

    public async Task<AppSettingsState> UpdateSettingsAsync(Action<AppSettingsState> update)
    {
        return await MutateAsync(db =>
        {
            update(db.Settings);
            return db.Settings;
        });
    }

    public async Task MarkReminderSentAsync(string userId, DateTimeOffset sentAt)
    {
        await MutateAsync(db =>
        {
            if (db.Users.TryGetValue(userId, out var user))
            {
                user.LastReminderAt = sentAt;
            }
            return true;
        });
    }

    private async Task<LocalAppDatabase> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            if (!File.Exists(DatabasePath))
            {
                var created = new LocalAppDatabase();
                await SaveUnlockedAsync(created);
                return created;
            }

            await using var stream = File.OpenRead(DatabasePath);
            return await JsonSerializer.DeserializeAsync<LocalAppDatabase>(stream, _jsonOptions) ?? new LocalAppDatabase();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> MutateAsync<T>(Func<LocalAppDatabase, T> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            LocalAppDatabase db;
            if (File.Exists(DatabasePath))
            {
                await using var read = File.OpenRead(DatabasePath);
                db = await JsonSerializer.DeserializeAsync<LocalAppDatabase>(read, _jsonOptions) ?? new LocalAppDatabase();
            }
            else
            {
                db = new LocalAppDatabase();
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

    private async Task SaveUnlockedAsync(LocalAppDatabase db)
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

    private static void EnsurePlans(LocalAppDatabase db)
    {
        if (db.Plans.Count > 0) return;
        db.Plans.AddRange(PlanDefinition.Defaults());
    }

    private static PlanDefinition EffectivePlan(LocalAppDatabase db, string planName)
    {
        EnsurePlans(db);
        var normalized = NormalizePlanId(planName);
        return db.Plans.FirstOrDefault(plan => plan.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || plan.Name.Equals(planName, StringComparison.OrdinalIgnoreCase))
            ?? db.Plans.FirstOrDefault(plan => plan.IsDefault)
            ?? PlanDefinition.Defaults()[0];
    }

    private static string NormalizePlanId(string value)
    {
        var normalized = new string((value ?? string.Empty).Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "free" : normalized;
    }

    private static void ApplyAiAllowance(AiUsageSummary summary)
    {
        var dailyExceeded = summary.DailyLimit > -1 && summary.Today >= summary.DailyLimit;
        var monthlyExceeded = summary.MonthlyLimit > -1 && summary.ThisMonth >= summary.MonthlyLimit;
        summary.Allowed = !dailyExceeded && !monthlyExceeded;
        summary.Message = summary.Allowed
            ? string.Empty
            : dailyExceeded
                ? "سقف روزانه درخواست‌های AI این پلن تمام شده است."
                : "سقف ماهانه درخواست‌های AI این پلن تمام شده است.";
    }

    private sealed class LocalAppDatabase
    {
        public Dictionary<string, UserProfile> Users { get; set; } = [];
        public Dictionary<string, DeckState> Decks { get; set; } = [];
        public Dictionary<string, AccessCode> AccessCodes { get; set; } = [];
        public List<PlanDefinition> Plans { get; set; } = [];
        public List<LocalAiUsage> AiUsage { get; set; } = [];
        public AppSettingsState Settings { get; set; } = new();
    }

    private sealed class LocalAiUsage
    {
        public string UserId { get; set; } = string.Empty;
        public DateOnly UsageDate { get; set; }
        public int Count { get; set; }
    }
}
