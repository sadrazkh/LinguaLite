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

    public string ProviderName => "postgres";

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
                    telegram_id text NOT NULL DEFAULT '',
                    telegram_username text NOT NULL DEFAULT '',
                    telegram_chat_id bigint NULL,
                    is_active boolean NOT NULL DEFAULT true,
                    plan text NOT NULL DEFAULT 'Free',
                    features jsonb NOT NULL DEFAULT '{}'::jsonb,
                    access_code text NOT NULL DEFAULT '',
                    reminders_enabled boolean NOT NULL DEFAULT true,
                    reminder_hour integer NULL,
                    last_reminder_at timestamptz NULL,
                    created_at timestamptz NOT NULL,
                    last_seen_at timestamptz NOT NULL
                );

                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_id text NOT NULL DEFAULT '';
                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_username text NOT NULL DEFAULT '';
                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_chat_id bigint NULL;
                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS reminders_enabled boolean NOT NULL DEFAULT true;
                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS reminder_hour integer NULL;
                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS last_reminder_at timestamptz NULL;

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

                CREATE TABLE IF NOT EXISTS app_plans (
                    id text PRIMARY KEY,
                    name text NOT NULL,
                    badge_color text NOT NULL DEFAULT '#16a34a',
                    badge_text_color text NOT NULL DEFAULT '#ffffff',
                    features jsonb NOT NULL DEFAULT '{}'::jsonb,
                    ai_daily_limit integer NOT NULL DEFAULT 20,
                    ai_monthly_limit integer NOT NULL DEFAULT 300,
                    dictionary_daily_limit integer NOT NULL DEFAULT 30,
                    dictionary_monthly_limit integer NOT NULL DEFAULT 600,
                    correction_daily_limit integer NOT NULL DEFAULT 15,
                    correction_monthly_limit integer NOT NULL DEFAULT 300,
                    card_limit integer NOT NULL DEFAULT 200,
                    sort_order integer NOT NULL DEFAULT 0,
                    is_default boolean NOT NULL DEFAULT false,
                    created_at timestamptz NOT NULL
                );

                ALTER TABLE app_plans ADD COLUMN IF NOT EXISTS badge_color text NOT NULL DEFAULT '#16a34a';
                ALTER TABLE app_plans ADD COLUMN IF NOT EXISTS badge_text_color text NOT NULL DEFAULT '#ffffff';
                ALTER TABLE app_plans ADD COLUMN IF NOT EXISTS dictionary_daily_limit integer NOT NULL DEFAULT 30;
                ALTER TABLE app_plans ADD COLUMN IF NOT EXISTS dictionary_monthly_limit integer NOT NULL DEFAULT 600;
                ALTER TABLE app_plans ADD COLUMN IF NOT EXISTS correction_daily_limit integer NOT NULL DEFAULT 15;
                ALTER TABLE app_plans ADD COLUMN IF NOT EXISTS correction_monthly_limit integer NOT NULL DEFAULT 300;

                CREATE TABLE IF NOT EXISTS app_ai_usage (
                    user_id text NOT NULL,
                    usage_date date NOT NULL,
                    count integer NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, usage_date)
                );

                CREATE TABLE IF NOT EXISTS app_ai_tool_usage (
                    user_id text NOT NULL,
                    tool text NOT NULL DEFAULT 'card',
                    usage_date date NOT NULL,
                    count integer NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, tool, usage_date)
                );

                CREATE TABLE IF NOT EXISTS app_browser_login_codes (
                    code text PRIMARY KEY,
                    user_id text NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
                    created_at timestamptz NOT NULL,
                    expires_at timestamptz NOT NULL,
                    used_at timestamptz NULL
                );

                CREATE INDEX IF NOT EXISTS ix_browser_login_codes_user ON app_browser_login_codes(user_id);

                CREATE TABLE IF NOT EXISTS app_browser_sessions (
                    token_hash text PRIMARY KEY,
                    user_id text NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
                    created_at timestamptz NOT NULL,
                    last_seen_at timestamptz NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_browser_sessions_user ON app_browser_sessions(user_id);

                CREATE TABLE IF NOT EXISTS app_user_daily_activity (
                    user_id text NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
                    activity_date date NOT NULL,
                    first_seen_at timestamptz NOT NULL,
                    last_seen_at timestamptz NOT NULL,
                    request_count integer NOT NULL DEFAULT 0,
                    cards_added integer NOT NULL DEFAULT 0,
                    reviews integer NOT NULL DEFAULT 0,
                    ai_card integer NOT NULL DEFAULT 0,
                    ai_dictionary integer NOT NULL DEFAULT 0,
                    ai_correction integer NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, activity_date)
                );

                CREATE TABLE IF NOT EXISTS app_settings (
                    key text PRIMARY KEY,
                    value jsonb NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
            await SeedPlansAsync(connection);
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
            if (!string.IsNullOrWhiteSpace(identity.TelegramId))
            {
                existing.TelegramId = identity.TelegramId;
            }
            if (!string.IsNullOrWhiteSpace(identity.TelegramUsername))
            {
                existing.TelegramUsername = identity.TelegramUsername;
            }
            if (identity.TelegramChatId.HasValue)
            {
                existing.TelegramChatId = identity.TelegramChatId;
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
            TelegramId = identity.TelegramId,
            TelegramUsername = identity.TelegramUsername,
            TelegramChatId = identity.TelegramChatId,
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
            SELECT id, source, display_name, telegram_id, telegram_username, telegram_chat_id,
                   is_active, plan, features::text, access_code, reminders_enabled, reminder_hour,
                   last_reminder_at, created_at, last_seen_at
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

    public async Task<AccessCode?> UpdateAccessCodeAsync(string codeText, UpdateAccessCodeRequest request)
    {
        await EnsureReadyAsync();
        var normalized = codeText.Trim().ToUpperInvariant();

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var code = await FindAccessCodeAsync(connection, transaction, normalized);
        if (code is null) return null;

        if (!string.IsNullOrWhiteSpace(request.Plan)) code.Plan = request.Plan.Trim();
        if (request.Features is not null) code.Features = request.Features;
        if (request.MaxUses.HasValue) code.MaxUses = Math.Max(code.Uses, request.MaxUses.Value);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE app_access_codes
            SET plan = @plan,
                features = @features,
                max_uses = @max_uses
            WHERE code = @code;
            """;
        command.Parameters.AddWithValue("code", code.Code);
        command.Parameters.AddWithValue("plan", code.Plan);
        command.Parameters.Add("features", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(code.Features, JsonOptions);
        command.Parameters.AddWithValue("max_uses", code.MaxUses);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return code;
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

    public async Task<List<PlanDefinition>> GetPlansAsync()
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, badge_color, badge_text_color, features::text,
                   ai_daily_limit, ai_monthly_limit,
                   dictionary_daily_limit, dictionary_monthly_limit,
                   correction_daily_limit, correction_monthly_limit,
                   card_limit, sort_order, is_default, created_at
            FROM app_plans
            ORDER BY sort_order, name;
            """;

        var plans = new List<PlanDefinition>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plans.Add(ReadPlan(reader));
        }

        return plans;
    }

    public async Task<PlanDefinition> UpsertPlanAsync(PlanDefinition plan)
    {
        await EnsureReadyAsync();
        plan.Id = NormalizePlanId(plan.Id);
        plan.Name = string.IsNullOrWhiteSpace(plan.Name) ? plan.Id : plan.Name.Trim();
        plan.CreatedAt = plan.CreatedAt == default ? DateTimeOffset.UtcNow : plan.CreatedAt;

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (plan.IsDefault)
        {
            await using var unset = connection.CreateCommand();
            unset.Transaction = transaction;
            unset.CommandText = "UPDATE app_plans SET is_default = false;";
            await unset.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_plans
                (id, name, badge_color, badge_text_color, features,
                 ai_daily_limit, ai_monthly_limit,
                 dictionary_daily_limit, dictionary_monthly_limit,
                 correction_daily_limit, correction_monthly_limit,
                 card_limit, sort_order, is_default, created_at)
            VALUES
                (@id, @name, @badge_color, @badge_text_color, @features,
                 @ai_daily_limit, @ai_monthly_limit,
                 @dictionary_daily_limit, @dictionary_monthly_limit,
                 @correction_daily_limit, @correction_monthly_limit,
                 @card_limit, @sort_order, @is_default, @created_at)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                badge_color = EXCLUDED.badge_color,
                badge_text_color = EXCLUDED.badge_text_color,
                features = EXCLUDED.features,
                ai_daily_limit = EXCLUDED.ai_daily_limit,
                ai_monthly_limit = EXCLUDED.ai_monthly_limit,
                dictionary_daily_limit = EXCLUDED.dictionary_daily_limit,
                dictionary_monthly_limit = EXCLUDED.dictionary_monthly_limit,
                correction_daily_limit = EXCLUDED.correction_daily_limit,
                correction_monthly_limit = EXCLUDED.correction_monthly_limit,
                card_limit = EXCLUDED.card_limit,
                sort_order = EXCLUDED.sort_order,
                is_default = EXCLUDED.is_default;
            """;
        AddPlanParameters(command, plan);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return plan;
    }

    public async Task<bool> DeletePlanAsync(string id)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_plans WHERE id = @id AND is_default = false;";
        command.Parameters.AddWithValue("id", NormalizePlanId(id));
        return await command.ExecuteNonQueryAsync() > 0;
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

    public async Task<AiUsageSummary> GetAiUsageAsync(string userId, string planName, AiToolKind tool)
    {
        await EnsureReadyAsync();
        var plan = await GetEffectivePlanAsync(planName);
        var toolKey = ToolKey(tool);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN usage_date = @today THEN count ELSE 0 END), 0)::int AS today_count,
                COALESCE(SUM(CASE WHEN usage_date >= @month_start THEN count ELSE 0 END), 0)::int AS month_count
            FROM app_ai_tool_usage
            WHERE user_id = @user_id AND tool = @tool;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("tool", toolKey);
        command.Parameters.AddWithValue("today", today);
        command.Parameters.AddWithValue("month_start", monthStart);

        await using var reader = await command.ExecuteReaderAsync();
        var summary = new AiUsageSummary
        {
            Tool = toolKey,
            DailyLimit = DailyLimitFor(plan, tool),
            MonthlyLimit = MonthlyLimitFor(plan, tool)
        };
        if (await reader.ReadAsync())
        {
            summary.Today = reader.GetInt32(0);
            summary.ThisMonth = reader.GetInt32(1);
        }

        ApplyAiAllowance(summary);
        return summary;
    }

    public async Task<AiUsageSummary> TryConsumeAiRequestAsync(string userId, string planName, AiToolKind tool)
    {
        var summary = await GetAiUsageAsync(userId, planName, tool);
        if (!summary.Allowed) return summary;

        var toolKey = ToolKey(tool);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_ai_tool_usage (user_id, tool, usage_date, count)
            VALUES (@user_id, @tool, @usage_date, 1)
            ON CONFLICT (user_id, tool, usage_date) DO UPDATE SET count = app_ai_tool_usage.count + 1;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("tool", toolKey);
        command.Parameters.AddWithValue("usage_date", today);
        await command.ExecuteNonQueryAsync();

        summary.Today++;
        summary.ThisMonth++;
        ApplyAiAllowance(summary);
        return summary;
    }

    public async Task<BrowserLoginCode> CreateBrowserLoginCodeAsync(string userId, TimeSpan ttl)
    {
        await EnsureReadyAsync();
        var code = new BrowserLoginCode
        {
            Code = GenerateNumericCode(),
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl)
        };

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                DELETE FROM app_browser_login_codes
                WHERE user_id = @user_id OR expires_at <= @now OR used_at IS NOT NULL;
                """;
            cleanup.Parameters.AddWithValue("user_id", userId);
            cleanup.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            await cleanup.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO app_browser_login_codes (code, user_id, created_at, expires_at, used_at)
                VALUES (@code, @user_id, @created_at, @expires_at, NULL);
                """;
            command.Parameters.AddWithValue("code", code.Code);
            command.Parameters.AddWithValue("user_id", code.UserId);
            command.Parameters.AddWithValue("created_at", code.CreatedAt);
            command.Parameters.AddWithValue("expires_at", code.ExpiresAt);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return code;
    }

    public async Task<BrowserLoginResult> RedeemBrowserLoginCodeAsync(string codeText)
    {
        await EnsureReadyAsync();
        var normalized = NormalizeLoginCode(codeText);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT code, user_id, created_at, expires_at, used_at
            FROM app_browser_login_codes
            WHERE code = @code
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("code", normalized);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return BrowserLoginResult.Fail("کد ورود پیدا نشد.");

        var loginCode = new BrowserLoginCode
        {
            Code = reader.GetString(0),
            UserId = reader.GetString(1),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(3),
            UsedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)
        };
        await reader.DisposeAsync();

        if (loginCode.UsedAt.HasValue) return BrowserLoginResult.Fail("این کد قبلا استفاده شده است.");
        if (loginCode.ExpiresAt <= DateTimeOffset.UtcNow) return BrowserLoginResult.Fail("کد ورود منقضی شده است. از ربات یک کد جدید بگیر.");

        var user = await FindUserAsync(connection, transaction, loginCode.UserId);
        if (user is null) return BrowserLoginResult.Fail("اکانت تلگرام پیدا نشد.");

        var token = GenerateSessionToken();
        await using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                INSERT INTO app_browser_sessions (token_hash, user_id, created_at, last_seen_at)
                VALUES (@token_hash, @user_id, @created_at, @last_seen_at);

                UPDATE app_browser_login_codes SET used_at = @used_at WHERE code = @code;
                """;
            session.Parameters.AddWithValue("token_hash", HashToken(token));
            session.Parameters.AddWithValue("user_id", user.Id);
            session.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
            session.Parameters.AddWithValue("last_seen_at", DateTimeOffset.UtcNow);
            session.Parameters.AddWithValue("used_at", DateTimeOffset.UtcNow);
            session.Parameters.AddWithValue("code", loginCode.Code);
            await session.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return BrowserLoginResult.Ok(token, user);
    }

    public async Task<UserProfile?> GetUserBySessionTokenAsync(string sessionToken)
    {
        await EnsureReadyAsync();
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        var hash = HashToken(sessionToken);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT user_id
            FROM app_browser_sessions
            WHERE token_hash = @token_hash
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("token_hash", hash);
        var userId = await command.ExecuteScalarAsync() as string;
        if (string.IsNullOrWhiteSpace(userId)) return null;

        var user = await FindUserAsync(connection, transaction, userId);
        if (user is null) return null;

        user.LastSeenAt = DateTimeOffset.UtcNow;
        await UpsertUserAsync(connection, transaction, user);

        await using var touch = connection.CreateCommand();
        touch.Transaction = transaction;
        touch.CommandText = "UPDATE app_browser_sessions SET last_seen_at = @last_seen_at WHERE token_hash = @token_hash;";
        touch.Parameters.AddWithValue("last_seen_at", DateTimeOffset.UtcNow);
        touch.Parameters.AddWithValue("token_hash", hash);
        await touch.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
        return user;
    }

    public async Task RecordActivityAsync(string userId, ActivityKind kind, int count = 1)
    {
        await EnsureReadyAsync();
        var safeCount = Math.Max(1, count);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var increments = ActivityIncrements(kind, safeCount);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_user_daily_activity
                (user_id, activity_date, first_seen_at, last_seen_at, request_count,
                 cards_added, reviews, ai_card, ai_dictionary, ai_correction)
            VALUES
                (@user_id, @activity_date, @now, @now, @request_count,
                 @cards_added, @reviews, @ai_card, @ai_dictionary, @ai_correction)
            ON CONFLICT (user_id, activity_date) DO UPDATE SET
                last_seen_at = EXCLUDED.last_seen_at,
                request_count = app_user_daily_activity.request_count + EXCLUDED.request_count,
                cards_added = app_user_daily_activity.cards_added + EXCLUDED.cards_added,
                reviews = app_user_daily_activity.reviews + EXCLUDED.reviews,
                ai_card = app_user_daily_activity.ai_card + EXCLUDED.ai_card,
                ai_dictionary = app_user_daily_activity.ai_dictionary + EXCLUDED.ai_dictionary,
                ai_correction = app_user_daily_activity.ai_correction + EXCLUDED.ai_correction;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("activity_date", today);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("request_count", increments.Requests);
        command.Parameters.AddWithValue("cards_added", increments.CardsAdded);
        command.Parameters.AddWithValue("reviews", increments.Reviews);
        command.Parameters.AddWithValue("ai_card", increments.AiCard);
        command.Parameters.AddWithValue("ai_dictionary", increments.AiDictionary);
        command.Parameters.AddWithValue("ai_correction", increments.AiCorrection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<AdminUserMetrics>> GetAdminUserMetricsAsync()
    {
        await EnsureReadyAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                u.id,
                COALESCE(card_counts.total_cards, 0)::int,
                COALESCE(card_counts.due_cards, 0)::int,
                COALESCE(a.request_count, 0)::int,
                CASE
                    WHEN a.first_seen_at IS NULL THEN 0
                    WHEN a.request_count > 0 THEN GREATEST(1, CEIL(EXTRACT(EPOCH FROM (a.last_seen_at - a.first_seen_at)) / 60.0))::int
                    ELSE 0
                END AS active_minutes,
                COALESCE(a.cards_added, 0)::int,
                COALESCE(a.reviews, 0)::int,
                COALESCE(a.ai_card, 0)::int,
                COALESCE(a.ai_dictionary, 0)::int,
                COALESCE(a.ai_correction, 0)::int
            FROM app_users u
            LEFT JOIN LATERAL (
                SELECT
                    COUNT(*)::int AS total_cards,
                    COUNT(*) FILTER (WHERE next_review_at <= now())::int AS due_cards
                FROM app_cards c
                WHERE c.user_id = u.id
            ) card_counts ON true
            LEFT JOIN app_user_daily_activity a
                ON a.user_id = u.id AND a.activity_date = @today
            ORDER BY u.last_seen_at DESC;
            """;
        command.Parameters.AddWithValue("today", today);

        var result = new List<AdminUserMetrics>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AdminUserMetrics
            {
                UserId = reader.GetString(0),
                TotalCards = reader.GetInt32(1),
                DueCards = reader.GetInt32(2),
                RequestsToday = reader.GetInt32(3),
                ActiveMinutesToday = reader.GetInt32(4),
                CardsAddedToday = reader.GetInt32(5),
                ReviewsToday = reader.GetInt32(6),
                AiCardToday = reader.GetInt32(7),
                AiDictionaryToday = reader.GetInt32(8),
                AiCorrectionToday = reader.GetInt32(9)
            });
        }

        return result;
    }

    public async Task<AppSettingsState> GetSettingsAsync()
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value::text FROM app_settings WHERE key = 'app';";
        var value = await command.ExecuteScalarAsync();
        return value is string json
            ? JsonSerializer.Deserialize<AppSettingsState>(json, JsonOptions) ?? new AppSettingsState()
            : new AppSettingsState();
    }

    public async Task<AppSettingsState> UpdateSettingsAsync(Action<AppSettingsState> update)
    {
        await EnsureReadyAsync();
        var settings = await GetSettingsAsync();
        update(settings);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value)
            VALUES ('app', @value)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value;
            """;
        command.Parameters.Add("value", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(settings, JsonOptions);
        await command.ExecuteNonQueryAsync();
        return settings;
    }

    public async Task MarkReminderSentAsync(string userId, DateTimeOffset sentAt)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_users SET last_reminder_at = @sent_at WHERE id = @id;";
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("sent_at", sentAt);
        await command.ExecuteNonQueryAsync();
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
            SELECT id, source, display_name, telegram_id, telegram_username, telegram_chat_id,
                   is_active, plan, features::text, access_code, reminders_enabled, reminder_hour,
                   last_reminder_at, created_at, last_seen_at
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
            INSERT INTO app_users
                (id, source, display_name, telegram_id, telegram_username, telegram_chat_id, is_active, plan,
                 features, access_code, reminders_enabled, reminder_hour, last_reminder_at, created_at, last_seen_at)
            VALUES
                (@id, @source, @display_name, @telegram_id, @telegram_username, @telegram_chat_id, @is_active, @plan,
                 @features, @access_code, @reminders_enabled, @reminder_hour, @last_reminder_at, @created_at, @last_seen_at)
            ON CONFLICT (id) DO UPDATE SET
                source = EXCLUDED.source,
                display_name = EXCLUDED.display_name,
                telegram_id = EXCLUDED.telegram_id,
                telegram_username = EXCLUDED.telegram_username,
                telegram_chat_id = EXCLUDED.telegram_chat_id,
                is_active = EXCLUDED.is_active,
                plan = EXCLUDED.plan,
                features = EXCLUDED.features,
                access_code = EXCLUDED.access_code,
                reminders_enabled = EXCLUDED.reminders_enabled,
                reminder_hour = EXCLUDED.reminder_hour,
                last_reminder_at = EXCLUDED.last_reminder_at,
                last_seen_at = EXCLUDED.last_seen_at;
            """;
        command.Parameters.AddWithValue("id", user.Id);
        command.Parameters.AddWithValue("source", user.Source);
        command.Parameters.AddWithValue("display_name", user.DisplayName);
        command.Parameters.AddWithValue("telegram_id", user.TelegramId);
        command.Parameters.AddWithValue("telegram_username", user.TelegramUsername);
        command.Parameters.Add("telegram_chat_id", NpgsqlDbType.Bigint).Value = (object?)user.TelegramChatId ?? DBNull.Value;
        command.Parameters.AddWithValue("is_active", user.IsActive);
        command.Parameters.AddWithValue("plan", user.Plan);
        command.Parameters.Add("features", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(user.Features, JsonOptions);
        command.Parameters.AddWithValue("access_code", user.AccessCode);
        command.Parameters.AddWithValue("reminders_enabled", user.RemindersEnabled);
        command.Parameters.Add("reminder_hour", NpgsqlDbType.Integer).Value = (object?)user.ReminderHour ?? DBNull.Value;
        command.Parameters.Add("last_reminder_at", NpgsqlDbType.TimestampTz).Value = (object?)user.LastReminderAt ?? DBNull.Value;
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
        TelegramId = reader.GetString(3),
        TelegramUsername = reader.GetString(4),
        TelegramChatId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        IsActive = reader.GetBoolean(6),
        Plan = reader.GetString(7),
        Features = DeserializeFeatures(reader.GetString(8)),
        AccessCode = reader.GetString(9),
        RemindersEnabled = reader.GetBoolean(10),
        ReminderHour = reader.IsDBNull(11) ? null : reader.GetInt32(11),
        LastReminderAt = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(13),
        LastSeenAt = reader.GetFieldValue<DateTimeOffset>(14)
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

    private static PlanDefinition ReadPlan(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        BadgeColor = reader.GetString(2),
        BadgeTextColor = reader.GetString(3),
        Features = DeserializeFeatures(reader.GetString(4)),
        AiDailyLimit = reader.GetInt32(5),
        AiMonthlyLimit = reader.GetInt32(6),
        DictionaryDailyLimit = reader.GetInt32(7),
        DictionaryMonthlyLimit = reader.GetInt32(8),
        CorrectionDailyLimit = reader.GetInt32(9),
        CorrectionMonthlyLimit = reader.GetInt32(10),
        CardLimit = reader.GetInt32(11),
        SortOrder = reader.GetInt32(12),
        IsDefault = reader.GetBoolean(13),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(14)
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

    private static void AddPlanParameters(NpgsqlCommand command, PlanDefinition plan)
    {
        command.Parameters.AddWithValue("id", plan.Id);
        command.Parameters.AddWithValue("name", plan.Name);
        command.Parameters.AddWithValue("badge_color", string.IsNullOrWhiteSpace(plan.BadgeColor) ? "#16a34a" : plan.BadgeColor);
        command.Parameters.AddWithValue("badge_text_color", string.IsNullOrWhiteSpace(plan.BadgeTextColor) ? "#ffffff" : plan.BadgeTextColor);
        command.Parameters.Add("features", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(plan.Features, JsonOptions);
        command.Parameters.AddWithValue("ai_daily_limit", plan.AiDailyLimit);
        command.Parameters.AddWithValue("ai_monthly_limit", plan.AiMonthlyLimit);
        command.Parameters.AddWithValue("dictionary_daily_limit", plan.DictionaryDailyLimit);
        command.Parameters.AddWithValue("dictionary_monthly_limit", plan.DictionaryMonthlyLimit);
        command.Parameters.AddWithValue("correction_daily_limit", plan.CorrectionDailyLimit);
        command.Parameters.AddWithValue("correction_monthly_limit", plan.CorrectionMonthlyLimit);
        command.Parameters.AddWithValue("card_limit", plan.CardLimit);
        command.Parameters.AddWithValue("sort_order", plan.SortOrder);
        command.Parameters.AddWithValue("is_default", plan.IsDefault);
        command.Parameters.AddWithValue("created_at", plan.CreatedAt);
    }

    private static async Task SeedPlansAsync(NpgsqlConnection connection)
    {
        foreach (var plan in PlanDefinition.Defaults())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_plans
                    (id, name, badge_color, badge_text_color, features,
                     ai_daily_limit, ai_monthly_limit,
                     dictionary_daily_limit, dictionary_monthly_limit,
                     correction_daily_limit, correction_monthly_limit,
                     card_limit, sort_order, is_default, created_at)
                VALUES
                    (@id, @name, @badge_color, @badge_text_color, @features,
                     @ai_daily_limit, @ai_monthly_limit,
                     @dictionary_daily_limit, @dictionary_monthly_limit,
                     @correction_daily_limit, @correction_monthly_limit,
                     @card_limit, @sort_order, @is_default, @created_at)
                ON CONFLICT (id) DO NOTHING;
                """;
            AddPlanParameters(command, plan);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static FeatureSet DeserializeFeatures(string json)
    {
        return JsonSerializer.Deserialize<FeatureSet>(json, JsonOptions) ?? FeatureSet.AllEnabled();
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

    private static (int Requests, int CardsAdded, int Reviews, int AiCard, int AiDictionary, int AiCorrection) ActivityIncrements(ActivityKind kind, int count)
    {
        return kind switch
        {
            ActivityKind.CardAdded => (0, count, 0, 0, 0, 0),
            ActivityKind.Review => (0, 0, count, 0, 0, 0),
            ActivityKind.AiCard => (0, 0, 0, count, 0, 0),
            ActivityKind.AiDictionary => (0, 0, 0, 0, count, 0),
            ActivityKind.AiCorrection => (0, 0, 0, 0, 0, count),
            _ => (count, 0, 0, 0, 0, 0)
        };
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

    private static string GenerateCode()
    {
        Span<byte> bytes = stackalloc byte[5];
        RandomNumberGenerator.Fill(bytes);
        return $"LL-{Convert.ToHexString(bytes)}";
    }

    public static bool HasConnectionString(IConfiguration config)
    {
        return !string.IsNullOrWhiteSpace(ReadConnectionString(config));
    }

    private static string GetConnectionString(IConfiguration config)
    {
        var raw = ReadConnectionString(config);

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

    private static string? ReadConnectionString(IConfiguration config)
    {
        return config["POSTGRES_CONNECTION_STRING"]
            ?? config["DATABASE_URL"]
            ?? config.GetConnectionString("Default");
    }
}
