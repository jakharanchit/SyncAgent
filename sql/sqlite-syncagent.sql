-- SyncAgent — SQLite Infrastructure
-- Run this once on every SQLite database that SyncAgent will monitor.
-- This file contains ONLY the tables SyncAgent itself requires.
-- Your application's own tables are separate — define them however you like.

-- ── Journal mode ──────────────────────────────────────────────────────────────
-- WAL mode allows SyncAgent and your application to write to the database
-- concurrently without blocking each other. Set once; persists in the DB file.
PRAGMA journal_mode=WAL;

-- ── SyncAgent queue ───────────────────────────────────────────────────────────
-- Your application inserts one row here for every record it wants synced.
-- SyncAgent reads this table each cycle to know what to push to PostgreSQL.
--
-- Insert pattern (run after every INSERT into your business table):
--
--   INSERT OR IGNORE INTO sync_status (record_id, table_name)
--   VALUES ('<your_primary_key_value>', '<your_table_name>');
--
-- States:
--   synced = 0  → pending     (default — SyncAgent will push this)
--   synced = 1  → synced      (SyncAgent confirmed receipt in PostgreSQL)
--   synced = 2  → dead-letter (all retries exhausted — needs manual reset)

CREATE TABLE IF NOT EXISTS sync_status (
    record_id       TEXT    NOT NULL,
    table_name      TEXT    NOT NULL,
    synced          INTEGER NOT NULL DEFAULT 0,
    retry_count     INTEGER NOT NULL DEFAULT 0,
    last_attempt    TEXT,
    next_attempt    TEXT,
    failure_reason  TEXT,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (record_id, table_name)
);

CREATE INDEX IF NOT EXISTS idx_sync_pending
    ON sync_status (synced, next_attempt)
    WHERE synced = 0;
