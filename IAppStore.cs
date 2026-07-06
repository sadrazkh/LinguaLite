public interface IAppStore
{
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
    Task<RedeemResult> RedeemCodeAsync(string userId, string codeText);
}
