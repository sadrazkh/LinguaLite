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

    public async Task<DeckSummary> GetDeckSummaryAsync(string userId)
    {
        var deck = await GetDeckAsync(userId);
        return DeckSummary.From(deck);
    }

    public async Task<CardPage> GetCardsPageAsync(string userId, bool archived, int limit, string? cursor, IReadOnlyCollection<int>? boxes = null)
    {
        var deck = await GetDeckAsync(userId);
        var query = deck.Cards.Where(card => card.IsArchived == archived);
        if (!archived && boxes is { Count: > 0 }) query = query.Where(card => boxes.Contains(card.Box));
        var ordered = query.OrderByDescending(card => card.CreatedAt).ThenByDescending(card => card.Id).ToList();
        if (CardCursor.TryDecode(cursor, out var createdAt, out var id))
        {
            ordered = ordered.Where(card => card.CreatedAt < createdAt || (card.CreatedAt == createdAt && card.Id.CompareTo(id) < 0)).ToList();
        }

        var pageSize = Math.Clamp(limit, 1, 100);
        var items = ordered.Take(pageSize + 1).ToList();
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        var nextCursor = hasMore && items.Count > 0 ? CardCursor.Encode(items[^1].CreatedAt, items[^1].Id) : null;
        return new CardPage(items, nextCursor, hasMore);
    }

    public async Task<List<FlashCard>> GetDueCardsAsync(string userId, int limit)
    {
        var deck = await GetDeckAsync(userId);
        var now = DateTimeOffset.UtcNow;
        return deck.Cards
            .Where(card => !card.IsArchived && LeitnerSchedule.IsDue(card, now))
            .OrderBy(card => card.NextReviewAt)
            .ThenBy(card => card.Box)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    public async Task<FlashCard?> GetCardAsync(string userId, Guid cardId)
    {
        var deck = await GetDeckAsync(userId);
        return deck.Cards.FirstOrDefault(card => card.Id == cardId);
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
                card.NextReviewAt = card.NextReviewAt == default ? LeitnerSchedule.TodayUtc() : card.NextReviewAt.ToUniversalTime();
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

    public async Task<AccessCode?> UpdateAccessCodeAsync(string codeText, UpdateAccessCodeRequest request)
    {
        return await MutateAsync(db =>
        {
            var normalized = codeText.Trim().ToUpperInvariant();
            if (!db.AccessCodes.TryGetValue(normalized, out var code)) return null;
            if (!string.IsNullOrWhiteSpace(request.Plan)) code.Plan = request.Plan.Trim();
            if (request.Features is not null) code.Features = request.Features;
            if (request.MaxUses.HasValue) code.MaxUses = Math.Max(code.Uses, request.MaxUses.Value);
            return code;
        });
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

    public async Task<List<LearningPackage>> GetPackagesAsync()
    {
        return await MutateAsync(db =>
        {
            EnsurePackages(db);
            return db.Packages.OrderBy(item => item.SortOrder).ThenBy(item => item.Title).ToList();
        });
    }

    public async Task<List<PackageProgress>> GetPackageProgressAsync(string userId)
    {
        var deck = await GetDeckAsync(userId);
        return deck.Cards
            .Where(card => !string.IsNullOrWhiteSpace(card.SourcePackageId))
            .GroupBy(card => card.SourcePackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PackageProgress(group.Key, group.Count()))
            .ToList();
    }

    public async Task<LearningPackage> UpsertPackageAsync(LearningPackage package)
    {
        return await MutateAsync(db =>
        {
            EnsurePackages(db);
            NormalizePackage(package);
            var index = db.Packages.FindIndex(item => item.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) db.Packages[index] = package;
            else db.Packages.Add(package);
            return package;
        });
    }

    public async Task<bool> DeletePackageAsync(string id)
    {
        return await MutateAsync(db =>
        {
            EnsurePackages(db);
            return db.Packages.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
        });
    }

    public async Task<PackageImportResult> ImportPackageCardsAsync(string userId, string planName, string packageId, int count)
    {
        return await MutateAsync(db =>
        {
            EnsurePackages(db);
            if (!db.Decks.TryGetValue(userId, out var deck))
            {
                deck = new DeckState();
                db.Decks[userId] = deck;
            }

            var package = db.Packages.FirstOrDefault(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase) && item.IsPublished);
            if (package is null) return new PackageImportResult(packageId, count, 0, 0, 0, "بسته پیدا نشد.");
            if (!HasPackageAccess(package, planName)) return new PackageImportResult(package.Id, count, 0, 0, count, "پلن شما به این بسته دسترسی ندارد.");
            return ImportPackageIntoDeck(deck, package, count);
        });
    }

    public async Task<AiUsageSummary> GetAiUsageAsync(string userId, string planName, AiToolKind tool)
    {
        var plan = await GetEffectivePlanAsync(planName);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var db = await LoadAsync();
        var toolKey = ToolKey(tool);
        var usage = db.AiUsage.Where(item => item.UserId == userId && item.Tool == toolKey).ToList();
        var summary = new AiUsageSummary
        {
            Tool = toolKey,
            Today = usage.Where(item => item.UsageDate == today).Sum(item => item.Count),
            ThisMonth = usage.Where(item => item.UsageDate >= monthStart).Sum(item => item.Count),
            DailyLimit = DailyLimitFor(plan, tool),
            MonthlyLimit = MonthlyLimitFor(plan, tool)
        };
        ApplyAiAllowance(summary);
        return summary;
    }

    public async Task<AiUsageSummary> TryConsumeAiRequestAsync(string userId, string planName, AiToolKind tool)
    {
        return await MutateAsync(db =>
        {
            EnsurePlans(db);
            var plan = EffectivePlan(db, planName);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var toolKey = ToolKey(tool);
            var userUsage = db.AiUsage.Where(item => item.UserId == userId && item.Tool == toolKey).ToList();
            var summary = new AiUsageSummary
            {
                Tool = toolKey,
                Today = userUsage.Where(item => item.UsageDate == today).Sum(item => item.Count),
                ThisMonth = userUsage.Where(item => item.UsageDate >= monthStart).Sum(item => item.Count),
                DailyLimit = DailyLimitFor(plan, tool),
                MonthlyLimit = MonthlyLimitFor(plan, tool)
            };
            ApplyAiAllowance(summary);
            if (!summary.Allowed) return summary;

            var item = db.AiUsage.FirstOrDefault(row => row.UserId == userId && row.Tool == toolKey && row.UsageDate == today);
            if (item is null)
            {
                item = new LocalAiUsage { UserId = userId, Tool = toolKey, UsageDate = today, Count = 0 };
                db.AiUsage.Add(item);
            }

            item.Count++;
            summary.Today++;
            summary.ThisMonth++;
            ApplyAiAllowance(summary);
            return summary;
        });
    }

    public async Task<BrowserLoginCode> CreateBrowserLoginCodeAsync(string userId, TimeSpan ttl)
    {
        return await MutateAsync(db =>
        {
            var code = new BrowserLoginCode
            {
                Code = GenerateNumericCode(),
                UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.Add(ttl)
            };
            db.BrowserLoginCodes.RemoveAll(item => item.UserId == userId || item.ExpiresAt <= DateTimeOffset.UtcNow || item.UsedAt.HasValue);
            db.BrowserLoginCodes.Add(code);
            return code;
        });
    }

    public async Task<BrowserLoginResult> RedeemBrowserLoginCodeAsync(string codeText)
    {
        return await MutateAsync(db =>
        {
            var normalized = NormalizeLoginCode(codeText);
            var code = db.BrowserLoginCodes.FirstOrDefault(item => item.Code == normalized && !item.UsedAt.HasValue);
            if (code is null) return BrowserLoginResult.Fail("کد ورود پیدا نشد.");
            if (code.ExpiresAt <= DateTimeOffset.UtcNow) return BrowserLoginResult.Fail("کد ورود منقضی شده است. از ربات یک کد جدید بگیر.");
            if (!db.Users.TryGetValue(code.UserId, out var user)) return BrowserLoginResult.Fail("اکانت تلگرام پیدا نشد.");

            code.UsedAt = DateTimeOffset.UtcNow;
            var token = GenerateSessionToken();
            db.BrowserSessions.Add(new LocalBrowserSession
            {
                TokenHash = HashToken(token),
                UserId = user.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            });
            return BrowserLoginResult.Ok(token, user);
        });
    }

    public async Task<UserProfile?> GetUserBySessionTokenAsync(string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        return await MutateAsync(db =>
        {
            var hash = HashToken(sessionToken);
            var session = db.BrowserSessions.FirstOrDefault(item => item.TokenHash == hash);
            if (session is null) return null;
            if (!db.Users.TryGetValue(session.UserId, out var user)) return null;

            session.LastSeenAt = DateTimeOffset.UtcNow;
            user.LastSeenAt = DateTimeOffset.UtcNow;
            return user;
        });
    }

    public async Task RecordActivityAsync(string userId, ActivityKind kind, int count = 1)
    {
        await MutateAsync(db =>
        {
            var activity = GetTodayActivity(db, userId);
            var now = DateTimeOffset.UtcNow;
            activity.LastSeenAt = now;
            activity.FirstSeenAt = activity.FirstSeenAt == default ? now : activity.FirstSeenAt;
            var safeCount = Math.Max(1, count);
            switch (kind)
            {
                case ActivityKind.CardAdded:
                    activity.CardsAdded += safeCount;
                    break;
                case ActivityKind.Review:
                    activity.Reviews += safeCount;
                    break;
                case ActivityKind.AiCard:
                    activity.AiCard += safeCount;
                    break;
                case ActivityKind.AiDictionary:
                    activity.AiDictionary += safeCount;
                    break;
                case ActivityKind.AiCorrection:
                    activity.AiCorrection += safeCount;
                    break;
                default:
                    activity.Requests += safeCount;
                    break;
            }
            return true;
        });
    }

    public async Task<List<AdminUserMetrics>> GetAdminUserMetricsAsync()
    {
        var db = await LoadAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return db.Users.Keys.Select(userId =>
        {
            db.Decks.TryGetValue(userId, out var deck);
            var activity = db.UserActivity.FirstOrDefault(item => item.UserId == userId && item.ActivityDate == today);
            var activeCards = deck?.Cards.Where(card => !card.IsArchived).ToList() ?? new List<FlashCard>();
            var dueCards = activeCards.Count(card => LeitnerSchedule.IsDue(card));
            return new AdminUserMetrics
            {
                UserId = userId,
                TotalCards = activeCards.Count,
                DueCards = dueCards,
                RequestsToday = activity?.Requests ?? 0,
                ActiveMinutesToday = ActiveMinutes(activity),
                CardsAddedToday = activity?.CardsAdded ?? 0,
                ReviewsToday = activity?.Reviews ?? 0,
                AiCardToday = activity?.AiCard ?? 0,
                AiDictionaryToday = activity?.AiDictionary ?? 0,
                AiCorrectionToday = activity?.AiCorrection ?? 0
            };
        }).ToList();
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

    public async Task<List<ReminderCandidate>> GetDueReminderCandidatesAsync(DateTimeOffset now, int defaultReminderHour)
    {
        var db = await LoadAsync();
        var result = new List<ReminderCandidate>();
        foreach (var user in db.Users.Values)
        {
            if (!user.IsActive || !user.RemindersEnabled || !user.TelegramChatId.HasValue) continue;
            if ((user.ReminderHour ?? defaultReminderHour) != now.Hour) continue;
            if (user.LastReminderAt?.UtcDateTime.Date == now.UtcDateTime.Date) continue;
            var due = db.Decks.TryGetValue(user.Id, out var deck)
                ? deck.Cards.Count(card => !card.IsArchived && LeitnerSchedule.IsDue(card, now))
                : 0;
            if (due > 0) result.Add(new ReminderCandidate(user, due));
        }
        return result;
    }

    public async Task<BroadcastJob> QueueBroadcastAsync(AdminBroadcastRequest request)
    {
        return await MutateAsync(db =>
        {
            var job = new BroadcastJob { Id = Guid.NewGuid(), Message = request.Message.Trim(), CreatedAt = DateTimeOffset.UtcNow };
            var users = db.Users.Values.Where(user => MatchesBroadcast(user, request)).ToList();
            job.Matched = users.Count;
            foreach (var user in users.Where(user => user.TelegramChatId.HasValue))
            {
                db.BroadcastRecipients.Add(new LocalBroadcastRecipient { JobId = job.Id, UserId = user.Id, ChatId = user.TelegramChatId!.Value });
            }
            job.Skipped = job.Matched - db.BroadcastRecipients.Count(item => item.JobId == job.Id);
            db.BroadcastJobs.Add(job);
            return job;
        });
    }

    public async Task<List<BroadcastJob>> GetBroadcastJobsAsync(int limit = 20)
    {
        var db = await LoadAsync();
        return db.BroadcastJobs.OrderByDescending(job => job.CreatedAt).Take(Math.Clamp(limit, 1, 100)).ToList();
    }

    public async Task<List<BroadcastDelivery>> ClaimBroadcastDeliveriesAsync(int batchSize)
    {
        return await MutateAsync(db =>
        {
            var now = DateTimeOffset.UtcNow;
            var jobs = db.BroadcastJobs.Where(job => job.Status is "queued" or "running").ToDictionary(job => job.Id);
            var recipients = db.BroadcastRecipients
                .Where(item => jobs.ContainsKey(item.JobId) && item.Status == "pending" && item.NextAttemptAt <= now)
                .OrderBy(item => jobs[item.JobId].CreatedAt)
                .Take(Math.Clamp(batchSize, 1, 100))
                .ToList();
            foreach (var item in recipients)
            {
                item.Status = "sending";
                item.Attempts++;
                item.NextAttemptAt = now.AddMinutes(5);
                var job = jobs[item.JobId];
                job.Status = "running";
                job.StartedAt ??= now;
            }
            return recipients.Select(item => new BroadcastDelivery(item.JobId, item.UserId, item.ChatId, jobs[item.JobId].Message, item.Attempts)).ToList();
        });
    }

    public async Task CompleteBroadcastDeliveryAsync(BroadcastDelivery delivery, bool sent, string? error)
    {
        await MutateAsync(db =>
        {
            var recipient = db.BroadcastRecipients.FirstOrDefault(item => item.JobId == delivery.JobId && item.UserId == delivery.UserId);
            var job = db.BroadcastJobs.FirstOrDefault(item => item.Id == delivery.JobId);
            if (recipient is null || job is null) return false;
            if (sent)
            {
                recipient.Status = "sent";
                job.Sent++;
            }
            else if (recipient.Attempts >= 4)
            {
                recipient.Status = "failed";
                recipient.LastError = error ?? string.Empty;
                job.Failed++;
            }
            else
            {
                recipient.Status = "pending";
                recipient.LastError = error ?? string.Empty;
                recipient.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 10 * recipient.Attempts * recipient.Attempts));
            }
            if (!db.BroadcastRecipients.Any(item => item.JobId == job.Id && (item.Status == "pending" || item.Status == "sending")))
            {
                job.Status = "completed";
                job.CompletedAt = DateTimeOffset.UtcNow;
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

    private static string GenerateNumericCode()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt32(bytes) % 1_000_000;
        return value.ToString("D6");
    }

    private static string GenerateSessionToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token.Trim());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string NormalizeLoginCode(string value)
    {
        return new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
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
                ? "سقف روزانه این ابزار در پلن شما تمام شده است."
                : "سقف ماهانه این ابزار در پلن شما تمام شده است.";
    }

    private static string ToolKey(AiToolKind tool) => tool switch
    {
        AiToolKind.Dictionary => "dictionary",
        AiToolKind.Correction => "correction",
        _ => "card"
    };

    private static int DailyLimitFor(PlanDefinition plan, AiToolKind tool) => tool switch
    {
        AiToolKind.Dictionary => plan.DictionaryDailyLimit,
        AiToolKind.Correction => plan.CorrectionDailyLimit,
        _ => plan.AiDailyLimit
    };

    private static int MonthlyLimitFor(PlanDefinition plan, AiToolKind tool) => tool switch
    {
        AiToolKind.Dictionary => plan.DictionaryMonthlyLimit,
        AiToolKind.Correction => plan.CorrectionMonthlyLimit,
        _ => plan.AiMonthlyLimit
    };

    private static LocalUserActivity GetTodayActivity(LocalAppDatabase db, string userId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var activity = db.UserActivity.FirstOrDefault(item => item.UserId == userId && item.ActivityDate == today);
        if (activity is not null) return activity;

        activity = new LocalUserActivity
        {
            UserId = userId,
            ActivityDate = today,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        db.UserActivity.Add(activity);
        return activity;
    }

    private static int ActiveMinutes(LocalUserActivity? activity)
    {
        if (activity is null) return 0;
        var minutes = (int)Math.Ceiling((activity.LastSeenAt - activity.FirstSeenAt).TotalMinutes);
        return activity.Requests > 0 ? Math.Max(1, minutes) : Math.Max(0, minutes);
    }

    private static void EnsurePackages(LocalAppDatabase db)
    {
        if (db.Packages.Count > 0) return;
        db.Packages.AddRange(LearningPackage.Defaults());
    }

    private static void NormalizePackage(LearningPackage package)
    {
        package.Id = string.IsNullOrWhiteSpace(package.Id) ? Slug(package.Title) : Slug(package.Id);
        package.Title = string.IsNullOrWhiteSpace(package.Title) ? package.Id : package.Title.Trim();
        package.Description = package.Description?.Trim() ?? string.Empty;
        package.RequiredPlans = package.RequiredPlans.Select(item => item.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        package.CreatedAt = package.CreatedAt == default ? DateTimeOffset.UtcNow : package.CreatedAt;
        package.UpdatedAt = DateTimeOffset.UtcNow;
        foreach (var card in package.Cards)
        {
            card.Id = string.IsNullOrWhiteSpace(card.Id) ? Slug(card.Front) : Slug(card.Id);
            card.Front = card.Front.Trim();
            card.Back = card.Back.Trim();
            card.Example = card.Example?.Trim() ?? string.Empty;
            card.Prompt = card.Prompt?.Trim() ?? string.Empty;
            card.Answer = card.Answer?.Trim() ?? string.Empty;
            card.Notes = card.Notes?.Trim() ?? string.Empty;
        }
    }

    private static PackageImportResult ImportPackageIntoDeck(DeckState deck, LearningPackage package, int requested)
    {
        var count = Math.Clamp(requested, 1, 100);
        var existingPackageCards = deck.Cards
            .Where(card => card.SourcePackageId.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
            .Select(card => card.SourcePackageCardId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingSignatures = deck.Cards.Select(CardSignature).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skippedDuplicate = 0;

        foreach (var item in package.Cards)
        {
            if (added >= count) break;
            if (existingPackageCards.Contains(item.Id))
            {
                skippedDuplicate++;
                continue;
            }

            var card = item.ToFlashCard(package.Id);
            if (existingSignatures.Contains(CardSignature(card)))
            {
                skippedDuplicate++;
                continue;
            }

            deck.Cards.Add(card);
            existingPackageCards.Add(item.Id);
            existingSignatures.Add(CardSignature(card));
            added++;
        }

        return new PackageImportResult(package.Id, count, added, skippedDuplicate, 0,
            added > 0 ? $"{added} کارت از بسته اضافه شد." : "کارت جدیدی برای اضافه کردن پیدا نشد.");
    }

    private static bool HasPackageAccess(LearningPackage package, string planName) =>
        package.RequiredPlans.Count == 0
        || package.RequiredPlans.Any(plan => plan.Equals(planName, StringComparison.OrdinalIgnoreCase) || NormalizePlanId(plan) == NormalizePlanId(planName));

    private static bool MatchesBroadcast(UserProfile user, AdminBroadcastRequest request)
    {
        if (request.Audience.Equals("selected", StringComparison.OrdinalIgnoreCase)) return request.UserIds?.Contains(user.Id) == true;
        if (request.Audience.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(request.Plan) && !user.Plan.Equals(request.Plan, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.IsActive.HasValue && user.IsActive != request.IsActive.Value) return false;
        if (!string.IsNullOrWhiteSpace(request.Source) && !user.Source.Equals(request.Source, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(request.AccessCode) && !user.AccessCode.Equals(request.AccessCode, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            var value = $"{user.Id} {user.DisplayName} {user.TelegramId} {user.TelegramUsername}";
            if (!value.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string CardSignature(FlashCard card) => $"{card.Type}:{NormalizeText(card.Front)}";
    private static string NormalizeText(string value) => new(value.Trim().ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch)).ToArray());
    private static string Slug(string value) => NormalizePlanId(value);

    private sealed class LocalAppDatabase
    {
        public Dictionary<string, UserProfile> Users { get; set; } = [];
        public Dictionary<string, DeckState> Decks { get; set; } = [];
        public Dictionary<string, AccessCode> AccessCodes { get; set; } = [];
        public List<PlanDefinition> Plans { get; set; } = [];
        public List<LearningPackage> Packages { get; set; } = [];
        public List<LocalAiUsage> AiUsage { get; set; } = [];
        public List<BrowserLoginCode> BrowserLoginCodes { get; set; } = [];
        public List<LocalBrowserSession> BrowserSessions { get; set; } = [];
        public List<LocalUserActivity> UserActivity { get; set; } = [];
        public List<BroadcastJob> BroadcastJobs { get; set; } = [];
        public List<LocalBroadcastRecipient> BroadcastRecipients { get; set; } = [];
        public AppSettingsState Settings { get; set; } = new();
    }

    private sealed class LocalAiUsage
    {
        public string UserId { get; set; } = string.Empty;
        public string Tool { get; set; } = "card";
        public DateOnly UsageDate { get; set; }
        public int Count { get; set; }
    }

    private sealed class LocalBrowserSession
    {
        public string TokenHash { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }

    private sealed class LocalUserActivity
    {
        public string UserId { get; set; } = string.Empty;
        public DateOnly ActivityDate { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public int Requests { get; set; }
        public int CardsAdded { get; set; }
        public int Reviews { get; set; }
        public int AiCard { get; set; }
        public int AiDictionary { get; set; }
        public int AiCorrection { get; set; }
    }

    private sealed class LocalBroadcastRecipient
    {
        public Guid JobId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public long ChatId { get; set; }
        public string Status { get; set; } = "pending";
        public int Attempts { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
        public string LastError { get; set; } = string.Empty;
    }
}
