# Changelog

All notable changes to OpCore SyncAgent are documented here.

## [1.2.0] — 2026-06-10

### Added

- **Upsert support.** New `ConflictStrategy` field on each table mapping. `"nothing"` (default) keeps the existing `ON CONFLICT DO NOTHING` behaviour. `"update"` issues `ON CONFLICT ({pk}) DO UPDATE SET …` to overwrite existing rows with the latest values from SQLite. Useful for tables where records can be corrected after initial creation.

- **Column exclusion and remapping.** `ExcludeColumns` (array) drops columns from the PostgreSQL INSERT — useful for local-only SQLite columns with no counterpart in the central schema. `ColumnMap` (object) renames columns on the way out: `{"sqlite_name": "pg_name"}`. Both are applied per table-mapping entry; no code change or rebuild needed.

- **Composite primary key support.** `PrimaryKeys` (string array) replaces `PrimaryKey` for tables with multi-column PKs. `PrimaryKeySeparator` (default `|`) is used when concatenating PK values into `sync_status.record_id`. `SQLiteReader` uses SQLite's row-value constructor (`WHERE (pk1, pk2) IN (…)`) for batch hydration. `PostgresWriter` lists all PK columns in the `ON CONFLICT` clause. The existing single-column `PrimaryKey` field remains fully supported.

- **Delete propagation.** `SyncDeletes: true` on a table mapping causes SyncAgent to mirror DELETE operations from SQLite to PostgreSQL. Requires a delete-log table and a SQLite AFTER DELETE trigger (schema and trigger DDL documented in `syncagent.example.json`). `DeleteLogTable` defaults to `{SourceTable}_deletes`. Deletes are processed in a separate pass after inserts each cycle.

- **PostgreSQL schema validation at startup.** `SyncOrchestrator.VerifyStartupAsync` queries `information_schema.columns` for every configured target table. Missing tables or missing columns are logged as `Warning` lines at startup so misconfigured deployments are caught immediately without waiting for the first sync failure.

- **HTTP health endpoint.** `Sync.HealthEndpointPort` (default `0` = disabled) starts a `System.Net.HttpListener` on `http://localhost:{port}/health/` serving the same JSON written to `sync-health.json`. Suitable for Prometheus, Datadog, and load-balancer health checks. No ASP.NET or additional packages required.

- **Throughput metrics.** `syncedTotal` (cumulative records synced since service start) and `lastCycleDurationMs` (wall-clock duration of the most recent cycle) are now written to `sync-health.json` after every cycle.

- **Secret warning.** SyncAgent logs a `Warning` at startup if `Postgres.ConnectionString` contains a plaintext password (`Password=`) and the `SYNCAGENT_Postgres__ConnectionString` environment variable is not set. This encourages moving credentials to an env var or secrets manager without blocking operation.

- **Admin CLI flags.** All commands run once and exit; no background service is started.
  - `--version` — print the assembled version and exit.
  - `--status` — print a per-table table of pending and dead-letter record counts.
  - `--reset-dead-letters` — reset all `synced=2` records to `synced=0, retry_count=0` so they will be retried on the next cycle. Accepts an optional `--table=<name>` argument to scope the reset to one table.
  - `--dry-run` — run one complete sync cycle logging what would be synced and deleted, but write nothing to PostgreSQL. Exits cleanly after the single cycle via `IHostApplicationLifetime.StopApplication()`.

### Changed

- **Infrastructure failures no longer count toward `MaxRetries`.** When PostgreSQL is unreachable (network timeout, connection refused, `NpgsqlException` with `IsTransient=true`, or SQL state code in the `08xxx`/`57xxx`/`53xxx`/`40xxx` families), affected records are deferred without incrementing `retry_count`. Only failures where PostgreSQL received and rejected the record (type errors, constraint violations) consume a retry. This prevents offline stations from dead-lettering valid records after `MaxRetries × IntervalSeconds` seconds of downtime.

- **Per-record fallback on batch data failure.** When a batch INSERT fails with a data error, SyncAgent retries each record individually to isolate the bad row. Only the row that individually fails has its `retry_count` incremented; all other records in the batch are synced normally in the same cycle.

- **`postgresReachable` now accurately reflects infrastructure state.** The flag is derived from the `FailureKind` of the most recent write attempt rather than a separate probe. Empty-pending cycles skip the PostgreSQL connection entirely unless the previous cycle had infrastructure failures.

- **Health file expanded.** `sync-health.json` now includes `infraDeferredCount`, `lastInfraErrorAt`, `syncedTotal`, `lastCycleDurationMs`, and a `tables[]` array with per-table `{name, pending, deadLetter}` counts.

- **Per-table pending/dead-letter stats** are collected each cycle and surfaced in both the health file and cycle log lines.

- **`Logging.RetentionDays`** config field (default `30`) controls how many daily log files Serilog retains. Previously hard-coded to 30.

- **`Sync.CommandTimeoutSeconds`** config field (default `30`) sets the `CommandTimeout` on every `NpgsqlCommand`. Prevents slow or hung queries from blocking the sync loop indefinitely.

- **`Sync.PruneAfterDays`** config field (default `0` = disabled) deletes `synced=1` rows from `sync_status` older than the configured number of days. Runs at the start of each cycle. Recommended for 24/7 stations to keep `sync_status` from growing unboundedly.

---

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
