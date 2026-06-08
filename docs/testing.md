# SyncAgent — Testing Guide

**Component:** SyncAgent  
**Version:** 1.0.0

This guide covers how to set up, inject test data, and verify SyncAgent behaviour across all scenarios on a development machine.

SyncAgent is table-agnostic — it syncs any SQLite tables you configure in the `Tables` array of `syncagent.json`. The scenarios below use the four example tables (`sessions`, `tests`, `measurements`, `audit_log`) from `sql/examples/`, but the steps and principles apply equally to any schema.

| Scenario | What it tests |
|---|---|
| [1. Happy path](#1-happy-path--all-four-tables) | All four table types sync from SQLite → PostgreSQL |
| [2. Offline + recovery](#2-offline--recovery) | Postgres unreachable during writes; records synced on reconnect |
| [3. Retry and backoff](#3-retry-and-backoff) | Failed writes increment retry_count; next_attempt honoured |
| [4. Dead letter](#4-dead-letter) | Record exhausts MaxRetries; synced=2, never retried again |
| [5. Idempotency](#5-idempotency--duplicate-sends) | Re-sending already-synced records is a silent no-op |
| [6. Schema version mismatch](#6-schema-version-mismatch) | SyncAgent refuses to start if SQLite schema version is wrong |
| [7. Health file](#7-health-file-verification) | sync-health.json is written atomically after every cycle |
| [8. Windows Service](#8-windows-service-installuninstall) | Install and run as OS service |

---

## Prerequisites

| Tool | Purpose | Install |
|---|---|---|
| .NET 8 SDK | Build and run from source | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| Docker Desktop | Run PostgreSQL | [docker.com](https://www.docker.com/products/docker-desktop) |
| `sqlite3.exe` | Inject test data from PowerShell | Ships with the deliverable packages; also at [sqlite.org/download.html](https://sqlite.org/download.html) |
| DB Browser for SQLite *(optional)* | Inspect `station.db` visually | [sqlitebrowser.org](https://sqlitebrowser.org) |
| psql / pgAdmin *(optional)* | Query PostgreSQL directly | ships with PostgreSQL or Docker |

Verify installs:

```powershell
dotnet --version   # 8.x.x or higher
docker --version
```

---

## Running against the packaged deliverables vs. source

All scenarios work the same way regardless of how you run SyncAgent. Replace `dotnet run --configuration Release` with the path to the packaged binary as needed:

```powershell
# From source
dotnet run --configuration Release

# Framework-dependent package (requires .NET 8 Runtime installed)
& ".\SyncAgent-v1.0.0-win-x64-frameworkdependent\SyncAgent.exe"

# Self-contained package (no prerequisites)
& ".\SyncAgent-v1.0.0-win-x64-selfcontained\SyncAgent.exe"
```

When using a package, the `station.db`, `syncagent.json`, `sync-health.json`, and `logs/` folder are all relative to the package folder — place them alongside `SyncAgent.exe`.

---

## Infrastructure Setup

### Start PostgreSQL

```powershell
docker run -d `
  --name syncagent-test-postgres `
  -e POSTGRES_USER=opcore `
  -e POSTGRES_PASSWORD=opcore_dev_pw `
  -e POSTGRES_DB=testdata `
  -p 5432:5432 `
  postgres:16
```

Wait ~10 seconds for it to start, then verify:

```powershell
docker exec syncagent-test-postgres pg_isready -U opcore -d testdata
# localhost:5432 - accepting connections
```

### Create PostgreSQL schemas and tables

```powershell
Get-Content sql\examples\postgres-schema.example.sql | docker exec -i syncagent-test-postgres psql -U opcore -d testdata
```

This creates the `events`, `audit`, and `core` schemas plus all example tables. Adjust to your own schema if you are not using the example tables.

### Prepare the SQLite station database

```powershell
sqlite3 .\station.db ".read sql\sqlite-syncagent.sql"
sqlite3 .\station.db ".read sql\examples\sqlite-schema.example.sql"
sqlite3 .\station.db "SELECT version FROM schema_version;"
# Expected: 1
sqlite3 .\station.db "PRAGMA journal_mode;"
# Expected: wal
```

`sqlite-syncagent.sql` sets WAL journal mode automatically. WAL allows SyncAgent and your test scripts to write to the database concurrently without blocking each other. If you are working with an existing database that was created before this change, set it once:

```powershell
sqlite3 .\station.db "PRAGMA journal_mode=WAL;"
# Expected: wal
```

> **No sqlite3 CLI?** Open DB Browser for SQLite → New Database → `station.db` → Execute SQL → paste `sql/sqlite-syncagent.sql`, then `sql/examples/sqlite-schema.example.sql`.

### Configure syncagent.local.json

`syncagent.json` ships with placeholder values. Create a `syncagent.local.json` alongside it (gitignored) to override them with your local credentials:

```powershell
Copy-Item .\syncagent.json .\syncagent.local.json
# Edit syncagent.local.json — set Postgres:ConnectionString, Station:StationId, Station:SiteName
```

Only the keys you need to override must be in `syncagent.local.json`; everything else falls through to `syncagent.json`.

Recommended values for local testing:

```json
{
  "Station":  { "StationId": "ST-TEST", "SiteName": "Test Factory" },
  "Postgres": { "ConnectionString": "Host=localhost;Port=5432;Database=testdata;Username=opcore;Password=opcore_dev_pw" },
  "Sync":     { "IntervalSeconds": 5 }
}
```

Setting `IntervalSeconds` to 5 makes cycles run faster during testing so you do not have to wait 30 seconds per scenario.

---

## Configuring Tables

SyncAgent syncs whatever tables you list in the `Tables` array in `syncagent.json`. No code change or rebuild is needed to add, remove, or rename tables — edit the array and restart.

```json
"Tables": [
  {
    "SourceTable":      "your_table",
    "TargetTable":      "schema.your_table",
    "PrimaryKey":       "your_id_column",
    "InjectStationId":  true,
    "TimestampColumns": ["created_at", "updated_at"],
    "BooleanColumns":   ["is_active"]
  }
]
```

The example `syncagent.example.json` shows a fully-configured four-table setup. The scenarios below use that configuration.

---

## Record ID Ordering — Important Note

SyncAgent reads `sync_status` in `ORDER BY record_id ASC` and sends each batch to PostgreSQL in that order inside a single transaction. If your PostgreSQL schema has foreign-key constraints between tables (e.g. `tests` references `sessions`), your primary-key values must sort in dependency order — sessions must have a smaller key than the tests that reference them, which must be smaller than measurements.

The simplest way to guarantee this is to use **time-ordered primary keys** (ULIDs, UUID v7, or a timestamp-prefixed value). Since sessions are always created before their tests, time-ordered keys naturally sort in the correct dependency order.

The test data in the scenarios below uses prefixed IDs (`t1-1-sess`, `t1-2-test`, `t1-3-meas`) to make this ordering explicit. In production, ULIDs handle this automatically.

---

## 1. Happy Path — All Four Tables

Insert one record of each type plus their `sync_status` rows. The foreign key chain is `sessions → tests → measurements`; `audit_log` is independent.

```powershell
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    't1-1-sess', 'OP-001', 'WO-2026-001',
    '2026-06-08T10:00:00', '2026-06-08T10:30:00', 'CLOSED'
);
INSERT INTO tests VALUES (
    't1-2-test', 't1-1-sess',
    'SN-ABC-001', 'Voltage Check',
    '2026-06-08T10:01:00', '2026-06-08T10:02:00', 'PASS'
);
INSERT INTO measurements VALUES (
    't1-3-meas', 't1-2-test',
    'CH1_VOLTAGE', 12.04, 'V',
    11.5, 12.5, 1,
    '2026-06-08T10:01:30'
);
INSERT INTO audit_log VALUES (
    't1-4-aud', 't1-1-sess',
    'OP-001', 'SESSION_CLOSED',
    'Closed by operator after passing test', NULL,
    '2026-06-08T10:03:00'
);
INSERT INTO sync_status (record_id, table_name) VALUES
    ('t1-1-sess', 'sessions'),
    ('t1-2-test', 'tests'),
    ('t1-3-meas', 'measurements'),
    ('t1-4-aud',  'audit_log');
'@
```

Run SyncAgent:

```powershell
dotnet run --configuration Release
```

**Expected console output after one cycle:**
```
[HH:mm:ss INF] SyncAgent starting. StationId=ST-TEST SiteName=Test Factory Interval=5s
[HH:mm:ss INF] SQLite schema version OK: 1
[HH:mm:ss INF] Table mappings loaded: sessions→events.sessions, tests→events.tests, measurements→events.measurements, audit_log→audit.audit_log
[HH:mm:ss INF] PostgreSQL connection verified.
[HH:mm:ss DBG] Batch committed: 4 records
[HH:mm:ss INF] Cycle complete. Synced=4 Pending=0 Failed=0 DeadLetter=0
```

**Verify SQLite sync_status:**

```powershell
sqlite3 .\station.db "SELECT record_id, table_name, synced FROM sync_status;"
# Expected: all four rows show synced=1
```

**Verify PostgreSQL:**

```powershell
docker exec syncagent-test-postgres psql -U opcore -d testdata -c "
SELECT session_id, station_id, operator_id, status, synced_at FROM events.sessions;
SELECT test_id, station_id, verdict, synced_at FROM events.tests;
SELECT measurement_id, channel_name, value, in_limit, synced_at FROM events.measurements;
SELECT audit_id, station_id, action, synced_at FROM audit.audit_log;
"
```

Key checks:
- `station_id` on every row (SyncAgent injected this — not present in SQLite)
- `synced_at` is recent (set by PostgreSQL `DEFAULT NOW()`)
- `in_limit` is a proper `BOOLEAN` (`t`/`f`), not `0`/`1` — SyncAgent converted it
- All four tables have exactly one row each

---

## 2. Offline + Recovery

Verifies records accumulate in SQLite while PostgreSQL is unreachable, then flush on reconnect.

```powershell
docker stop syncagent-test-postgres

sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    't2-1-sess', 'OP-002', 'WO-2026-002',
    '2026-06-08T11:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name)
VALUES ('t2-1-sess', 'sessions');
'@

dotnet run --configuration Release
# Expected: [WRN] PostgreSQL unreachable at startup.
# Ctrl+C to stop

docker start syncagent-test-postgres
Start-Sleep -Seconds 5

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1 Pending=0 Failed=0 DeadLetter=0

sqlite3 .\station.db "SELECT record_id, synced FROM sync_status WHERE record_id='t2-1-sess';"
# Expected: synced=1
```

---

## 3. Retry and Backoff

Verifies `next_attempt` is respected — a record is not retried until its backoff window expires.

```powershell
# Insert a record with next_attempt far in the future
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    't3-1-sess', 'OP-003', NULL,
    '2026-06-08T12:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name, synced, retry_count, next_attempt, failure_reason)
VALUES (
    't3-1-sess', 'sessions',
    0, 1, datetime('now', '+1 hour'), 'Simulated prior failure'
);
'@

dotnet run --configuration Release
# Record must NOT appear in cycle output — next_attempt is in the future.
# If there are no other pending records, there will be no "Cycle complete" line at all.
# Ctrl+C

sqlite3 .\station.db "SELECT synced, retry_count, next_attempt FROM sync_status WHERE record_id='t3-1-sess';"
# Expected: synced=0, retry_count=1, next_attempt still in future

# Fast-forward next_attempt to now
sqlite3 .\station.db "UPDATE sync_status SET next_attempt=datetime('now','-1 second') WHERE record_id='t3-1-sess';"

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1 Pending=0 Failed=0 DeadLetter=0

sqlite3 .\station.db "SELECT synced, retry_count FROM sync_status WHERE record_id='t3-1-sess';"
# Expected: synced=1, retry_count=1 (carried over from prior attempt)
```

---

## 4. Dead Letter

Verifies a record reaching `MaxRetries` (default: 10) is permanently moved to `synced=2`.

```powershell
# Insert record at retry 9 (one attempt from dead letter)
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    't4-1-sess', 'OP-004', NULL,
    '2026-06-08T13:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name, synced, retry_count, next_attempt, failure_reason)
VALUES (
    't4-1-sess', 'sessions',
    0, 9, datetime('now', '-1 second'), 'Approaching dead letter'
);
'@

docker stop syncagent-test-postgres

dotnet run --configuration Release
# Expected:
#   [ERR] DeadLetter: t4-1-sess table=sessions after 10 retries.
#   [INF] Cycle complete. Synced=0 Pending=1 Failed=1 DeadLetter=1

sqlite3 .\station.db "SELECT record_id, synced, retry_count FROM sync_status WHERE record_id='t4-1-sess';"
# Expected: synced=2, retry_count=10

Get-Content .\sync-health.json | ConvertFrom-Json
# Expected: deadLetterCount=1, postgresReachable=false
```

**Manual recovery after fixing the root cause:**

```powershell
sqlite3 .\station.db @"
UPDATE sync_status
SET synced=0, retry_count=0, next_attempt=NULL, failure_reason=NULL
WHERE record_id='t4-1-sess';
"@

docker start syncagent-test-postgres
Start-Sleep -Seconds 5

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1 Pending=0 Failed=0 DeadLetter=0
```

---

## 5. Idempotency — Duplicate Sends

```powershell
# Reset an already-synced record to pending
sqlite3 .\station.db "UPDATE sync_status SET synced=0, retry_count=0, next_attempt=NULL WHERE record_id='t1-1-sess';"

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1 Pending=0 — no error

# Verify no duplicate row in PostgreSQL
docker exec syncagent-test-postgres psql -U opcore -d testdata -t -c "
SELECT COUNT(*) FROM events.sessions WHERE session_id='t1-1-sess';
"
# Expected: 1
```

---

## 6. Schema Version Mismatch

```powershell
sqlite3 .\station.db "UPDATE schema_version SET version=99;"

dotnet run --configuration Release
# Expected:
#   [ERR] Schema version mismatch. SQLite=99 Expected=1. Sync suspended.
#   [FTL] SyncAgent terminated unexpectedly.
# Process exits immediately — no sync cycle runs.

sqlite3 .\station.db "UPDATE schema_version SET version=1;"
# Subsequent run starts normally.
```

---

## 7. Health File Verification

```powershell
dotnet run --configuration Release
Get-Content .\sync-health.json | ConvertFrom-Json
```

Expected shape:
```json
{
  "stationId":         "ST-TEST",
  "lastCycleAt":       "2026-06-08T10:32:15.0000000Z",
  "lastSyncedAt":      null,
  "pendingCount":      0,
  "deadLetterCount":   0,
  "postgresReachable": true,
  "agentVersion":      "1.0.0.0"
}
```

Verify no partial file left behind:
```powershell
Test-Path .\sync-health.json.tmp   # Expected: False
```

> **Note on `postgresReachable`:** this field reflects the most recent *write attempt*, not a periodic ping. If there are no pending records, SyncAgent returns early without contacting PostgreSQL, so `postgresReachable` will remain `true` from the previous successful cycle. This is by design — no unnecessary connections are made when there is nothing to sync.

---

## 8. Windows Service Install/Uninstall

> **Requires Administrator PowerShell.**  
> Use the packaged deliverable folders — the install script must sit next to `SyncAgent.exe`.

```powershell
# Framework-dependent
cd .\SyncAgent-v1.0.0-win-x64-frameworkdependent\

# OR self-contained (no .NET Runtime required on the target machine)
cd .\SyncAgent-v1.0.0-win-x64-selfcontained\

# Edit syncagent.json in this folder with real values before installing.

.\install-service.ps1

sc.exe query SyncAgent
# Expected: STATE : 4  RUNNING

# Check logs
Get-ChildItem .\logs\

# Uninstall
.\uninstall-service.ps1
sc.exe query SyncAgent   # service no longer found
```

The install script sets up automatic restart on failure (3 attempts, 60-second delay) and configures the service to start automatically at boot.

---

## Resetting Test State

Run this between scenarios to start from a clean slate:

**SQLite — wipe all data:**

```powershell
sqlite3 .\station.db @'
DELETE FROM sync_status;
DELETE FROM measurements;
DELETE FROM audit_log;
DELETE FROM tests;
DELETE FROM sessions;
'@
```

**Or recreate the DB entirely (also resets WAL files):**

```powershell
Remove-Item .\station.db, .\station.db-wal, .\station.db-shm -ErrorAction SilentlyContinue
sqlite3 .\station.db ".read sql\sqlite-syncagent.sql"
sqlite3 .\station.db ".read sql\examples\sqlite-schema.example.sql"
```

**PostgreSQL — wipe all synced data:**

```powershell
docker exec syncagent-test-postgres psql -U opcore -d testdata -c "
TRUNCATE events.measurements, events.tests, events.sessions, audit.audit_log, core.station_sync_state RESTART IDENTITY CASCADE;
"
```

**Logs and health file:**
```powershell
Remove-Item .\sync-health.json -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force .\logs -ErrorAction SilentlyContinue
```

---

## sync_status Reference

| `synced` value | Name | Meaning |
|---|---|---|
| 0 | Pending | Written locally, not yet pushed to PostgreSQL |
| 1 | Synced | Confirmed in PostgreSQL — will not be retried |
| 2 | Dead Letter | Exhausted all retries — requires manual review and reset |

## Backoff Schedule

| Retry | Wait | Cumulative |
|---|---|---|
| 1 | 30 s | 30 s |
| 2 | 60 s | 1.5 min |
| 3 | 2 min | 3.5 min |
| 4 | 5 min | 8.5 min |
| 5 | 15 min | 23.5 min |
| 6–10 | 1 hr each | ~5.5 hrs total |
| 10 | Dead Letter | — |

All waits include ±10% jitter to prevent a thundering herd when multiple stations reconnect simultaneously.
