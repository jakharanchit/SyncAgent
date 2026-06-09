# Changelog

All notable changes to OpCore SyncAgent are documented here.

## [1.1.0] — 2026-06-09

### Changed
- **Schema version check replaced with actual schema check.** SyncAgent no longer reads a `schema_version` table. Instead it runs `PRAGMA table_info(sync_status)` and verifies that every column it depends on is present. Missing table → "sync_status table not found". Missing columns → lists exactly which ones. This catches partial migrations and eliminates the possibility of a wrong version number. The `schema_version` table is no longer created by `sqlite-syncagent.sql`; existing databases that have it are unaffected.
- **`BackoffSeconds` removed from configuration.** Retry wait times are now computed internally using exponential backoff (30 s → 60 s → 2 min → 4 min → … capped at 1 hour, ±10% jitter). This simplifies `syncagent.json` for client deployments — there are no retry timing knobs to misconfigure. Behaviour is equivalent to the previous defaults.
- **`syncagent.json` is now clean JSON** (no `//` comments). Comments have moved to `syncagent.example.json`, which remains the annotated reference. This ensures the operational config file is parseable by any standard JSON tool.
- **Single-file publish.** The deliverable is now a single `SyncAgent.exe` containing the .NET runtime, rather than a folder of ~200 DLLs. Size is unchanged; the package is significantly easier to navigate.
- Duplicate `ExpectedSchemaVersion` constant removed from `SQLiteReader`.

---

## [1.0.1] — 2026-06-08

### Fixed
- **SQLite WAL mode now enforced on startup.** `SQLiteReader.EnsureWalModeAsync` is called in `VerifyStartupAsync` and issues `PRAGMA journal_mode=WAL`. This prevents `SQLITE_BUSY` (database is locked) errors when the client application and SyncAgent write to the same database file concurrently. Previously, WAL mode was only set by `sql/sqlite-syncagent.sql`; existing databases that were initialised without WAL were never automatically migrated.

### Changed
- `sql/sqlite-syncagent.sql` now includes `PRAGMA journal_mode=WAL` so newly initialised databases get WAL mode immediately from the setup script as well.
- Startup log now confirms WAL mode is active at `Debug` level (`SQLite journal mode: WAL`). A `Warning` is emitted if WAL mode cannot be set (e.g. database on a network share that does not support WAL).

---

## [1.0.0] — 2026-06-05

### Added
- Initial release.
- Generic SQLite-to-PostgreSQL sync for any table configuration defined in `syncagent.json`. No code change or redeployment needed to add, remove, or rename tables.
- Configurable batch size (default 100 records per cycle) and poll interval (default 30 s).
- 10-step exponential backoff with ±10% jitter: 30 s → 60 s → 2 min → 5 min → 15 min → 1 hr (×5).
- Dead-letter state (`synced=2`) after MaxRetries exhausted — record frozen until manually reset.
- Idempotent writes via `ON CONFLICT (primary_key) DO NOTHING` on every configured table.
- Atomic health file (`sync-health.json`) written via `.tmp` → rename after every cycle.
- Schema version check on startup — refuses to run if SQLite schema does not match expected version.
- Windows Service support via `UseWindowsService()` (auto-starts at boot, recoverable).
- Linux systemd support via `UseSystemd()` (same binary).
- Rolling log files via Serilog (daily rotation, 30-day retention).
- `station_id` injected into every PostgreSQL row by SyncAgent (not present in SQLite source).
- `StationId` and `SiteName` configurable per station via `syncagent.json` (template) and `syncagent.local.json` (gitignored local overrides).
