-- Run once with psql before deploying the performance/scalability release.
-- CREATE INDEX CONCURRENTLY cannot run inside a transaction block.

ALTER TABLE app_cards ADD COLUMN IF NOT EXISTS card_signature text NOT NULL DEFAULT '';

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

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_app_cards_due_active
    ON app_cards(user_id, next_review_at, box, id) WHERE is_archived = false;
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_app_cards_active_cursor
    ON app_cards(user_id, created_at DESC, id DESC) WHERE is_archived = false;
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_app_cards_archived_cursor
    ON app_cards(user_id, created_at DESC, id DESC) WHERE is_archived = true;
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_app_cards_signature
    ON app_cards(user_id, card_signature);
CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_app_cards_package_source
    ON app_cards(user_id, source_package_id, source_package_card_id)
    WHERE source_package_id <> '' AND source_package_card_id <> '';
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_card_due_buckets_due_date
    ON app_card_due_buckets(due_date, user_id);
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_broadcast_recipients_pending
    ON app_broadcast_recipients(status, next_attempt_at, job_id);

-- Backfill existing users without materializing card rows in the application.
INSERT INTO app_user_deck_summaries
    (user_id, total_cards, active_cards, archived_cards, due_cards,
     box_1_count, box_2_count, box_3_count, box_4_count, box_5_count,
     total_reviews, correct_reviews, last_reviewed_at, updated_at)
SELECT user_id,
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
       MAX(last_reviewed_at) FILTER (WHERE NOT is_archived),
       now()
FROM app_cards
GROUP BY user_id
ON CONFLICT (user_id) DO NOTHING;

INSERT INTO app_card_due_buckets (user_id, due_date, active_count)
SELECT user_id, (next_review_at AT TIME ZONE 'UTC')::date, COUNT(*)::int
FROM app_cards
WHERE is_archived = false
GROUP BY user_id, (next_review_at AT TIME ZONE 'UTC')::date
ON CONFLICT (user_id, due_date) DO NOTHING;
