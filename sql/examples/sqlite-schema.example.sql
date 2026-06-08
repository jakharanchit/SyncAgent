-- SyncAgent — Example SQLite Application Schema
-- This is an example for an aerospace test station application.
-- Replace these tables with your own application tables.
--
-- IMPORTANT: This file is for reference only.
-- The tables below are application-specific and NOT required by SyncAgent.
-- SyncAgent only requires the tables in sql/sqlite-syncagent.sql.
--
-- After creating your tables, run sql/sqlite-syncagent.sql to add
-- the sync_status and schema_version infrastructure tables.
--
-- Usage pattern: after every INSERT into a business table, also insert
-- into sync_status so SyncAgent knows to push that record to PostgreSQL.

-- ── Example: Test sessions ────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS sessions (
    session_id   TEXT    NOT NULL PRIMARY KEY,
    operator_id  TEXT    NOT NULL,
    work_order   TEXT,
    started_at   TEXT    NOT NULL,
    closed_at    TEXT,
    status       TEXT    NOT NULL
);

-- ── Example: Individual test runs ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS tests (
    test_id      TEXT    NOT NULL PRIMARY KEY,
    session_id   TEXT    NOT NULL,
    part_serial  TEXT    NOT NULL,
    test_name    TEXT    NOT NULL,
    started_at   TEXT    NOT NULL,
    completed_at TEXT,
    verdict      TEXT
);

-- ── Example: Per-channel measurements ─────────────────────────────────────────
CREATE TABLE IF NOT EXISTS measurements (
    measurement_id TEXT    NOT NULL PRIMARY KEY,
    test_id        TEXT    NOT NULL,
    channel_name   TEXT    NOT NULL,
    value          REAL    NOT NULL,
    unit           TEXT,
    lower_limit    REAL,
    upper_limit    REAL,
    in_limit       INTEGER NOT NULL,
    captured_at    TEXT    NOT NULL DEFAULT (datetime('now'))
);

-- ── Example: Audit trail ──────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS audit_log (
    audit_id    TEXT    NOT NULL PRIMARY KEY,
    session_id  TEXT,
    operator_id TEXT,
    action      TEXT    NOT NULL,
    detail      TEXT,
    signature   TEXT,
    logged_at   TEXT    NOT NULL
);

-- ── Example: Queue records for sync ──────────────────────────────────────────
-- After inserting a session, queue it:
--   INSERT OR IGNORE INTO sync_status (record_id, table_name) VALUES (session_id, 'sessions');
-- After inserting a measurement, queue it:
--   INSERT OR IGNORE INTO sync_status (record_id, table_name) VALUES (measurement_id, 'measurements');
