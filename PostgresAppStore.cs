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
                    language_level text NOT NULL DEFAULT 'B1',
                    reminders_enabled boolean NOT NULL DEFAULT true,
                    reminder_hour integer NULL,
                    last_reminder_at timestamptz NULL,
                    created_at timestamptz NOT NULL,
                    last_seen_at timestamptz NOT NULL
                );

                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_id text NOT NULL DEFAULT '';
                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_username text NOT NULL DEFAULT '';
                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_chat_id bigint NULL;
                ALTER TABLE app_users ADD COLUMN IF NOT EXISTS language_level text NOT NULL DEFAULT 'B1';
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
                    last_reviewed_at timestamptz NULL,
                    is_archived boolean NOT NULL DEFAULT false,
                    source_package_id text NOT NULL DEFAULT '',
                    source_package_card_id text NOT NULL DEFAULT '',
                    card_signature text NOT NULL DEFAULT ''
                );

                ALTER TABLE app_cards ADD COLUMN IF NOT EXISTS is_archived boolean NOT NULL DEFAULT false;
                ALTER TABLE app_cards ADD COLUMN IF NOT EXISTS source_package_id text NOT NULL DEFAULT '';
                ALTER TABLE app_cards ADD COLUMN IF NOT EXISTS source_package_card_id text NOT NULL DEFAULT '';
                ALTER TABLE app_cards ADD COLUMN IF NOT EXISTS card_signature text NOT NULL DEFAULT '';

                CREATE INDEX IF NOT EXISTS ix_app_cards_due_active
                    ON app_cards(user_id, next_review_at, box, id) WHERE is_archived = false;
                CREATE INDEX IF NOT EXISTS ix_app_cards_active_cursor
                    ON app_cards(user_id, created_at DESC, id DESC) WHERE is_archived = false;
                CREATE INDEX IF NOT EXISTS ix_app_cards_archived_cursor
                    ON app_cards(user_id, created_at DESC, id DESC) WHERE is_archived = true;
                CREATE INDEX IF NOT EXISTS ix_app_cards_signature
                    ON app_cards(user_id, card_signature);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_app_cards_package_source
                    ON app_cards(user_id, source_package_id, source_package_card_id)
                    WHERE source_package_id <> '' AND source_package_card_id <> '';

                CREATE TABLE IF NOT EXISTS app_user_deck_summaries (
                    user_id text PRIMARY KEY REFERENCES app_users(id) ON DELETE CASCADE,
                    total_cards integer NOT NULL DEFAULT 0,
                    active_cards integer NOT NULL DEFAULT 0,
                    archived_cards integer NOT NULL DEFAULT 0,
                    due_cards integer NOT NULL DEFAULT 0,
                    box_1_count integer NOT NULL DEFAULT 0,
                    box_2_count integer NOT NULL DEFAULT 0,
                    box_3_count integer NOT NULL DEFAULT 0,
                    box_4_count integer NOT NULL DEFAULT 0,
                    box_5_count integer NOT NULL DEFAULT 0,
                    total_reviews bigint NOT NULL DEFAULT 0,
                    correct_reviews bigint NOT NULL DEFAULT 0,
                    last_reviewed_at timestamptz NULL,
                    updated_at timestamptz NOT NULL DEFAULT now()
                );

                CREATE TABLE IF NOT EXISTS app_card_due_buckets (
                    user_id text NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
                    due_date date NOT NULL,
                    active_count integer NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, due_date)
                );
                CREATE INDEX IF NOT EXISTS ix_card_due_buckets_due_date
                    ON app_card_due_buckets(due_date, user_id);

                CREATE TABLE IF NOT EXISTS app_broadcast_jobs (
                    id uuid PRIMARY KEY,
                    message text NOT NULL,
                    status text NOT NULL DEFAULT 'queued',
                    matched integer NOT NULL DEFAULT 0,
                    sent integer NOT NULL DEFAULT 0,
                    skipped integer NOT NULL DEFAULT 0,
                    failed integer NOT NULL DEFAULT 0,
                    created_at timestamptz NOT NULL,
                    started_at timestamptz NULL,
                    completed_at timestamptz NULL
                );
                CREATE TABLE IF NOT EXISTS app_broadcast_recipients (
                    job_id uuid NOT NULL REFERENCES app_broadcast_jobs(id) ON DELETE CASCADE,
                    user_id text NOT NULL REFERENCES app_users(id) ON DELETE CASCADE,
                    chat_id bigint NOT NULL,
                    status text NOT NULL DEFAULT 'pending',
                    attempts integer NOT NULL DEFAULT 0,
                    next_attempt_at timestamptz NOT NULL DEFAULT now(),
                    last_error text NOT NULL DEFAULT '',
                    PRIMARY KEY (job_id, user_id)
                );
                CREATE INDEX IF NOT EXISTS ix_broadcast_recipients_pending
                    ON app_broadcast_recipients(status, next_attempt_at, job_id);

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
                    active_seconds integer NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, activity_date)
                );

                ALTER TABLE app_user_daily_activity ADD COLUMN IF NOT EXISTS active_seconds integer NOT NULL DEFAULT 0;

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
            await EnsureDeckSummaryAsync(connection, transaction, existing.Id);
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
        await EnsureDeckSummaryAsync(connection, transaction, profile.Id);

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
                   total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at,
                   is_archived, source_package_id, source_package_card_id
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

    public async Task<DeckSummary> GetDeckSummaryAsync(string userId)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await EnsureDeckSummaryAsync(connection, transaction, userId);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.active_cards,
                   COALESCE((SELECT SUM(b.active_count)::int
                             FROM app_card_due_buckets b
                             WHERE b.user_id = s.user_id AND b.due_date <= @today), 0),
                   s.box_4_count + s.box_5_count,
                   s.total_reviews,
                   s.correct_reviews,
                   s.box_1_count, s.box_2_count, s.box_3_count, s.box_4_count, s.box_5_count,
                   s.archived_cards, s.last_reviewed_at
            FROM app_user_deck_summaries s
            WHERE s.user_id = @user_id;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("today", DateOnly.FromDateTime(DateTime.UtcNow));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            await transaction.CommitAsync();
            return new DeckSummary(0, 0, 0, 0, EmptyBoxes());
        }

        var totalReviews = reader.GetInt64(3);
        var correctReviews = reader.GetInt64(4);
        var summary = new DeckSummary(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            totalReviews == 0 ? 0 : Math.Round((double)correctReviews / totalReviews * 100, 1),
            new Dictionary<int, int>
            {
                [1] = reader.GetInt32(5), [2] = reader.GetInt32(6), [3] = reader.GetInt32(7),
                [4] = reader.GetInt32(8), [5] = reader.GetInt32(9)
            },
            reader.GetInt32(0),
            reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            totalReviews,
            correctReviews);
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return summary;
    }

    public async Task<CardPage> GetCardsPageAsync(string userId, bool archived, int limit, string? cursor, IReadOnlyCollection<int>? boxes = null)
    {
        await EnsureReadyAsync();
        var pageSize = Math.Clamp(limit, 1, 100);
        var hasCursor = CardCursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId);
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, front, back, example, prompt, answer, notes, type, box,
                   total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at,
                   is_archived, source_package_id, source_package_card_id
            FROM app_cards
            WHERE user_id = @user_id AND is_archived = @archived
              AND (@has_cursor = false OR (created_at, id) < (@cursor_created_at, @cursor_id))
              AND (@use_boxes = false OR box = ANY(@boxes))
            ORDER BY created_at DESC, id DESC
            LIMIT @take;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("archived", archived);
        command.Parameters.AddWithValue("has_cursor", hasCursor);
        command.Parameters.AddWithValue("cursor_created_at", hasCursor ? cursorCreatedAt : DateTimeOffset.MaxValue);
        command.Parameters.AddWithValue("cursor_id", hasCursor ? cursorId : Guid.Empty);
        var normalizedBoxes = boxes?.Where(box => box is >= 1 and <= 5).Distinct().ToArray() ?? [];
        command.Parameters.AddWithValue("use_boxes", !archived && normalizedBoxes.Length > 0);
        command.Parameters.AddWithValue("boxes", normalizedBoxes);
        command.Parameters.AddWithValue("take", pageSize + 1);

        var items = new List<FlashCard>(pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) items.Add(ReadCard(reader));
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new CardPage(items, hasMore && items.Count > 0 ? CardCursor.Encode(items[^1].CreatedAt, items[^1].Id) : null, hasMore);
    }

    public async Task<List<FlashCard>> GetDueCardsAsync(string userId, int limit)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, front, back, example, prompt, answer, notes, type, box,
                   total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at,
                   is_archived, source_package_id, source_package_card_id
            FROM app_cards
            WHERE user_id = @user_id AND is_archived = false AND next_review_at < @tomorrow
            ORDER BY next_review_at, box, id
            LIMIT @take;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("tomorrow", LeitnerSchedule.TodayUtc().AddDays(1));
        command.Parameters.AddWithValue("take", Math.Clamp(limit, 1, 100));
        var cards = new List<FlashCard>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) cards.Add(ReadCard(reader));
        return cards;
    }

    public async Task<FlashCard?> GetCardAsync(string userId, Guid cardId)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, front, back, example, prompt, answer, notes, type, box,
                   total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at,
                   is_archived, source_package_id, source_package_card_id
            FROM app_cards
            WHERE user_id = @user_id AND id = @id;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("id", cardId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadCard(reader) : null;
    }

    public async Task AddCardAsync(string userId, FlashCard card)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await EnsureDeckSummaryAsync(connection, transaction, userId);
        await InsertCardAsync(connection, transaction, userId, card);
        await ApplyCardSummaryChangeAsync(connection, transaction, userId, null, card);
        await transaction.CommitAsync();
    }

    public async Task<FlashCard?> UpdateCardAsync(string userId, Guid cardId, Action<FlashCard> update)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var card = await FindCardAsync(connection, transaction, userId, cardId);
        if (card is null) return null;

        var before = CloneCard(card);
        update(card);
        await UpdateCardRowAsync(connection, transaction, userId, card);
        await EnsureDeckSummaryAsync(connection, transaction, userId);
        await ApplyCardSummaryChangeAsync(connection, transaction, userId, before, card);
        await transaction.CommitAsync();
        return card;
    }

    public async Task<SyncCardProgressBatchResult> SyncCardProgressBatchAsync(string userId, IReadOnlyCollection<SyncCardProgressItem> items)
    {
        await EnsureReadyAsync();
        var requested = items.Count;
        var latest = items
            .GroupBy(item => item.Id)
            .Select(group => group.MaxBy(item => item.LastReviewedAt)!)
            .ToList();
        if (latest.Count == 0) return new SyncCardProgressBatchResult(requested, 0);

        var payload = JsonSerializer.Serialize(latest.Select(item => new
        {
            id = item.Id,
            box = item.Box,
            total_reviews = item.TotalReviews,
            correct_reviews = item.CorrectReviews,
            last_reviewed_at = item.LastReviewedAt.ToUniversalTime(),
            next_review_at = item.NextReviewAt.ToUniversalTime()
        }), JsonOptions);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await EnsureDeckSummaryAsync(connection, transaction, userId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH input AS (
                SELECT *
                FROM jsonb_to_recordset(@items) AS value(
                    id uuid,
                    box integer,
                    total_reviews integer,
                    correct_reviews integer,
                    last_reviewed_at timestamptz,
                    next_review_at timestamptz)
            ),
            before_rows AS MATERIALIZED (
                SELECT c.id,
                       c.box AS old_box,
                       c.total_reviews AS old_total_reviews,
                       c.correct_reviews AS old_correct_reviews,
                       c.next_review_at AS old_next_review_at,
                       c.is_archived,
                       i.box AS new_box,
                       GREATEST(c.total_reviews, i.total_reviews) AS new_total_reviews,
                       LEAST(
                           GREATEST(c.total_reviews, i.total_reviews),
                           GREATEST(c.correct_reviews, i.correct_reviews)
                       ) AS new_correct_reviews,
                       i.last_reviewed_at AS new_last_reviewed_at,
                       i.next_review_at AS new_next_review_at
                FROM app_cards c
                JOIN input i ON i.id = c.id
                WHERE c.user_id = @user_id
                  AND c.is_archived = false
                  AND (c.last_reviewed_at IS NULL OR i.last_reviewed_at > c.last_reviewed_at)
                FOR UPDATE OF c
            ),
            updated AS (
                UPDATE app_cards c
                SET box = b.new_box,
                    total_reviews = b.new_total_reviews,
                    correct_reviews = b.new_correct_reviews,
                    last_reviewed_at = b.new_last_reviewed_at,
                    next_review_at = b.new_next_review_at
                FROM before_rows b
                WHERE c.user_id = @user_id AND c.id = b.id
                RETURNING c.id, c.box, c.total_reviews, c.correct_reviews,
                          c.next_review_at, c.last_reviewed_at, c.is_archived
            ),
            deltas AS (
                SELECT COUNT(*)::int AS applied,
                       COALESCE(SUM(u.total_reviews - b.old_total_reviews), 0)::bigint AS total_reviews_delta,
                       COALESCE(SUM(u.correct_reviews - b.old_correct_reviews), 0)::bigint AS correct_reviews_delta,
                       COALESCE(SUM((u.box = 1)::int - (b.old_box = 1)::int), 0)::int AS box_1_delta,
                       COALESCE(SUM((u.box = 2)::int - (b.old_box = 2)::int), 0)::int AS box_2_delta,
                       COALESCE(SUM((u.box = 3)::int - (b.old_box = 3)::int), 0)::int AS box_3_delta,
                       COALESCE(SUM((u.box = 4)::int - (b.old_box = 4)::int), 0)::int AS box_4_delta,
                       COALESCE(SUM((u.box = 5)::int - (b.old_box = 5)::int), 0)::int AS box_5_delta,
                       MAX(u.last_reviewed_at) AS last_reviewed_at
                FROM before_rows b
                JOIN updated u ON u.id = b.id
            ),
            summary_update AS (
                UPDATE app_user_deck_summaries s
                SET box_1_count = s.box_1_count + d.box_1_delta,
                    box_2_count = s.box_2_count + d.box_2_delta,
                    box_3_count = s.box_3_count + d.box_3_delta,
                    box_4_count = s.box_4_count + d.box_4_delta,
                    box_5_count = s.box_5_count + d.box_5_delta,
                    total_reviews = s.total_reviews + d.total_reviews_delta,
                    correct_reviews = s.correct_reviews + d.correct_reviews_delta,
                    last_reviewed_at = CASE
                        WHEN d.last_reviewed_at IS NULL THEN s.last_reviewed_at
                        WHEN s.last_reviewed_at IS NULL THEN d.last_reviewed_at
                        ELSE GREATEST(s.last_reviewed_at, d.last_reviewed_at)
                    END,
                    updated_at = now()
                FROM deltas d
                WHERE s.user_id = @user_id AND d.applied > 0
                RETURNING s.user_id
            ),
            due_changes AS (
                SELECT CAST(@user_id AS text) AS user_id,
                       (b.old_next_review_at AT TIME ZONE 'UTC')::date AS due_date,
                       -1 AS delta
                FROM before_rows b
                UNION ALL
                SELECT CAST(@user_id AS text),
                       (u.next_review_at AT TIME ZONE 'UTC')::date,
                       1
                FROM updated u
            ),
            due_grouped AS (
                SELECT user_id, due_date, SUM(delta)::int AS delta
                FROM due_changes
                GROUP BY user_id, due_date
                HAVING SUM(delta) <> 0
            ),
            due_upsert AS (
                INSERT INTO app_card_due_buckets (user_id, due_date, active_count)
                SELECT user_id, due_date, delta FROM due_grouped
                ON CONFLICT (user_id, due_date) DO UPDATE
                SET active_count = app_card_due_buckets.active_count + EXCLUDED.active_count
                RETURNING user_id
            ),
            activity_update AS (
                INSERT INTO app_user_daily_activity
                    (user_id, activity_date, first_seen_at, last_seen_at, request_count,
                     cards_added, reviews, ai_card, ai_dictionary, ai_correction)
                SELECT @user_id, @activity_date, @now, @now, 0, 0, d.applied, 0, 0, 0
                FROM deltas d
                WHERE d.applied > 0
                ON CONFLICT (user_id, activity_date) DO UPDATE SET
                    active_seconds = app_user_daily_activity.active_seconds +
                        CASE
                            WHEN EXCLUDED.last_seen_at >= app_user_daily_activity.last_seen_at
                             AND EXCLUDED.last_seen_at - app_user_daily_activity.last_seen_at <= interval '5 minutes'
                            THEN FLOOR(EXTRACT(EPOCH FROM (EXCLUDED.last_seen_at - app_user_daily_activity.last_seen_at)))::int
                            ELSE 0
                        END,
                    last_seen_at = EXCLUDED.last_seen_at,
                    reviews = app_user_daily_activity.reviews + EXCLUDED.reviews
                RETURNING user_id
            )
            SELECT applied FROM deltas;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.Add("items", NpgsqlDbType.Jsonb).Value = payload;
        command.Parameters.AddWithValue("activity_date", ReportingClock.Today(configuration));
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var applied = Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
        await transaction.CommitAsync();
        return new SyncCardProgressBatchResult(requested, applied);
    }

    public async Task<bool> DeleteCardAsync(string userId, Guid cardId)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var card = await FindCardAsync(connection, transaction, userId, cardId);
        if (card is null) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM app_cards WHERE user_id = @user_id AND id = @id;";
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("id", cardId);
        await command.ExecuteNonQueryAsync();
        await EnsureDeckSummaryAsync(connection, transaction, userId);
        await ApplyCardSummaryChangeAsync(connection, transaction, userId, card, null);
        await transaction.CommitAsync();
        return true;
    }

    public async Task<int> ImportCardsAsync(string userId, List<FlashCard> cards, ImportMode mode)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await EnsureDeckSummaryAsync(connection, transaction, userId);

        if (mode == ImportMode.Replace)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM app_cards WHERE user_id = @user_id;";
            delete.Parameters.AddWithValue("user_id", userId);
            await delete.ExecuteNonQueryAsync();
            await ResetDeckSummaryAsync(connection, transaction, userId);
        }

        var imported = 0;
        var batch = new List<FlashCard>(250);
        foreach (var card in cards)
        {
            if (card.Id == Guid.Empty) card.Id = Guid.NewGuid();

            card.CreatedAt = card.CreatedAt == default ? DateTimeOffset.UtcNow : card.CreatedAt;
            card.NextReviewAt = card.NextReviewAt == default ? LeitnerSchedule.TodayUtc() : card.NextReviewAt;
            card.LastReviewedAt = card.LastReviewedAt?.ToUniversalTime();
            batch.Add(card);
            if (batch.Count < 250) continue;
            imported += await InsertImportBatchAsync(connection, transaction, userId, batch);
            batch.Clear();
        }

        if (batch.Count > 0) imported += await InsertImportBatchAsync(connection, transaction, userId, batch);
        await RebuildDeckSummaryAsync(connection, transaction, userId);

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
                   is_active, plan, features::text, access_code, language_level, reminders_enabled, reminder_hour,
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

    public async Task<List<LearningPackage>> GetPackagesAsync()
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        var packages = await LoadPackagesAsync(connection, null);
        return packages.OrderBy(item => item.SortOrder).ThenBy(item => item.Title).ToList();
    }

    public async Task<List<PackageProgress>> GetPackageProgressAsync(string userId)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_package_id, COUNT(*)::int
            FROM app_cards
            WHERE user_id = @user_id AND source_package_id <> ''
            GROUP BY source_package_id;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        var result = new List<PackageProgress>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new PackageProgress(reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    public async Task<LearningPackage> UpsertPackageAsync(LearningPackage package)
    {
        await EnsureReadyAsync();
        NormalizePackage(package);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var packages = await LoadPackagesAsync(connection, transaction);
        var index = packages.FindIndex(item => item.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) packages[index] = package;
        else packages.Add(package);
        await SavePackagesAsync(connection, transaction, packages);
        await transaction.CommitAsync();
        return package;
    }

    public async Task<bool> DeletePackageAsync(string id)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var packages = await LoadPackagesAsync(connection, transaction);
        var removed = packages.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            await SavePackagesAsync(connection, transaction, packages);
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return removed;
    }

    public async Task<PackageImportResult> ImportPackageCardsAsync(string userId, string planName, string packageId, int count)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var packages = await LoadPackagesAsync(connection, transaction);
        var package = packages.FirstOrDefault(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase) && item.IsPublished);
        if (package is null) return new PackageImportResult(packageId, count, 0, 0, 0, "بسته پیدا نشد.");
        if (!HasPackageAccess(package, planName)) return new PackageImportResult(package.Id, count, 0, 0, count, "پلن شما به این بسته دسترسی ندارد.");

        await EnsureDeckSummaryAsync(connection, transaction, userId);
        var requested = Math.Clamp(count, 1, 100);
        var added = 0;
        var skippedDuplicate = 0;

        foreach (var item in package.Cards)
        {
            if (added >= requested) break;
            if (await PackageCardExistsAsync(connection, transaction, userId, package.Id, item.Id))
            {
                skippedDuplicate++;
                continue;
            }

            var card = item.ToFlashCard(package.Id);
            if (await CardSignatureExistsAsync(connection, transaction, userId, CardSignature(card)))
            {
                skippedDuplicate++;
                continue;
            }

            if (await InsertCardIfNewAsync(connection, transaction, userId, card))
            {
                await ApplyCardSummaryChangeAsync(connection, transaction, userId, null, card);
                added++;
            }
            else
            {
                skippedDuplicate++;
            }
        }

        await transaction.CommitAsync();
        return new PackageImportResult(package.Id, requested, added, skippedDuplicate, 0,
            added > 0 ? $"{added} کارت از بسته اضافه شد." : "کارت جدیدی برای اضافه کردن پیدا نشد.");
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
        var today = ReportingClock.Today(configuration);
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
        var today = ReportingClock.Today(configuration);
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
                active_seconds = app_user_daily_activity.active_seconds +
                    CASE
                        WHEN EXCLUDED.last_seen_at >= app_user_daily_activity.last_seen_at
                         AND EXCLUDED.last_seen_at - app_user_daily_activity.last_seen_at <= interval '5 minutes'
                        THEN FLOOR(EXTRACT(EPOCH FROM (EXCLUDED.last_seen_at - app_user_daily_activity.last_seen_at)))::int
                        ELSE 0
                    END,
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
        var reportToday = ReportingClock.Today(configuration);
        var dueToday = DateOnly.FromDateTime(DateTime.UtcNow);

        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                u.id,
                COALESCE(s.total_cards, 0)::int,
                COALESCE(s.active_cards, 0)::int,
                COALESCE(s.archived_cards, 0)::int,
                COALESCE(card_counts.due_cards, 0)::int,
                COALESCE(a.request_count, 0)::int,
                CASE
                    WHEN a.first_seen_at IS NULL THEN 0
                    WHEN a.request_count > 0 THEN GREATEST(1, CEIL(a.active_seconds / 60.0))::int
                    ELSE 0
                END AS active_minutes,
                COALESCE(a.cards_added, 0)::int,
                COALESCE(a.reviews, 0)::int,
                COALESCE(a.ai_card, 0)::int,
                COALESCE(a.ai_dictionary, 0)::int,
                COALESCE(a.ai_correction, 0)::int
            FROM app_users u
            LEFT JOIN app_user_deck_summaries s ON s.user_id = u.id
            LEFT JOIN LATERAL (
                SELECT COALESCE(SUM(active_count), 0)::int AS due_cards
                FROM app_card_due_buckets b
                WHERE b.user_id = u.id AND b.due_date <= @due_today
            ) card_counts ON true
            LEFT JOIN app_user_daily_activity a
                ON a.user_id = u.id AND a.activity_date = @report_today
            ORDER BY u.last_seen_at DESC;
            """;
        command.Parameters.AddWithValue("report_today", reportToday);
        command.Parameters.AddWithValue("due_today", dueToday);

        var result = new List<AdminUserMetrics>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AdminUserMetrics
            {
                UserId = reader.GetString(0),
                TotalCards = reader.GetInt32(1),
                ActiveCards = reader.GetInt32(2),
                ArchivedCards = reader.GetInt32(3),
                DueCards = reader.GetInt32(4),
                RequestsToday = reader.GetInt32(5),
                ActiveMinutesToday = reader.GetInt32(6),
                CardsAddedToday = reader.GetInt32(7),
                ReviewsToday = reader.GetInt32(8),
                AiCardToday = reader.GetInt32(9),
                AiDictionaryToday = reader.GetInt32(10),
                AiCorrectionToday = reader.GetInt32(11)
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

    public async Task<List<ReminderCandidate>> GetDueReminderCandidatesAsync(DateTimeOffset now, int defaultReminderHour)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT u.id, u.source, u.display_name, u.telegram_id, u.telegram_username, u.telegram_chat_id,
                   u.is_active, u.plan, u.features::text, u.access_code, u.language_level, u.reminders_enabled,
                   u.reminder_hour, u.last_reminder_at, u.created_at, u.last_seen_at,
                   SUM(b.active_count)::int AS due_cards
            FROM app_users u
            JOIN app_card_due_buckets b ON b.user_id = u.id AND b.due_date <= @today
            WHERE u.is_active = true
              AND u.reminders_enabled = true
              AND u.telegram_chat_id IS NOT NULL
              AND COALESCE(u.reminder_hour, @default_hour) = @hour
              AND (u.last_reminder_at IS NULL OR u.last_reminder_at < @today_start)
            GROUP BY u.id, u.source, u.display_name, u.telegram_id, u.telegram_username, u.telegram_chat_id,
                     u.is_active, u.plan, u.features, u.access_code, u.language_level, u.reminders_enabled,
                     u.reminder_hour, u.last_reminder_at, u.created_at, u.last_seen_at;
            """;
        command.Parameters.AddWithValue("today", DateOnly.FromDateTime(now.UtcDateTime));
        command.Parameters.AddWithValue("default_hour", defaultReminderHour);
        command.Parameters.AddWithValue("hour", now.Hour);
        command.Parameters.AddWithValue("today_start", LeitnerSchedule.TodayUtc(now));
        var candidates = new List<ReminderCandidate>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            candidates.Add(new ReminderCandidate(ReadUser(reader), reader.GetInt32(16)));
        }
        return candidates;
    }

    public async Task<BroadcastJob> QueueBroadcastAsync(AdminBroadcastRequest request)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var job = new BroadcastJob { Id = Guid.NewGuid(), Message = request.Message.Trim(), CreatedAt = DateTimeOffset.UtcNow };

        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "INSERT INTO app_broadcast_jobs (id, message, status, created_at) VALUES (@id, @message, 'queued', @created_at);";
            create.Parameters.AddWithValue("id", job.Id);
            create.Parameters.AddWithValue("message", job.Message);
            create.Parameters.AddWithValue("created_at", job.CreatedAt);
            await create.ExecuteNonQueryAsync();
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            var filter = AddBroadcastFilter(count, request, "u");
            count.CommandText = $"SELECT COUNT(*)::int FROM app_users u WHERE {filter};";
            job.Matched = (int)(await count.ExecuteScalarAsync() ?? 0);
        }

        int reachable;
        await using (var recipients = connection.CreateCommand())
        {
            recipients.Transaction = transaction;
            var filter = AddBroadcastFilter(recipients, request, "u");
            recipients.CommandText = $"""
                INSERT INTO app_broadcast_recipients (job_id, user_id, chat_id, status, attempts, next_attempt_at)
                SELECT @job_id, u.id, u.telegram_chat_id, 'pending', 0, now()
                FROM app_users u
                WHERE {filter} AND u.telegram_chat_id IS NOT NULL;
                """;
            recipients.Parameters.AddWithValue("job_id", job.Id);
            reachable = await recipients.ExecuteNonQueryAsync();
        }

        job.Skipped = Math.Max(0, job.Matched - reachable);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE app_broadcast_jobs SET matched = @matched, skipped = @skipped WHERE id = @id;";
            update.Parameters.AddWithValue("matched", job.Matched);
            update.Parameters.AddWithValue("skipped", job.Skipped);
            update.Parameters.AddWithValue("id", job.Id);
            await update.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return job;
    }

    public async Task<List<BroadcastJob>> GetBroadcastJobsAsync(int limit = 20)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, message, status, matched, sent, skipped, failed, created_at, started_at, completed_at
            FROM app_broadcast_jobs
            ORDER BY created_at DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        var jobs = new List<BroadcastJob>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) jobs.Add(ReadBroadcastJob(reader));
        return jobs;
    }

    public async Task<List<BroadcastDelivery>> ClaimBroadcastDeliveriesAsync(int batchSize)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH candidates AS (
                SELECT r.job_id, r.user_id
                FROM app_broadcast_recipients r
                JOIN app_broadcast_jobs j ON j.id = r.job_id
                WHERE j.status IN ('queued', 'running')
                  AND ((r.status = 'pending' AND r.next_attempt_at <= now())
                       OR (r.status = 'sending' AND r.next_attempt_at <= now()))
                ORDER BY j.created_at, r.next_attempt_at
                FOR UPDATE OF r SKIP LOCKED
                LIMIT @limit
            ), claimed AS (
                UPDATE app_broadcast_recipients r
                SET status = 'sending', attempts = attempts + 1, next_attempt_at = now() + interval '5 minutes'
                FROM candidates c
                WHERE r.job_id = c.job_id AND r.user_id = c.user_id
                RETURNING r.job_id, r.user_id, r.chat_id, r.attempts
            )
            SELECT c.job_id, c.user_id, c.chat_id, j.message, c.attempts
            FROM claimed c
            JOIN app_broadcast_jobs j ON j.id = c.job_id;
            """;
        command.Parameters.AddWithValue("limit", Math.Clamp(batchSize, 1, 100));
        var deliveries = new List<BroadcastDelivery>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            deliveries.Add(new BroadcastDelivery(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4)));
        }
        await reader.DisposeAsync();
        if (deliveries.Count > 0)
        {
            await using var start = connection.CreateCommand();
            start.Transaction = transaction;
            start.CommandText = "UPDATE app_broadcast_jobs SET status = 'running', started_at = COALESCE(started_at, now()) WHERE id = ANY(@ids);";
            start.Parameters.AddWithValue("ids", deliveries.Select(item => item.JobId).Distinct().ToArray());
            await start.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return deliveries;
    }

    public async Task CompleteBroadcastDeliveryAsync(BroadcastDelivery delivery, bool sent, string? error)
    {
        await EnsureReadyAsync();
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        string status;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE app_broadcast_recipients
                SET status = CASE
                        WHEN @sent THEN 'sent'
                        WHEN attempts >= 4 THEN 'failed'
                        ELSE 'pending'
                    END,
                    next_attempt_at = CASE
                        WHEN @sent OR attempts >= 4 THEN now()
                        ELSE now() + make_interval(secs => LEAST(300, 10 * attempts * attempts))
                    END,
                    last_error = @error
                WHERE job_id = @job_id AND user_id = @user_id
                RETURNING status;
                """;
            update.Parameters.AddWithValue("sent", sent);
            update.Parameters.AddWithValue("error", error ?? string.Empty);
            update.Parameters.AddWithValue("job_id", delivery.JobId);
            update.Parameters.AddWithValue("user_id", delivery.UserId);
            status = (string?)await update.ExecuteScalarAsync() ?? "pending";
        }

        if (status is "sent" or "failed")
        {
            await using var totals = connection.CreateCommand();
            totals.Transaction = transaction;
            totals.CommandText = """
                UPDATE app_broadcast_jobs
                SET sent = sent + @sent_delta,
                    failed = failed + @failed_delta
                WHERE id = @job_id;
                UPDATE app_broadcast_jobs j
                SET status = 'completed', completed_at = now()
                WHERE j.id = @job_id
                  AND NOT EXISTS (
                      SELECT 1 FROM app_broadcast_recipients r
                      WHERE r.job_id = j.id AND r.status IN ('pending', 'sending')
                  );
                """;
            totals.Parameters.AddWithValue("sent_delta", status == "sent" ? 1 : 0);
            totals.Parameters.AddWithValue("failed_delta", status == "failed" ? 1 : 0);
            totals.Parameters.AddWithValue("job_id", delivery.JobId);
            await totals.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
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
                   is_active, plan, features::text, access_code, language_level, reminders_enabled, reminder_hour,
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
                 features, access_code, language_level, reminders_enabled, reminder_hour, last_reminder_at, created_at, last_seen_at)
            VALUES
                (@id, @source, @display_name, @telegram_id, @telegram_username, @telegram_chat_id, @is_active, @plan,
                 @features, @access_code, @language_level, @reminders_enabled, @reminder_hour, @last_reminder_at, @created_at, @last_seen_at)
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
                language_level = EXCLUDED.language_level,
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
        command.Parameters.AddWithValue("language_level", string.IsNullOrWhiteSpace(user.LanguageLevel) ? "B1" : user.LanguageLevel);
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
                 total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at,
                 is_archived, source_package_id, source_package_card_id, card_signature)
            VALUES
                (@id, @user_id, @front, @back, @example, @prompt, @answer, @notes, @type, @box,
                 @total_reviews, @correct_reviews, @created_at, @next_review_at, @last_reviewed_at,
                 @is_archived, @source_package_id, @source_package_card_id, @card_signature);
            """;
        AddCardParameters(command.Parameters, userId, card);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> InsertCardIfNewAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, FlashCard card)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_cards
                (id, user_id, front, back, example, prompt, answer, notes, type, box,
                 total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at,
                 is_archived, source_package_id, source_package_card_id, card_signature)
            VALUES
                (@id, @user_id, @front, @back, @example, @prompt, @answer, @notes, @type, @box,
                 @total_reviews, @correct_reviews, @created_at, @next_review_at, @last_reviewed_at,
                 @is_archived, @source_package_id, @source_package_card_id, @card_signature)
            ON CONFLICT DO NOTHING;
            """;
        AddCardParameters(command.Parameters, userId, card);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    private static async Task<int> InsertImportBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, IReadOnlyCollection<FlashCard> cards)
    {
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var card in cards)
        {
            var command = new NpgsqlBatchCommand("""
                INSERT INTO app_cards
                    (id, user_id, front, back, example, prompt, answer, notes, type, box,
                     total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at,
                     is_archived, source_package_id, source_package_card_id, card_signature)
                VALUES
                    (@id, @user_id, @front, @back, @example, @prompt, @answer, @notes, @type, @box,
                     @total_reviews, @correct_reviews, @created_at, @next_review_at, @last_reviewed_at,
                     @is_archived, @source_package_id, @source_package_card_id, @card_signature)
                ON CONFLICT DO NOTHING;
                """);
            AddCardParameters(command.Parameters, userId, card);
            batch.BatchCommands.Add(command);
        }
        return await batch.ExecuteNonQueryAsync();
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
                last_reviewed_at = @last_reviewed_at,
                is_archived = @is_archived,
                source_package_id = @source_package_id,
                source_package_card_id = @source_package_card_id,
                card_signature = @card_signature
            WHERE id = @id AND user_id = @user_id;
            """;
        AddCardParameters(command.Parameters, userId, card);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<FlashCard?> FindCardAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, Guid cardId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, front, back, example, prompt, answer, notes, type, box,
                   total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at,
                   is_archived, source_package_id, source_package_card_id
            FROM app_cards
            WHERE user_id = @user_id AND id = @id
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("id", cardId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadCard(reader) : null;
    }

    private static async Task<DeckState> LoadDeckAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string userId, bool lockRows = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id, front, back, example, prompt, answer, notes, type, box,
                   total_reviews, correct_reviews, created_at, next_review_at, last_reviewed_at,
                   is_archived, source_package_id, source_package_card_id
            FROM app_cards
            WHERE user_id = @user_id
            ORDER BY created_at DESC
            {(lockRows ? "FOR UPDATE" : string.Empty)};
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

    private static async Task<List<LearningPackage>> LoadPackagesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value::text FROM app_settings WHERE key = 'packages';";
        var value = await command.ExecuteScalarAsync();
        var packages = value is string json
            ? JsonSerializer.Deserialize<List<LearningPackage>>(json, JsonOptions) ?? []
            : [];

        if (packages.Count == 0)
        {
            packages = LearningPackage.Defaults();
            await SavePackagesAsync(connection, transaction, packages);
        }

        foreach (var package in packages)
        {
            NormalizePackage(package);
        }

        return packages.OrderBy(item => item.SortOrder).ThenBy(item => item.Title).ToList();
    }

    private static async Task SavePackagesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, List<LearningPackage> packages)
    {
        foreach (var package in packages)
        {
            NormalizePackage(package);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_settings (key, value)
            VALUES ('packages', @value)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value;
            """;
        command.Parameters.Add("value", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(packages, JsonOptions);
        await command.ExecuteNonQueryAsync();
    }

    private static void NormalizePackage(LearningPackage package)
    {
        package.Id = string.IsNullOrWhiteSpace(package.Id) ? Slug(package.Title) : Slug(package.Id);
        package.Title = string.IsNullOrWhiteSpace(package.Title) ? package.Id : package.Title.Trim();
        package.Description = package.Description?.Trim() ?? string.Empty;
        package.RequiredPlans = package.RequiredPlans
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private static bool HasPackageAccess(LearningPackage package, string planName) =>
        package.RequiredPlans.Count == 0
        || package.RequiredPlans.Any(plan => plan.Equals(planName, StringComparison.OrdinalIgnoreCase) || NormalizePlanId(plan) == NormalizePlanId(planName));

    private static string CardSignature(FlashCard card) => $"{card.Type}:{NormalizeText(card.Front)}";
    private static string NormalizeText(string value) => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch)).ToArray());
    private static string Slug(string value) => NormalizePlanId(value);

    private static IReadOnlyDictionary<int, int> EmptyBoxes() => new Dictionary<int, int>
    {
        [1] = 0, [2] = 0, [3] = 0, [4] = 0, [5] = 0
    };

    private static FlashCard CloneCard(FlashCard card) => new()
    {
        Id = card.Id, Front = card.Front, Back = card.Back, Example = card.Example, Prompt = card.Prompt,
        Answer = card.Answer, Notes = card.Notes, Type = card.Type, Box = card.Box,
        TotalReviews = card.TotalReviews, CorrectReviews = card.CorrectReviews, CreatedAt = card.CreatedAt,
        NextReviewAt = card.NextReviewAt, LastReviewedAt = card.LastReviewedAt, IsArchived = card.IsArchived,
        SourcePackageId = card.SourcePackageId, SourcePackageCardId = card.SourcePackageCardId
    };

    private static async Task EnsureDeckSummaryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM app_user_deck_summaries WHERE user_id = @user_id);";
            exists.Parameters.AddWithValue("user_id", userId);
            if ((bool)(await exists.ExecuteScalarAsync() ?? false)) return;
        }

        await using var summary = connection.CreateCommand();
        summary.Transaction = transaction;
        summary.CommandText = """
            INSERT INTO app_user_deck_summaries
                (user_id, total_cards, active_cards, archived_cards, due_cards,
                 box_1_count, box_2_count, box_3_count, box_4_count, box_5_count,
                 total_reviews, correct_reviews, last_reviewed_at, updated_at)
            SELECT @user_id,
                   COUNT(*)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived)::int,
                   COUNT(*) FILTER (WHERE is_archived)::int,
                   0,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box = 1)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box = 2)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box = 3)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box = 4)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box >= 5)::int,
                   COALESCE(SUM(total_reviews) FILTER (WHERE NOT is_archived), 0),
                   COALESCE(SUM(correct_reviews) FILTER (WHERE NOT is_archived), 0),
                   MAX(last_reviewed_at) FILTER (WHERE NOT is_archived), now()
            FROM app_cards
            WHERE user_id = @user_id
            ON CONFLICT (user_id) DO NOTHING;
            """;
        summary.Parameters.AddWithValue("user_id", userId);
        if (await summary.ExecuteNonQueryAsync() == 0) return;

        await using var buckets = connection.CreateCommand();
        buckets.Transaction = transaction;
        buckets.CommandText = """
            INSERT INTO app_card_due_buckets (user_id, due_date, active_count)
            SELECT user_id, (next_review_at AT TIME ZONE 'UTC')::date, COUNT(*)::int
            FROM app_cards
            WHERE user_id = @user_id AND is_archived = false
            GROUP BY user_id, (next_review_at AT TIME ZONE 'UTC')::date
            ON CONFLICT (user_id, due_date) DO NOTHING;
            """;
        buckets.Parameters.AddWithValue("user_id", userId);
        await buckets.ExecuteNonQueryAsync();
    }

    private static async Task ResetDeckSummaryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE app_user_deck_summaries
            SET total_cards = 0, active_cards = 0, archived_cards = 0, due_cards = 0,
                box_1_count = 0, box_2_count = 0, box_3_count = 0, box_4_count = 0, box_5_count = 0,
                total_reviews = 0, correct_reviews = 0, last_reviewed_at = NULL, updated_at = now()
            WHERE user_id = @user_id;
            DELETE FROM app_card_due_buckets WHERE user_id = @user_id;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RebuildDeckSummaryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_user_deck_summaries
                (user_id, total_cards, active_cards, archived_cards, due_cards,
                 box_1_count, box_2_count, box_3_count, box_4_count, box_5_count,
                 total_reviews, correct_reviews, last_reviewed_at, updated_at)
            SELECT @user_id,
                   COUNT(*)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived)::int,
                   COUNT(*) FILTER (WHERE is_archived)::int,
                   0,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box = 1)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box = 2)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box = 3)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box = 4)::int,
                   COUNT(*) FILTER (WHERE NOT is_archived AND box >= 5)::int,
                   COALESCE(SUM(total_reviews) FILTER (WHERE NOT is_archived), 0),
                   COALESCE(SUM(correct_reviews) FILTER (WHERE NOT is_archived), 0),
                   MAX(last_reviewed_at) FILTER (WHERE NOT is_archived), now()
            FROM app_cards WHERE user_id = @user_id
            ON CONFLICT (user_id) DO UPDATE SET
                total_cards = EXCLUDED.total_cards,
                active_cards = EXCLUDED.active_cards,
                archived_cards = EXCLUDED.archived_cards,
                due_cards = EXCLUDED.due_cards,
                box_1_count = EXCLUDED.box_1_count,
                box_2_count = EXCLUDED.box_2_count,
                box_3_count = EXCLUDED.box_3_count,
                box_4_count = EXCLUDED.box_4_count,
                box_5_count = EXCLUDED.box_5_count,
                total_reviews = EXCLUDED.total_reviews,
                correct_reviews = EXCLUDED.correct_reviews,
                last_reviewed_at = EXCLUDED.last_reviewed_at,
                updated_at = now();
            DELETE FROM app_card_due_buckets WHERE user_id = @user_id;
            INSERT INTO app_card_due_buckets (user_id, due_date, active_count)
            SELECT user_id, (next_review_at AT TIME ZONE 'UTC')::date, COUNT(*)::int
            FROM app_cards
            WHERE user_id = @user_id AND is_archived = false
            GROUP BY user_id, (next_review_at AT TIME ZONE 'UTC')::date;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ApplyCardSummaryChangeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, FlashCard? before, FlashCard? after)
    {
        await using var summary = connection.CreateCommand();
        summary.Transaction = transaction;
        summary.CommandText = """
            UPDATE app_user_deck_summaries
            SET total_cards = total_cards + @total_delta,
                active_cards = active_cards + @active_delta,
                archived_cards = archived_cards + @archived_delta,
                box_1_count = box_1_count + @box_1_delta,
                box_2_count = box_2_count + @box_2_delta,
                box_3_count = box_3_count + @box_3_delta,
                box_4_count = box_4_count + @box_4_delta,
                box_5_count = box_5_count + @box_5_delta,
                total_reviews = total_reviews + @total_reviews_delta,
                correct_reviews = correct_reviews + @correct_reviews_delta,
                last_reviewed_at = CASE
                    WHEN @last_reviewed_at IS NULL THEN last_reviewed_at
                    WHEN last_reviewed_at IS NULL THEN @last_reviewed_at
                    ELSE GREATEST(last_reviewed_at, @last_reviewed_at)
                END,
                updated_at = now()
            WHERE user_id = @user_id;
            """;
        summary.Parameters.AddWithValue("user_id", userId);
        summary.Parameters.AddWithValue("total_delta", (after is null ? 0 : 1) - (before is null ? 0 : 1));
        summary.Parameters.AddWithValue("active_delta", Active(after) - Active(before));
        summary.Parameters.AddWithValue("archived_delta", Archived(after) - Archived(before));
        for (var box = 1; box <= 5; box++) summary.Parameters.AddWithValue($"box_{box}_delta", BoxDelta(before, after, box));
        summary.Parameters.AddWithValue("total_reviews_delta", Active(after) * (after?.TotalReviews ?? 0) - Active(before) * (before?.TotalReviews ?? 0));
        summary.Parameters.AddWithValue("correct_reviews_delta", Active(after) * (after?.CorrectReviews ?? 0) - Active(before) * (before?.CorrectReviews ?? 0));
        summary.Parameters.Add("last_reviewed_at", NpgsqlDbType.TimestampTz).Value = (object?)after?.LastReviewedAt ?? DBNull.Value;
        await summary.ExecuteNonQueryAsync();

        if (Active(before) == 1) await AdjustDueBucketAsync(connection, transaction, userId, before!.NextReviewAt, -1);
        if (Active(after) == 1) await AdjustDueBucketAsync(connection, transaction, userId, after!.NextReviewAt, 1);
    }

    private static async Task AdjustDueBucketAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, DateTimeOffset dueAt, int delta)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_card_due_buckets (user_id, due_date, active_count)
            VALUES (@user_id, @due_date, @delta)
            ON CONFLICT (user_id, due_date) DO UPDATE
            SET active_count = app_card_due_buckets.active_count + EXCLUDED.active_count;
            DELETE FROM app_card_due_buckets WHERE user_id = @user_id AND active_count <= 0;
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("due_date", DateOnly.FromDateTime(dueAt.UtcDateTime));
        command.Parameters.AddWithValue("delta", delta);
        await command.ExecuteNonQueryAsync();
    }

    private static int Active(FlashCard? card) => card is null || card.IsArchived ? 0 : 1;
    private static int Archived(FlashCard? card) => card is { IsArchived: true } ? 1 : 0;
    private static int BoxDelta(FlashCard? before, FlashCard? after, int box) =>
        (Active(after) == 1 && Math.Clamp(after!.Box, 1, 5) == box ? 1 : 0)
        - (Active(before) == 1 && Math.Clamp(before!.Box, 1, 5) == box ? 1 : 0);

    private static async Task<bool> PackageCardExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, string packageId, string packageCardId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM app_cards WHERE user_id = @user_id AND source_package_id = @package_id AND source_package_card_id = @card_id);";
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("package_id", packageId);
        command.Parameters.AddWithValue("card_id", packageCardId);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<bool> CardSignatureExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, string signature)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM app_cards
                WHERE user_id = @user_id
                  AND (card_signature = @signature
                       OR (card_signature = '' AND (type || ':' || lower(regexp_replace(front, '\\s+', '', 'g'))) = @signature))
            );
            """;
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("signature", signature);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static string AddBroadcastFilter(NpgsqlCommand command, AdminBroadcastRequest request, string alias)
    {
        if (request.Audience.Equals("selected", StringComparison.OrdinalIgnoreCase))
        {
            var ids = request.UserIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray() ?? [];
            command.Parameters.AddWithValue("user_ids", ids);
            return $"{alias}.id = ANY(@user_ids)";
        }
        if (request.Audience.Equals("all", StringComparison.OrdinalIgnoreCase)) return "true";

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Plan))
        {
            command.Parameters.AddWithValue("plan", request.Plan.Trim());
            conditions.Add($"{alias}.plan = @plan");
        }
        if (request.IsActive.HasValue)
        {
            command.Parameters.AddWithValue("is_active", request.IsActive.Value);
            conditions.Add($"{alias}.is_active = @is_active");
        }
        if (!string.IsNullOrWhiteSpace(request.Source))
        {
            command.Parameters.AddWithValue("source", request.Source.Trim());
            conditions.Add($"{alias}.source = @source");
        }
        if (!string.IsNullOrWhiteSpace(request.AccessCode))
        {
            command.Parameters.AddWithValue("access_code", request.AccessCode.Trim());
            conditions.Add($"{alias}.access_code = @access_code");
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            command.Parameters.AddWithValue("search", $"%{request.Search.Trim()}%");
            conditions.Add($"({alias}.id ILIKE @search OR {alias}.display_name ILIKE @search OR {alias}.telegram_id ILIKE @search OR {alias}.telegram_username ILIKE @search)");
        }
        return conditions.Count == 0 ? "true" : string.Join(" AND ", conditions);
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
        LanguageLevel = reader.GetString(10),
        RemindersEnabled = reader.GetBoolean(11),
        ReminderHour = reader.IsDBNull(12) ? null : reader.GetInt32(12),
        LastReminderAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(14),
        LastSeenAt = reader.GetFieldValue<DateTimeOffset>(15)
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
        LastReviewedAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        IsArchived = reader.GetBoolean(14),
        SourcePackageId = reader.GetString(15),
        SourcePackageCardId = reader.GetString(16)
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

    private static BroadcastJob ReadBroadcastJob(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Message = reader.GetString(1),
        Status = reader.GetString(2),
        Matched = reader.GetInt32(3),
        Sent = reader.GetInt32(4),
        Skipped = reader.GetInt32(5),
        Failed = reader.GetInt32(6),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
        StartedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        CompletedAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)
    };

    private static void AddCardParameters(NpgsqlParameterCollection parameters, string userId, FlashCard card)
    {
        parameters.AddWithValue("id", card.Id);
        parameters.AddWithValue("user_id", userId);
        parameters.AddWithValue("front", card.Front);
        parameters.AddWithValue("back", card.Back);
        parameters.AddWithValue("example", card.Example);
        parameters.AddWithValue("prompt", card.Prompt);
        parameters.AddWithValue("answer", card.Answer);
        parameters.AddWithValue("notes", card.Notes);
        parameters.AddWithValue("type", card.Type.ToString());
        parameters.AddWithValue("box", card.Box);
        parameters.AddWithValue("total_reviews", card.TotalReviews);
        parameters.AddWithValue("correct_reviews", card.CorrectReviews);
        parameters.AddWithValue("created_at", card.CreatedAt);
        parameters.AddWithValue("next_review_at", card.NextReviewAt);
        parameters.Add("last_reviewed_at", NpgsqlDbType.TimestampTz).Value = (object?)card.LastReviewedAt ?? DBNull.Value;
        parameters.AddWithValue("is_archived", card.IsArchived);
        parameters.AddWithValue("source_package_id", card.SourcePackageId ?? string.Empty);
        parameters.AddWithValue("source_package_card_id", card.SourcePackageCardId ?? string.Empty);
        parameters.AddWithValue("card_signature", CardSignature(card));
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

    public static string GetConnectionString(IConfiguration config)
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
