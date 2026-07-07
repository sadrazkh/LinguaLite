public interface IAppStore
{
    string ProviderName { get; }
    Task EnsureReadyAsync();
    Task<UserProfile> GetOrCreateUserAsync(UserIdentity identity);
    Task<DeckState> GetDeckAsync(string userId);
    Task AddCardAsync(string userId, FlashCard card);
    Task<FlashCard?> UpdateCardAsync(string userId, Guid cardId, Action<FlashCard> update);
    Task<bool> DeleteCardAsync(string userId, Guid cardId);
    Task<int> ImportCardsAsync(string userId, List<FlashCard> cards, ImportMode mode);
    Task<List<UserProfile>> GetUsersAsync();
    Task<UserProfile?> UpdateUserAsync(string id, Action<UserProfile> update);
    Task<AccessCode> CreateAccessCodeAsync(CreateAccessCodeRequest request);
    Task<List<AccessCode>> GetAccessCodesAsync();
    Task<AccessCode?> UpdateAccessCodeAsync(string code, UpdateAccessCodeRequest request);
    Task<RedeemResult> RedeemCodeAsync(string userId, string codeText);
    Task<List<PlanDefinition>> GetPlansAsync();
    Task<PlanDefinition> UpsertPlanAsync(PlanDefinition plan);
    Task<bool> DeletePlanAsync(string id);
    Task<PlanDefinition> GetEffectivePlanAsync(string planName);
    Task<List<LearningPackage>> GetPackagesAsync();
    Task<LearningPackage> UpsertPackageAsync(LearningPackage package);
    Task<bool> DeletePackageAsync(string id);
    Task<PackageImportResult> ImportPackageCardsAsync(string userId, string planName, string packageId, int count);
    Task<AiUsageSummary> GetAiUsageAsync(string userId, string planName, AiToolKind tool);
    Task<AiUsageSummary> TryConsumeAiRequestAsync(string userId, string planName, AiToolKind tool);
    Task<BrowserLoginCode> CreateBrowserLoginCodeAsync(string userId, TimeSpan ttl);
    Task<BrowserLoginResult> RedeemBrowserLoginCodeAsync(string codeText);
    Task<UserProfile?> GetUserBySessionTokenAsync(string sessionToken);
    Task RecordActivityAsync(string userId, ActivityKind kind, int count = 1);
    Task<List<AdminUserMetrics>> GetAdminUserMetricsAsync();
    Task<AppSettingsState> GetSettingsAsync();
    Task<AppSettingsState> UpdateSettingsAsync(Action<AppSettingsState> update);
    Task MarkReminderSentAsync(string userId, DateTimeOffset sentAt);
}
