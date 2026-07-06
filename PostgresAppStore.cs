using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

public sealed class PostgresAppStore(IConfiguration configuration) : IAppStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = AppJsonOptions.CreateCompact();
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private NpgsqlDataSource? _dataSource;
    private bool _schemaReady;

    private NpgsqlDataSource DataSource => _dataSource ??= NpgsqlDataSource.Create(GetConnectionString(configuration));

    public async Task EnsureReadyAsync()
    {
        if (_schemaReady) return;

        await _schemaGate.WaitAsync();
        try
        {
            if (_schemaReady) return;

            await using var connection = await DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS app_users (
                    id text PRIMARY KEY,
                    source text NOT NULL,
                    display_name text NOT NULL,
                    is_active boolean NOT NULL DEFAULT true,
                    plan text NOT NULL DEFAULT 'Free',
                    features jsonb NOT NULL DEFAULT '{}'::jsonb,
                    access_code text NOT NULL DEFAULT '',
                    created_at timestamptz NOT NULL,
                    last_seen_at timestamptz NOT NULL
                );

                CREATE TABLE IF NOT EXISTS app_cards (
                    id uuid PRIMARY KEY,
                    user_id text NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
                    front text NOT NULL,
                    back text NOT NULL,
                    example text NOT NULL DEFAULT '',
                    prompt text NOT NULL DEFAULT '',
                    answer text NOT NULL DEFAULT '',
                    notes text NOT NULL DEFAULT '',
                    type text NOT NULL DEFAULT 'Word',
                    box integer NOT NULL DEFAULT 1,
                    total_reviews integer NOT NULL DEFAULT 0,
                    correct_reviews integer NOT NULL DEFAULT 0,
                    created_at timestamptz NOT NULL,
                    next_review_at timestamptz NOT NULL,
                    last_reviewed_at timestamptz NULL
                );

                CREATE INDEX IF NOT EXISTS ix_app_cards_user_due ON app_cards(user_id, next_review_at);

                CREATE TABLE IF NOT EXISTS app_access_codes (
                    code text PRIMARY KEY,
                    plan text NOT NULL DEFAULT 'Free',
                    features jsonb NOT NULL DEFAULT '{}'::jsonb,
                    max_uses integer NOT NULL DEFAULT 1,
                    uses integer NOT NULL DEFAULT 0,
                    created_at timestamptz NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async Task<UserProfile> GetOrCreateUserAsync(UserIdentity identity)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var existing = await FindUserAsync(connection, transaction, identity.StorageKey);
        if (existing is not null)
        {
            existing.Source = identity.Source;
            if (!string.IsNullOrWhiteSpace(identity.DisplayName))
            {
                existing.DisplayName = identity.DisplayName;
            }
            existing.LastSeenAt = DateTimeOffset.UtcNow;

            await UpsertUserAsync(connection, transaction, existing);
            await transaction.CommitAsync();
            return existing;
        }

        var profile = new UserProfile
        {
            Id = identity.StorageKey,
            Source = identity.Source,
            DisplayName = identity.DisplayName,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };

        await UpsertUserAsync(connection, transaction, profile);
        foreach (var card in DeckState.CreateSeed().Cards)
        {
            await InsertCardAsync(connection, transaction, profile.Id, card);
        }

        await transaction.CommitAsync();
        return profile;
    }

    public async Task<DeckState> GetDeckAsync(string userId)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, front, back, example, prompt, answer, notes, type, box,
                   total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at
            FROM app_cards
            WHERE user_id = @user_id
            ORDER BY created_at DESC;
            """;
        command.Parameters.AddWithValue("user_id", userId);

        var deck = new DeckState();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            deck.Cards.Add(ReadCard(reader));
        }

        return deck;
    }

    public async Task AddCardAsync(string userId, FlashCard card)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await InsertCardAsync(connection, null, userId, card);
    }

    public async Task<FlashCard?> UpdateCardAsync(string userId, Guid cardId, Action<FlashCard> update)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var card = await FindCardAsync(connection, transaction, userId, cardId);
        if (card is null) return null;

        update(card);
        await UpdateCardRowAsync(connection, transaction, userId, card);
        await transaction.CommitAsync();
        return card;
    }

    public async Task<bool> DeleteCardAsync(string userId, Guid cardId)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_cards WHERE user_id = @user_id AND id = @id;";
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("id", cardId);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> ImportCardsAsync(string userId, List<FlashCard> cards, ImportMode mode)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (mode == ImportMode.Replace)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM app_cards WHERE user_id = @user_id;";
            delete.Parameters.AddWithValue("user_id", userId);
            await delete.ExecuteNonQueryAsync();
        }

        var existingIds = await GetCardIdsAsync(connection, transaction, userId);
        var imported = 0;
        foreach (var card in cards)
        {
            if (card.Id == Guid.Empty || existingIds.Contains(card.Id))
            {
                card.Id = Guid.NewGuid();
            }

            card.CreatedAt = card.CreatedAt == default ? DateTimeOffset.UtcNow : card.CreatedAt;
            card.NextReviewAt = card.NextReviewAt == default ? DateTimeOffset.UtcNow : card.NextReviewAt;
            await InsertCardAsync(connection, transaction, userId, card);
            existingIds.Add(card.Id);
            imported++;
        }

        await transaction.CommitAsync();
        return imported;
    }

    public async Task<List<UserProfile>> GetUsersAsync()
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source, display_name, is_active, plan, features::text, access_code, created_at, last_seen_at
            FROM app_users
            ORDER BY last_seen_at DESC;
            """;

        var users = new List<UserProfile>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(ReadUser(reader));
        }

        return users;
    }

    public async Task<UserProfile?> UpdateUserAsync(string id, Action<UserProfile> update)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var user = await FindUserAsync(connection, transaction, id);
        if (user is null) return null;

        update(user);
        await UpsertUserAsync(connection, transaction, user);
        await transaction.CommitAsync();
        return user;
    }

    public async Task<AccessCode> CreateAccessCodeAsync(CreateAccessCodeRequest request)
    {
        await EnsureReadyAsync();
        var code = new AccessCode
        {
            Code = string.IsNullOrWhiteSpace(request.Code) ? GenerateCode() : request.Code.Trim().ToUpperInvariant(),
            Plan = string.IsNullOrWhiteSpace(request.Plan) ? "Free" : request.Plan.Trim(),
            Features = request.Features ?? FeatureSet.AllEnabled(),
            MaxUses = Math.Max(1, request.MaxUses ?? 1),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_access_codes (code, plan, features, max_uses, uses, created_at)
            VALUES (@code, @plan, @features, @max_uses, @uses, @created_at)
            ON CONFLICT (code) DO UPDATE SET
                plan = EXCLUDED.plan,
                features = EXCLUDED.features,
                max_uses = EXCLUDED.max_uses;
            """;
        AddAccessCodeParameters(command, code);
        await command.ExecuteNonQueryAsync();
        return code;
    }

    public async Task<List<AccessCode>> GetAccessCodesAsync()
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT code, plan, features::text, max_uses, uses, created_at
            FROM app_access_codes
            ORDER BY created_at DESC;
            """;

        var codes = new List<AccessCode>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            codes.Add(ReadAccessCode(reader));
        }

        return codes;
    }

    public async Task<RedeemResult> RedeemCodeAsync(string userId, string codeText)
    {
        await EnsureReadyAsync();
        var normalized = codeText.Trim().ToUpperInvariant();

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var code = await FindAccessCodeAsync(connection, transaction, normalized);
        if (code is null) return RedeemResult.Fail("کد پیدا نشد.");
        if (code.Uses >= code.MaxUses) return RedeemResult.Fail("ظرفیت استفاده این کد تمام شده است.");

        var user = await FindUserAsync(connection, transaction, userId);
        if (user is null) return RedeemResult.Fail("کاربر پیدا نشد.");

        user.Plan = code.Plan;
        user.Features = code.Features;
        user.IsActive = true;
        user.AccessCode = code.Code;
        code.Uses++;

        await UpsertUserAsync(connection, transaction, user);
        await using var usage = connection.CreateCommand();
        usage.Transaction = transaction;
        usage.CommandText = "UPDATE app_access_codes SET uses = @uses WHERE code = @code;";
        usage.Parameters.AddWithValue("uses", code.Uses);
        usage.Parameters.AddWithValue("code", code.Code);
        await usage.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
        return RedeemResult.Ok(user);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    private static async Task<UserProfile?> FindUserAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string id)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, source, display_name, is_active, plan, features::text, access_code, created_at, last_seen_at
            FROM app_users
            WHERE id = @id
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadUser(reader) : null;
    }

    private static async Task UpsertUserAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, UserProfile user)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_users (id, source, display_name, is_active, plan, features, access_code, created_at, last_seen_at)
            VALUES (@id, @source, @display_name, @is_active, @plan, @features, @access_code, @created_at, @last_seen_at)
            ON CONFLICT (id) DO UPDATE SET
                source = EXCLUDED.source,
                display_name = EXCLUDED.display_name,
                is_active = EXCLUDED.is_active,
                plan = EXCLUDED.plan,
                features = EXCLUDED.features,
                access_code = EXCLUDED.access_code,
                last_seen_at = EXCLUDED.last_seen_at;
            """;
        command.Parameters.AddWithValue("id", user.Id);
        command.Parameters.AddWithValue("source", user.Source);
        command.Parameters.AddWithValue("display_name", user.DisplayName);
        command.Parameters.AddWithValue("is_active", user.IsActive);
        command.Parameters.AddWithValue("plan", user.Plan);
        command.Parameters.Add("features", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(user.Features, JsonOptions);
        command.Parameters.AddWithValue("access_code", user.AccessCode);
        command.Parameters.AddWithValue("created_at", user.CreatedAt);
        command.Parameters.AddWithValue("last_seen_at", user.LastSeenAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertCardAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string userId, FlashCard card)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_cards
                (id, user_id, front, back, example, prompt, answer, notes, type, box,
                 total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at)
            VALUES
                (@id, @user_id, @front, @back, @example, @prompt, @answer, @notes, @type, @box,
                 @total_reviews, @correct_reviews, @created_at, @next_review_at, @last_reviewed_at);
            """;
        AddCardParameters(command, userId, card);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateCardRowAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, FlashCard card)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE app_cards SET
                front = @front,
                back = @back,
                example = @example,
                prompt = @prompt,
                answer = @answer,
                notes = @notes,
                type = @type,
                box = @box,
                total_reviews = @total_reviews,
                correct_reviews = @correct_reviews,
                created_at = @created_at,
                next_review_at = @next_review_at,
                last_reviewed_at = @last_reviewed_at
            WHERE id = @id AND user_id = @user_id;
            """;
        AddCardParameters(command, userId, card);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<FlashCard?> FindCardAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, Guid cardId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, front, back, example, prompt, answer, notes, type, box,
                   total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at
            FROM app_cards
            WHERE user_id = @user_id AND id = @id
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("id", cardId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadCard(reader) : null;
    }

    private static async Task<HashSet<Guid>> GetCardIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM app_cards WHERE user_id = @user_id;";
        command.Parameters.AddWithValue("user_id", userId);

        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<AccessCode?> FindAccessCodeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT code, plan, features::text, max_uses, uses, created_at
            FROM app_access_codes
            WHERE code = @code
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadAccessCode(reader) : null;
    }

    private static UserProfile ReadUser(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Source = reader.GetString(1),
        DisplayName = reader.GetString(2),
        IsActive = reader.GetBoolean(3),
        Plan = reader.GetString(4),
        Features = DeserializeFeatures(reader.GetString(5)),
        AccessCode = reader.GetString(6),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
        LastSeenAt = reader.GetFieldValue<DateTimeOffset>(8)
    };

    private static FlashCard ReadCard(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Front = reader.GetString(1),
        Back = reader.GetString(2),
        Example = reader.GetString(3),
        Prompt = reader.GetString(4),
        Answer = reader.GetString(5),
        Notes = reader.GetString(6),
        Type = Enum.TryParse<CardType>(reader.GetString(7), true, out var type) ? type : CardType.Word,
        Box = reader.GetInt32(8),
        TotalReviews = reader.GetInt32(9),
        CorrectReviews = reader.GetInt32(10),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
        NextReviewAt = reader.GetFieldValue<DateTimeOffset>(12),
        LastReviewedAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13)
    };

    private static AccessCode ReadAccessCode(NpgsqlDataReader reader) => new()
    {
        Code = reader.GetString(0),
        Plan = reader.GetString(1),
        Features = DeserializeFeatures(reader.GetString(2)),
        MaxUses = reader.GetInt32(3),
        Uses = reader.GetInt32(4),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(5)
    };

    private static void AddCardParameters(NpgsqlCommand command, string userId, FlashCard card)
    {
        command.Parameters.AddWithValue("id", card.Id);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("front", card.Front);
        command.Parameters.AddWithValue("back", card.Back);
        command.Parameters.AddWithValue("example", card.Example);
        command.Parameters.AddWithValue("prompt", card.Prompt);
        command.Parameters.AddWithValue("answer", card.Answer);
        command.Parameters.AddWithValue("notes", card.Notes);
        command.Parameters.AddWithValue("type", card.Type.ToString());
        command.Parameters.AddWithValue("box", card.Box);
        command.Parameters.AddWithValue("total_reviews", card.TotalReviews);
        command.Parameters.AddWithValue("correct_reviews", card.CorrectReviews);
        command.Parameters.AddWithValue("created_at", card.CreatedAt);
        command.Parameters.AddWithValue("next_review_at", card.NextReviewAt);
        command.Parameters.Add("last_reviewed_at", NpgsqlDbType.TimestampTz).Value = (object?)card.LastReviewedAt ?? DBNull.Value;
    }

    private static void AddAccessCodeParameters(NpgsqlCommand command, AccessCode code)
    {
        command.Parameters.AddWithValue("code", code.Code);
        command.Parameters.AddWithValue("plan", code.Plan);
        command.Parameters.Add("features", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(code.Features, JsonOptions);
        command.Parameters.AddWithValue("max_uses", code.MaxUses);
        command.Parameters.AddWithValue("uses", code.Uses);
        command.Parameters.AddWithValue("created_at", code.CreatedAt);
    }

    private static FeatureSet DeserializeFeatures(string json)
    {
        return JsonSerializer.Deserialize<FeatureSet>(json, JsonOptions) ?? FeatureSet.AllEnabled();
    }

    private static string GenerateCode()
    {
        Span<byte> bytes = stackalloc byte[5];
        RandomNumberGenerator.Fill(bytes);
        return $"LL-{Convert.ToHexString(bytes)}";
    }

    private static string GetConnectionString(IConfiguration config)
    {
        var raw = config["POSTGRES_CONNECTION_STRING"]
            ?? config["DATABASE_URL"]
            ?? config.GetConnectionString("Default");

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("اتصال Postgres تنظیم نشده است. در CapRover مقدار DATABASE_URL یا POSTGRES_CONNECTION_STRING را وارد کنید.");
        }

        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty,
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            SslMode = SslMode.Prefer
        };

        return builder.ConnectionString;
    }
}
