# SyncAgent

A .NET 8 Windows Service (also runs on Linux via systemd) that syncs a local SQLite database to a central PostgreSQL server. Designed for offline-first deployments where edge nodes must operate without continuous network connectivity.

SyncAgent is **schema-agnostic**. It does not know or care what tables your application uses. You provide the table mappings in `syncagent.json`; SyncAgent reads from SQLite and writes to PostgreSQL accordingly. No code changes are needed when you add tables, change column names, or deploy to a new project.

## How It Works

```
Edge Node
    │
    │  Your application writes to its SQLite tables
    │  and queues records in sync_status
    ▼
local.db (SQLite)  ←──  SyncAgent polls sync_status
                              │
                              │  pushes batches over network
                              ▼
                        PostgreSQL (central server)

SyncAgent also writes:
sync-health.json  ←────  your app reads to display sync status
```

## The Contract

SyncAgent has exactly one requirement of the client's SQLite database:

| Requirement | Description |
|---|---|
| `sync_status` table | Your application inserts one row here per record to be synced |

At startup SyncAgent verifies that `sync_status` exists and has the columns it needs. If any column is missing it refuses to start and logs exactly which columns are missing. Run `sql/sqlite-syncagent.sql` to create the table.

Your application's own tables are **completely up to you**. SyncAgent reads them generically using `SELECT *` filtered by the primary key.

Your PostgreSQL schema is also **completely up to you**. SyncAgent inserts into whatever tables you configure. There are no required tables on the PostgreSQL side.

## Quick Start

### 1. Add SyncAgent infrastructure to your SQLite database

```bash
sqlite3 "path/to/your.db" ".read sql/sqlite-syncagent.sql"
```

This adds the `sync_status` table and sets WAL journal mode. Your application tables are separate — create them however you normally would.

SyncAgent also enforces WAL mode automatically on startup, so existing databases are migrated without any manual step.

### 2. Queue records for sync

After every INSERT into a business table, also insert into `sync_status`:

```sql
INSERT OR IGNORE INTO sync_status (record_id, table_name)
VALUES ('<primary_key_value>', '<table_name>');
```

For composite primary keys, concatenate the values using the `PrimaryKeySeparator` (default `|`):

```sql
INSERT OR IGNORE INTO sync_status (record_id, table_name)
VALUES ('device_01|42', 'sensor_readings');
```

> **Primary key ordering:** SyncAgent sends each batch to PostgreSQL in `record_id ASC` order inside a single transaction. If your PostgreSQL schema uses foreign-key constraints across tables (e.g. `tests` references `sessions`), your primary-key values must sort in dependency order so parent records are inserted before their children. Time-ordered keys such as ULIDs or UUID v7 satisfy this naturally.

### 3. Create the matching PostgreSQL tables

Design your central schema however you like. A useful pattern is to add a `synced_at TIMESTAMPTZ NOT NULL DEFAULT NOW()` column so you know when SyncAgent delivered each row.

### 4. Configure syncagent.json

```json
{
  "Station":  { "StationId": "NODE-01", "SiteName": "Site A" },
  "Postgres": { "ConnectionString": "Host=<host>;Database=<db>;Username=<user>;Password=<pw>;SslMode=Require" },
  "Sync":     { "SQLitePath": "C:\\Data\\your.db" },
  "Tables": [
    {
      "SourceTable":      "orders",
      "TargetTable":      "public.orders",
      "PrimaryKey":       "order_id",
      "InjectStationId":  false,
      "TimestampColumns": ["created_at"],
      "BooleanColumns":   ["is_paid"],
      "ConflictStrategy": "nothing"
    }
  ]
}
```

### 5. Run

```powershell
dotnet run --configuration Release
```

### 6. Install as Windows Service (production)

```powershell
dotnet publish --configuration Release --runtime win-x64 --self-contained true -o .\publish\
Copy-Item syncagent.json                  .\publish\
Copy-Item scripts\install-service.ps1     .\publish\
Copy-Item scripts\uninstall-service.ps1   .\publish\
# Then on the target machine (as Administrator):
cd .\publish\
.\install-service.ps1
```

## Configuration Reference

All configuration lives in **`syncagent.json`**. The annotated reference with every option is in **`syncagent.example.json`**.

### Core settings

| Key | Default | Description |
|---|---|---|
| `Station.StationId` | `""` | Identifier injected as `station_id` when `InjectStationId=true` |
| `Station.SiteName` | `""` | Human-readable name shown in logs |
| `Postgres.ConnectionString` | *(required)* | Npgsql connection string. Use `SslMode=Require` or `SslMode=VerifyFull` in production. Prefer the `SYNCAGENT_Postgres__ConnectionString` env var over a plaintext password in the file — SyncAgent warns at startup if it detects a plaintext password with no env var override |
| `Sync.SQLitePath` | `./local.db` | Path to the SQLite database |
| `Sync.IntervalSeconds` | `30` | Poll interval |
| `Sync.BatchSize` | `100` | Max records pushed per cycle |
| `Sync.MaxRetries` | `10` | Attempts before a record is dead-lettered (infrastructure failures do **not** count toward this limit) |
| `Sync.HealthFilePath` | `./sync-health.json` | Written after every cycle for external monitoring |
| `Sync.CommandTimeoutSeconds` | `30` | Per-command PostgreSQL timeout; prevents a hung query from blocking the sync loop |
| `Sync.PruneAfterDays` | `0` | Delete synced rows older than N days. `0` disables pruning |
| `Sync.HealthEndpointPort` | `0` | Exposes `GET http://localhost:{port}/health/` returning the health JSON. `0` disables the endpoint |
| `Logging.LogPath` | `./logs` | Rolling log directory |
| `Logging.MinLevel` | `Information` | `Debug` / `Information` / `Warning` / `Error` |
| `Logging.RetentionDays` | `30` | Number of daily log files to keep |
| `Alerts.WebhookUrl` | `""` | HTTP endpoint for dead-letter POST alerts. Works with Slack, Teams, PagerDuty, or any JSON webhook |
| `Tables` | `[]` | Table mappings — see below |

> **Retry backoff** is computed automatically: 30 s → 60 s → 2 min → 4 min → … capped at 1 hour, ±10% jitter. There is no config knob for this.

### Table mapping fields

| Field | Required | Description |
|---|---|---|
| `SourceTable` | Yes | Table name in SQLite |
| `TargetTable` | Yes | PostgreSQL destination as `schema.table` or `table` |
| `PrimaryKey` | Yes* | Single PK column used for `ON CONFLICT` and `sync_status.record_id` |
| `PrimaryKeys` | Yes* | Array of columns for composite PKs: `["device_id","seq_no"]`. Overrides `PrimaryKey` when set |
| `PrimaryKeySeparator` | No (default: `\|`) | Separator used when concatenating composite PK values into `record_id` |
| `InjectStationId` | No (default: `true`) | Adds `Station.StationId` as `station_id` on every INSERT |
| `TimestampColumns` | No (default: `[]`) | TEXT columns in SQLite → `TIMESTAMPTZ` in PostgreSQL |
| `BooleanColumns` | No (default: `[]`) | `0`/`1` INTEGER columns in SQLite → `BOOLEAN` in PostgreSQL |
| `ExcludeColumns` | No (default: `[]`) | SQLite columns to omit from the PostgreSQL INSERT (e.g. local-only blobs with no PG counterpart) |
| `ColumnMap` | No (default: `{}`) | Rename columns on the way out: `{"sqlite_name": "pg_name"}` |
| `ConflictStrategy` | No (default: `"nothing"`) | `"nothing"` — silently ignore duplicate PKs. `"update"` — `ON CONFLICT DO UPDATE SET` all non-PK columns (upsert) |
| `SyncDeletes` | No (default: `false`) | When `true`, also propagates DELETE operations to PostgreSQL via a delete-log table |
| `DeleteLogTable` | No | Name of the delete-log table. Defaults to `"{SourceTable}_deletes"` |

*Provide either `PrimaryKey` or `PrimaryKeys`.

### Delete propagation setup

When `SyncDeletes: true`, create a delete-log table and trigger in SQLite:

```sql
-- 1. Delete-log table
CREATE TABLE IF NOT EXISTS measurements_deletes (
    record_id  TEXT    NOT NULL,
    deleted_at TEXT    NOT NULL DEFAULT (datetime('now')),
    synced     INTEGER NOT NULL DEFAULT 0
);

-- 2. Trigger on the source table
CREATE TRIGGER measurements_delete_log
AFTER DELETE ON measurements
BEGIN
    INSERT INTO measurements_deletes (record_id) VALUES (OLD.measurement_id);
END;

-- For composite PKs, concatenate with PrimaryKeySeparator:
-- INSERT INTO sensor_readings_deletes (record_id)
-- VALUES (OLD.device_id || '|' || CAST(OLD.sequence_no AS TEXT));
```

### Adding a table (no code change or redeployment needed)

Append to the `Tables` array in `syncagent.json` and restart the service:

```json
{
  "SourceTable":      "readings",
  "TargetTable":      "telemetry.readings",
  "PrimaryKey":       "reading_id",
  "InjectStationId":  true,
  "TimestampColumns": ["captured_at"],
  "BooleanColumns":   ["alarm_active"],
  "ConflictStrategy": "update"
}
```

### Environment variable overrides

Any config key can be overridden with a `SYNCAGENT_` prefixed environment variable. Useful for Docker or CI:

```
SYNCAGENT_Postgres__ConnectionString=Host=...
SYNCAGENT_Station__StationId=NODE-01
```

### Local development overrides

Create `syncagent.local.json` (gitignored) to override specific keys without touching `syncagent.json`.

## Admin CLI

SyncAgent includes admin commands that run once and exit without starting the background service. All commands load config from the standard config files.

| Command | Description |
|---|---|
| `syncagent --version` | Print version and exit |
| `syncagent --status` | Print pending and dead-letter counts per table |
| `syncagent --reset-dead-letters` | Reset all dead-letter records (`synced=2`) to pending (`synced=0`) |
| `syncagent --reset-dead-letters --table=<name>` | Reset dead-letters for one table only |
| `syncagent --dry-run` | Run one sync cycle logging what would be synced/deleted, then exit without writing anything to PostgreSQL |

## sync_status States

| `synced` | State | Description |
|---|---|---|
| 0 | Pending | Queued by application, not yet pushed |
| 1 | Synced | Confirmed in PostgreSQL |
| 2 | Dead-letter | Exhausted all retries — frozen until manual reset |

To reset dead-letter records after fixing the root cause, use the CLI:

```powershell
.\SyncAgent.exe --reset-dead-letters
# Or for a specific table:
.\SyncAgent.exe --reset-dead-letters --table=measurements
```

Or directly in SQLite:

```sql
UPDATE sync_status
SET    synced = 0, retry_count = 0, next_attempt = NULL, failure_reason = NULL
WHERE  synced = 2;
```

## Health File

`sync-health.json` is written atomically after every cycle. Fields:

| Field | Description |
|---|---|
| `stationId` | From `Station.StationId` |
| `lastCycleAt` | UTC timestamp of the last cycle |
| `lastSyncedAt` | UTC timestamp of the last successful write |
| `pendingCount` | Records queued but not yet pushed |
| `deadLetterCount` | Records frozen after exhausting retries |
| `postgresReachable` | `true` if the last cycle successfully contacted PostgreSQL |
| `infraDeferredCount` | Records deferred this cycle due to infrastructure failures (not counted as retries) |
| `lastInfraErrorAt` | UTC timestamp of the most recent infrastructure failure |
| `syncedTotal` | Cumulative records synced since the service started |
| `lastCycleDurationMs` | Wall-clock duration of the most recent cycle in milliseconds |
| `agentVersion` | Assembled version string |
| `tables` | Array of `{name, pending, deadLetter}` per configured table |

If `HealthEndpointPort` is set, the same JSON is also served over HTTP at `GET http://localhost:{port}/health/` — useful for Prometheus, Datadog, and load-balancer health checks.

## Features

- **Offline-first** — records accumulate in SQLite while the server is unreachable; sync resumes automatically on reconnect without any records being lost or frozen
- **Infrastructure vs. data failure distinction** — connection failures do not count toward `MaxRetries`; only records rejected by PostgreSQL (type errors, constraint violations) consume retries
- **Exponential backoff** — automatic retry schedule with ±10% jitter
- **Dead-letter** — records exhausting retries are frozen at `synced=2`; never silently discarded
- **Upsert support** — `ConflictStrategy: "update"` rewrites existing rows on conflict
- **Composite primary keys** — multi-column PKs supported via `PrimaryKeys` array
- **Column exclusion and remapping** — `ExcludeColumns` drops local-only columns; `ColumnMap` renames columns on the way out
- **Delete propagation** — `SyncDeletes: true` mirrors SQLite DELETEs to PostgreSQL via a trigger + log-table pattern
- **Schema validation** — at startup, warns if any configured target table or column is missing from PostgreSQL
- **Idempotent writes** — re-sending is always safe
- **Atomic health file** — written via `.tmp` → rename; readers never see a partial file
- **HTTP health endpoint** — optional `GET /health/` endpoint for Prometheus/Datadog scraping
- **Throughput metrics** — cumulative `syncedTotal` and per-cycle `lastCycleDurationMs` in the health file
- **Dead-letter webhook alerts** — POST a JSON payload to Slack/Teams/PagerDuty when a record hits `MaxRetries`
- **Secret warning** — warns at startup if a plaintext password is detected in the config file
- **Admin CLI** — `--status`, `--reset-dead-letters`, `--dry-run`, `--version`
- **Windows Service + systemd** — same binary handles both
- **Zero-touch table additions** — new tables need only a `syncagent.json` edit; no rebuild required

## Project Structure

```
SyncAgent/
├── SyncAgent.csproj
├── syncagent.json                        ← config template (placeholders — edit before deploying)
├── syncagent.example.json                ← fully annotated example with all options and four tables
├── Program.cs                            ← host builder, DI wiring, CLI dispatch
├── Worker.cs                             ← BackgroundService main loop + throughput metrics
├── scripts/
│   ├── install-service.ps1               ← run as Administrator on target machine
│   └── uninstall-service.ps1
├── Cli/
│   └── CliRunner.cs                      ← --status, --reset-dead-letters handlers
├── Config/
│   └── SyncConfig.cs                     ← config POCO + TableMap
├── Data/
│   ├── SQLiteReader.cs                   ← generic hydration, delete-log reads, CLI queries
│   ├── PostgresWriter.cs                 ← INSERT/upsert builder, delete propagation, schema validation
│   └── Models/
│       ├── Records.cs                    ← GenericRecord, PendingDelete
│       └── Results.cs                    ← FailureKind, WriteResult, CycleResult, TableSyncStats
├── Sync/
│   ├── ISyncOrchestrator.cs              ← interface for Worker + test isolation
│   ├── SyncOrchestrator.cs               ← read → write → mark cycle, delete cycle, dry-run
│   └── RetryPolicy.cs                    ← backoff + dead-letter threshold
├── Health/
│   ├── IHealthReporter.cs                ← interface for Worker + test isolation
│   ├── HealthReporter.cs                 ← atomic sync-health.json writer
│   └── HealthEndpoint.cs                 ← optional HTTP /health/ endpoint
├── SyncAgent.Tests/                      ← automated test suite (124 tests, not in deliverable)
│   ├── SyncAgent.Tests.csproj
│   ├── Fixtures/                         ← SqliteFixture, PostgresFixture, TestHelpers
│   ├── Unit/                             ← RetryPolicy, TableMap, WriteResult, Worker
│   ├── Integration/                      ← SQLiteReader, PostgresWriter, SyncOrchestrator (E2E)
│   ├── Health/                           ← HealthReporter, HealthEndpoint
│   └── Cli/                              ← CliRunner
├── sql/
│   ├── sqlite-syncagent.sql              ← run once per SQLite DB (SyncAgent infra only)
│   └── examples/
│       ├── sqlite-schema.example.sql     ← example application SQLite schema
│       └── postgres-schema.example.sql   ← example central PostgreSQL schema
└── docs/
    ├── deployment.md                     ← step-by-step Windows deployment walkthrough
    ├── linux-deployment.md               ← Linux / systemd deployment
    ├── labview.md                        ← LabVIEW integration guide
    ├── labview-testing-guide.md          ← LabVIEW test VI walkthrough (JDP Science libraries)
    └── testing.md                        ← manual testing scenarios + automated test suite
```

## Deployment

See [Deployment Guide](docs/deployment.md) for a step-by-step Windows walkthrough.

For LabVIEW test stations, see the [LabVIEW Integration Guide](docs/labview.md).

For Linux / systemd deployments, see the [Linux Deployment Guide](docs/linux-deployment.md).

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.Data.Sqlite` | 8.0.x | SQLite access |
| `Npgsql` | 8.0.x | PostgreSQL driver |
| `Serilog` + sinks | 4.x / 6.x / 7.x | Structured logging |
| `Microsoft.Extensions.Hosting.WindowsServices` | 8.0.x | Windows Service lifetime |
| `Microsoft.Extensions.Hosting.Systemd` | 8.0.x | systemd lifetime (Linux) |

## Requirements

- .NET 8.0 SDK (build) or self-contained publish (no runtime required on target)
- Windows 10/11 or Linux (x64, arm64, arm)
- Network access to PostgreSQL on port 5432 (only required for sync; offline operation works without it)
