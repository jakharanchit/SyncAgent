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

This adds the `sync_status` and `schema_version` tables and sets WAL journal mode. Your application tables are separate — create them however you normally would.

SyncAgent also enforces WAL mode automatically on startup, so existing databases are migrated without any manual step.

### 2. Queue records for sync

After every INSERT into a business table, also insert into `sync_status`:

```sql
INSERT OR IGNORE INTO sync_status (record_id, table_name)
VALUES ('<primary_key_value>', '<table_name>');
```

> **Primary key ordering:** SyncAgent sends each batch to PostgreSQL in `record_id ASC` order inside a single transaction. If your PostgreSQL schema uses foreign-key constraints across tables (e.g. `tests` references `sessions`), your primary-key values must sort in dependency order so parent records are inserted before their children. Time-ordered keys such as ULIDs or UUID v7 satisfy this naturally, since parent records are always created first.

### 3. Create the matching PostgreSQL tables

Design your central schema however you like. A useful pattern is to add a `synced_at TIMESTAMPTZ NOT NULL DEFAULT NOW()` column so you know when SyncAgent delivered each row.

### 4. Configure syncagent.json

```json
{
  "Station":  { "StationId": "NODE-01", "SiteName": "Site A" },
  "Postgres": { "ConnectionString": "Host=<host>;Database=<db>;Username=<user>;Password=<pw>" },
  "Sync":     { "SQLitePath": "C:\\Data\\your.db" },
  "Tables": [
    {
      "SourceTable":      "orders",
      "TargetTable":      "public.orders",
      "PrimaryKey":       "order_id",
      "InjectStationId":  false,
      "TimestampColumns": ["created_at"],
      "BooleanColumns":   ["is_paid"]
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

All configuration lives in **`syncagent.json`**.

| Key | Default | Description |
|---|---|---|
| `Station.StationId` | `""` | Identifier injected as `station_id` when `InjectStationId=true`; leave empty if not used |
| `Station.SiteName` | `""` | Human-readable name shown in logs |
| `Postgres.ConnectionString` | *(required)* | Npgsql connection string to the central server |
| `Sync.SQLitePath` | `./local.db` | Path to the SQLite database |
| `Sync.IntervalSeconds` | `30` | Poll interval |
| `Sync.BatchSize` | `100` | Max records pushed per cycle |
| `Sync.MaxRetries` | `10` | Attempts before a record is dead-lettered |
| `Sync.HealthFilePath` | `./sync-health.json` | Written after every cycle for external monitoring |

> **Retry backoff** is computed automatically: 30 s → 60 s → 2 min → 4 min → … capped at 1 hour, ±10% jitter. There is no config knob for this.
| `Logging.LogPath` | `./logs` | Rolling log directory |
| `Logging.MinLevel` | `Information` | `Debug` / `Information` / `Warning` / `Error` |
| `Tables` | `[]` | Table mappings — see below |

### Table mapping fields

| Field | Required | Description |
|---|---|---|
| `SourceTable` | Yes | Table name in SQLite |
| `TargetTable` | Yes | PostgreSQL destination as `schema.table` or `table` |
| `PrimaryKey` | Yes | Column used for `ON CONFLICT` and `sync_status.record_id` |
| `InjectStationId` | No (default: `true`) | Adds `Station.StationId` as `station_id` on every INSERT |
| `TimestampColumns` | No (default: `[]`) | TEXT columns in SQLite → `TIMESTAMPTZ` in PostgreSQL |
| `BooleanColumns` | No (default: `[]`) | `0`/`1` INTEGER columns in SQLite → `BOOLEAN` in PostgreSQL |

### Adding a table (no code change or redeployment needed)

Append to the `Tables` array in `syncagent.json` and restart the service:

```json
{
  "SourceTable":      "readings",
  "TargetTable":      "telemetry.readings",
  "PrimaryKey":       "reading_id",
  "InjectStationId":  true,
  "TimestampColumns": ["captured_at"],
  "BooleanColumns":   ["alarm_active"]
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

## sync_status States

| `synced` | State | Description |
|---|---|---|
| 0 | Pending | Queued by application, not yet pushed |
| 1 | Synced | Confirmed in PostgreSQL |
| 2 | Dead-letter | Exhausted all retries — frozen until manual reset |

To reset dead-letter records after fixing the root cause:

```sql
UPDATE sync_status
SET    synced = 0, retry_count = 0, next_attempt = NULL, failure_reason = NULL
WHERE  synced = 2;
```

## Features

- **Offline-first** — records accumulates in SQLite while the server is unreachable; sync resumes automatically on reconnect
- **Exponential backoff** — configurable retry schedule with ±10% jitter
- **Dead-letter** — records exhausting all retries are frozen at `synced=2` and never silently discarded
- **Idempotent writes** — `ON CONFLICT DO NOTHING` on all INSERT statements; re-sending is always safe
- **Atomic health file** — written via `.tmp` → rename; readers never see a partial file
- **Schema version guard** — refuses to start if SyncAgent's own infrastructure tables are missing or incompatible
- **Windows Service + systemd** — same binary handles both
- **Zero-touch table additions** — new tables need only a `syncagent.json` edit; no rebuild required

## Project Structure

```
SyncAgent/
├── SyncAgent.csproj
├── syncagent.json                        ← config template (placeholders — edit before deploying)
├── syncagent.example.json                ← fully populated example with all four tables
├── Program.cs                            ← host builder + DI wiring
├── Worker.cs                             ← BackgroundService main loop
├── scripts/
│   ├── install-service.ps1               ← run as Administrator on target machine
│   └── uninstall-service.ps1
├── Config/
│   └── SyncConfig.cs                     ← config POCO + TableMap
├── Data/
│   ├── SQLiteReader.cs                   ← generic dynamic hydration (SELECT *)
│   ├── PostgresWriter.cs                 ← generic dynamic INSERT builder
│   └── Models/
│       ├── Records.cs
│       └── Results.cs
├── Sync/
│   ├── SyncOrchestrator.cs               ← read → write → mark cycle
│   └── RetryPolicy.cs                    ← backoff + dead-letter threshold
├── Health/
│   └── HealthReporter.cs                 ← atomic sync-health.json writer
├── sql/
│   ├── sqlite-syncagent.sql              ← run once per SQLite DB (SyncAgent infra only)
│   └── examples/
│       ├── sqlite-schema.example.sql     ← example application SQLite schema
│       └── postgres-schema.example.sql   ← example central PostgreSQL schema
└── docs/
    ├── client-deployment.md              ← step-by-step deployment walkthrough
    ├── labview.md                        ← LabVIEW integration guide
    ├── linux-deployment.md               ← Linux / systemd deployment
    └── testing.md                        ← developer testing guide
```

## Deployment

See [Client Deployment Guide](docs/client-deployment.md) for a step-by-step walkthrough.

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
