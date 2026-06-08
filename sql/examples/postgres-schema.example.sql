-- SyncAgent — Example PostgreSQL Central Schema
-- This is an example for an aerospace test station application.
-- Replace these tables with your own application tables.
--
-- IMPORTANT: This file is for reference only.
-- SyncAgent does NOT require any specific tables in PostgreSQL.
-- You define your central schema; SyncAgent inserts into whatever
-- tables you configure in the Tables array of syncagent.json.
--
-- The synced_at column (DEFAULT NOW()) is a useful pattern — it records
-- when SyncAgent pushed the row, separate from application timestamps.

CREATE SCHEMA IF NOT EXISTS events;
CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS core;

-- ── Example: Test sessions ────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS events.sessions (
    session_id      TEXT        PRIMARY KEY,
    station_id      TEXT        NOT NULL,
    operator_id     TEXT        NOT NULL,
    work_order      TEXT,
    started_at      TIMESTAMPTZ NOT NULL,
    closed_at       TIMESTAMPTZ,
    status          TEXT        NOT NULL,
    synced_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── Example: Individual test runs ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS events.tests (
    test_id         TEXT        PRIMARY KEY,
    session_id      TEXT        NOT NULL REFERENCES events.sessions(session_id),
    station_id      TEXT        NOT NULL,
    part_serial     TEXT        NOT NULL,
    test_name       TEXT        NOT NULL,
    started_at      TIMESTAMPTZ NOT NULL,
    completed_at    TIMESTAMPTZ,
    verdict         TEXT,
    synced_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── Example: Per-channel measurements ─────────────────────────────────────────
CREATE TABLE IF NOT EXISTS events.measurements (
    measurement_id  TEXT             PRIMARY KEY,
    test_id         TEXT             NOT NULL REFERENCES events.tests(test_id),
    station_id      TEXT             NOT NULL,
    channel_name    TEXT             NOT NULL,
    value           DOUBLE PRECISION NOT NULL,
    unit            TEXT,
    lower_limit     DOUBLE PRECISION,
    upper_limit     DOUBLE PRECISION,
    in_limit        BOOLEAN          NOT NULL,
    captured_at     TIMESTAMPTZ      NOT NULL,
    synced_at       TIMESTAMPTZ      NOT NULL DEFAULT NOW()
);

-- ── Example: Audit trail ──────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS audit.audit_log (
    audit_id        TEXT        PRIMARY KEY,
    session_id      TEXT,
    station_id      TEXT        NOT NULL,
    operator_id     TEXT,
    action          TEXT        NOT NULL,
    detail          TEXT,
    signature       TEXT,
    logged_at       TIMESTAMPTZ NOT NULL,
    synced_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── Example: Per-station sync counters (monitoring) ───────────────────────────
CREATE TABLE IF NOT EXISTS core.station_sync_state (
    station_id          TEXT        PRIMARY KEY,
    last_synced_at      TIMESTAMPTZ,
    total_synced_count  BIGINT      NOT NULL DEFAULT 0
);

-- ── Example: Recommended indexes ──────────────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_sessions_station  ON events.sessions     (station_id);
CREATE INDEX IF NOT EXISTS idx_tests_session     ON events.tests        (session_id);
CREATE INDEX IF NOT EXISTS idx_tests_station     ON events.tests        (station_id);
CREATE INDEX IF NOT EXISTS idx_meas_test         ON events.measurements (test_id);
CREATE INDEX IF NOT EXISTS idx_meas_station      ON events.measurements (station_id);
CREATE INDEX IF NOT EXISTS idx_audit_station     ON audit.audit_log     (station_id);
CREATE INDEX IF NOT EXISTS idx_audit_session     ON audit.audit_log     (session_id);
