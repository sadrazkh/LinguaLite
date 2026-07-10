-- Adds accurate active-time accumulation for per-user admin reports.
-- Run once on an existing PostgreSQL database before or during deployment.

ALTER TABLE app_user_daily_activity
    ADD COLUMN IF NOT EXISTS active_seconds integer NOT NULL DEFAULT 0;
