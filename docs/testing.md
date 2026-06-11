# SyncAgent — Testing Guide

**Component:** SyncAgent  
**Version:** 1.2.0

This guide covers how to set up, inject test data, and verify SyncAgent behaviour across all scenarios on a development machine.

SyncAgent is table-agnostic — it syncs any SQLite tables you configure in the `Tables` array of `syncagent.json`. The scenarios below use the four example tables (`sessions`, `tests`, `measurements`, `audit_log`) from `sql/examples/`, but the steps and principles apply equally to any schema.

| Scenario | What it tests |
|---|---|
| [1. Happy path](#1-happy-path--all-four-tables) | All four table types sync from SQLite → PostgreSQL |
| [2. Offline + recovery](#2-offline--recovery) | Postgres unreachable during writes; records synced on reconnect without consuming retries |
| [3. Retry and backoff](#3-retry-and-backoff) | Failed writes increment retry_count; next_attempt honoured |
| [4. Dead letter](#4-dead-letter) | Record exhausts MaxRetries; synced=2, never retried again |
| [5. Idempotency](#5-idempotency--duplicate-sends) | Re-sending already-synced records is a silent no-op |
| [6. Schema check](#6-schema-check--missing-table) | SyncAgent refuses to start if sync_status is missing |
| [7. Health file](#7-health-file-verification) | sync-health.json written atomically; new fields verified |
| [8. Upsert](#8-upsert--conflictstrategy-update) | ConflictStrategy="update" overwrites existing rows |
| [9. Column exclusion & remapping](#9-column-exclusion--remapping) | ExcludeColumns and ColumnMap applied on INSERT |
| [10. Delete propagation](#10-delete-propagation) | SyncDeletes=true mirrors DELETEs to PostgreSQL |
| [11. Dry run](#11-dry-run) | --dry-run logs what would sync, writes nothing, exits |
| [12. Admin CLI](#12-admin-cli) | --status and --reset-dead-letters |
| [13. Windows Service](#13-windows-service-installuninstall) | Install and run as OS service |

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

### Prepare the SQLite station database

```powershell
sqlite3 .\station.db ".read sql\sqlite-syncagent.sql"
sqlite3 .\station.db ".read sql\examples\sqlite-schema.example.sql"
sqlite3 .\station.db "PRAGMA journal_mode;"
# Expected: wal
```

### Configure syncagent.local.json

Create `syncagent.local.json` (gitignored) to override the template values:

```json
{
  "Station":  { "StationId": "ST-TEST", "SiteName": "Test Factory" },
  "Postgres": { "ConnectionString": "Host=localhost;Port=5432;Database=testdata;Username=opcore;Password=opcore_dev_pw" },
  "Sync":     { "IntervalSeconds": 5 }
}
```

Setting `IntervalSeconds` to 5 makes cycles run faster during testing.

---

## Configuring Tables

SyncAgent syncs whatever tables you list in the `Tables` array. No rebuild needed — edit the array and restart.

```json
"Tables": [
  {
    "SourceTable":      "your_table",
    "TargetTable":      "schema.your_table",
    "PrimaryKey":       "your_id_column",
    "InjectStationId":  true,
    "TimestampColumns": ["created_at"],
    "BooleanColumns":   ["is_active"],
    "ConflictStrategy": "nothing"
  }
]
```

The `syncagent.example.json` shows a fully-configured four-table setup. Scenarios below use that configuration.

---

## Record ID Ordering — Important Note

SyncAgent reads `sync_status` in `ORDER BY record_id ASC` and sends each batch to PostgreSQL in that order. If your PostgreSQL schema has foreign-key constraints between tables, your primary-key values must sort in dependency order. The test data below uses prefixed IDs (`t1-1-sess`, `t1-2-test`, `t1-3-meas`) to make this explicit.

---

## 1. Happy Path — All Four Tables

```powershell
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    't1-1-sess', 'OP-001', 'WO-2026-001',
    '2026-06-10T10:00:00', '2026-06-10T10:30:00', 'CLOSED'
);
INSERT INTO tests VALUES (
    't1-2-test', 't1-1-sess',
    'SN-ABC-001', 'Voltage Check',
    '2026-06-10T10:01:00', '2026-06-10T10:02:00', 'PASS'
);
INSERT INTO measurements VALUES (
    't1-3-meas', 't1-2-test',
    'CH1_VOLTAGE', 12.04, 'V',
    11.5, 12.5, 1,
    '2026-06-10T10:01:30'
);
INSERT INTO audit_log VALUES (
    't1-4-aud', 't1-1-sess',
    'OP-001', 'SESSION_CLOSED',
    'Closed by operator after passing test', NULL,
    '2026-06-10T10:03:00'
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

**Expected cycle log:**
```
[INF] Cycle complete. Synced=4 Deleted=0 Pending=0 Failed=0 Deferred=0 DeadLetter=0 Duration=<N>ms
```

**Verify SQLite:**
```powershell
sqlite3 .\station.db "SELECT record_id, table_name, synced FROM sync_status;"
# Expected: all four rows show synced=1
```

**Verify PostgreSQL:**
```powershell
docker exec syncagent-test-postgres psql -U opcore -d testdata -c "
SELECT session_id, station_id, status FROM events.sessions;
SELECT test_id, station_id, verdict FROM events.tests;
SELECT measurement_id, value, in_limit FROM events.measurements;
SELECT audit_id, station_id, action FROM audit.audit_log;
"
```

Key checks: `station_id` on every row; `in_limit` is BOOLEAN (`t`/`f`), not `0`/`1`.

---

## 2. Offline + Recovery

Verifies records accumulate in SQLite while PostgreSQL is unreachable and **do not consume retries** (infrastructure failures are deferred, not retried).

```powershell
docker stop syncagent-test-postgres

sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    't2-1-sess', 'OP-002', 'WO-2026-002',
    '2026-06-10T11:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name) VALUES ('t2-1-sess', 'sessions');
'@

dotnet run --configuration Release
# Expected: [WRN] PostgreSQL unreachable...  Deferred=1, retry_count stays 0
# Ctrl+C

sqlite3 .\station.db "SELECT record_id, synced, retry_count FROM sync_status WHERE record_id='t2-1-sess';"
# Expected: synced=0, retry_count=0  (NOT incremented — infrastructure failure)

docker start syncagent-test-postgres
Start-Sleep -Seconds 5

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1 Pending=0

sqlite3 .\station.db "SELECT record_id, synced FROM sync_status WHERE record_id='t2-1-sess';"
# Expected: synced=1
```

---

## 3. Retry and Backoff

```powershell
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    't3-1-sess', 'OP-003', NULL,
    '2026-06-10T12:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name, synced, retry_count, next_attempt, failure_reason)
VALUES ('t3-1-sess', 'sessions', 0, 1, datetime('now', '+1 hour'), 'Simulated prior failure');
'@

dotnet run --configuration Release
# Record must NOT appear — next_attempt is in the future.
# Ctrl+C

# Fast-forward next_attempt
sqlite3 .\station.db "UPDATE sync_status SET next_attempt=datetime('now','-1 second') WHERE record_id='t3-1-sess';"

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1
```

---

## 4. Dead Letter

```powershell
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    't4-1-sess', 'OP-004', NULL,
    '2026-06-10T13:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name, synced, retry_count, next_attempt, failure_reason)
VALUES ('t4-1-sess', 'sessions', 0, 9, datetime('now', '-1 second'), 'Approaching dead letter');
'@

docker stop syncagent-test-postgres

dotnet run --configuration Release
# Expected: [ERR] DeadLetter: t4-1-sess  →  Cycle complete. DeadLetter=1

sqlite3 .\station.db "SELECT synced, retry_count FROM sync_status WHERE record_id='t4-1-sess';"
# Expected: synced=2, retry_count=10

Get-Content .\sync-health.json | ConvertFrom-Json
# Expected: deadLetterCount=1, postgresReachable=false
```

**Recovery using the CLI:**

```powershell
docker start syncagent-test-postgres
Start-Sleep -Seconds 5

dotnet run --configuration Release -- --reset-dead-letters
# Or with the packaged binary: .\SyncAgent.exe --reset-dead-letters
# Expected: Reset 1 dead-letter record(s) to pending.

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1
```

---

## 5. Idempotency — Duplicate Sends

```powershell
sqlite3 .\station.db "UPDATE sync_status SET synced=0, retry_count=0, next_attempt=NULL WHERE record_id='t1-1-sess';"

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1 — no error

docker exec syncagent-test-postgres psql -U opcore -d testdata -t -c "
SELECT COUNT(*) FROM events.sessions WHERE session_id='t1-1-sess';
"
# Expected: 1 (no duplicate)
```

---

## 6. Schema Check — Missing Table

```powershell
sqlite3 .\station.db "ALTER TABLE sync_status RENAME TO sync_status_backup;"

dotnet run --configuration Release
# Expected: [ERR] sync_status table not found.  Process exits immediately.

sqlite3 .\station.db "ALTER TABLE sync_status_backup RENAME TO sync_status;"
```

---

## 7. Health File Verification

```powershell
dotnet run --configuration Release
Get-Content .\sync-health.json | ConvertFrom-Json
```

Expected shape (v1.2.0):
```json
{
  "stationId":            "ST-TEST",
  "lastCycleAt":          "2026-06-10T10:32:15.0000000Z",
  "lastSyncedAt":         "2026-06-10T10:32:14.0000000Z",
  "pendingCount":         0,
  "deadLetterCount":      0,
  "postgresReachable":    true,
  "infraDeferredCount":   0,
  "lastInfraErrorAt":     null,
  "syncedTotal":          4,
  "lastCycleDurationMs":  52,
  "agentVersion":         "1.2.0.0",
  "tables": [
    { "name": "sessions",     "pending": 0, "deadLetter": 0 },
    { "name": "tests",        "pending": 0, "deadLetter": 0 },
    { "name": "measurements", "pending": 0, "deadLetter": 0 },
    { "name": "audit_log",    "pending": 0, "deadLetter": 0 }
  ]
}
```

Verify no partial file left behind:
```powershell
Test-Path .\sync-health.json.tmp   # Expected: False
```

---

## 8. Upsert — ConflictStrategy: "update"

Add `"ConflictStrategy": "update"` to a table mapping (e.g. `tests`). Run Scenario 1 to insert initial data, then update a field in SQLite and re-queue the record.

```powershell
sqlite3 .\station.db @'
UPDATE tests SET verdict='FAIL' WHERE test_id='t1-2-test';
UPDATE sync_status SET synced=0, retry_count=0, next_attempt=NULL WHERE record_id='t1-2-test';
'@

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1
```

**Verify the row was updated in PostgreSQL:**
```powershell
docker exec syncagent-test-postgres psql -U opcore -d testdata -t -c "
SELECT verdict FROM events.tests WHERE test_id='t1-2-test';
"
# Expected: FAIL  (was PASS before upsert)
```

With `ConflictStrategy: "nothing"` (default), re-queueing the same record would be a silent no-op and the verdict would remain `PASS`.

---

## 9. Column Exclusion & Remapping

Add to the `measurements` table mapping:
```json
"ExcludeColumns": ["raw_adc"],
"ColumnMap":      { "ts": "captured_at" }
```

(If your `measurements` table has a `raw_adc` column in SQLite and uses `ts` instead of `captured_at`.)

Insert a measurement with a `raw_adc` value, run SyncAgent, then verify in PostgreSQL:

```powershell
docker exec syncagent-test-postgres psql -U opcore -d testdata -t -c "
SELECT column_name FROM information_schema.columns
WHERE table_name='measurements' AND table_schema='events';
"
# Verify: raw_adc is NOT a column (excluded), captured_at IS present (renamed from ts)
```

---

## 10. Delete Propagation

Requires `SyncDeletes: true` on a table mapping and a delete-log table + trigger in SQLite.

**Setup:**
```powershell
sqlite3 .\station.db @'
CREATE TABLE IF NOT EXISTS measurements_deletes (
    record_id  TEXT    NOT NULL,
    deleted_at TEXT    NOT NULL DEFAULT (datetime('now')),
    synced     INTEGER NOT NULL DEFAULT 0
);
CREATE TRIGGER IF NOT EXISTS measurements_delete_log
AFTER DELETE ON measurements
BEGIN
    INSERT INTO measurements_deletes (record_id) VALUES (OLD.measurement_id);
END;
'@
```

**Test:**
```powershell
# Delete the measurement record from SQLite
sqlite3 .\station.db "DELETE FROM measurements WHERE measurement_id='t1-3-meas';"

dotnet run --configuration Release
# Expected: Cycle complete. Deleted=1
```

**Verify in PostgreSQL:**
```powershell
docker exec syncagent-test-postgres psql -U opcore -d testdata -t -c "
SELECT COUNT(*) FROM events.measurements WHERE measurement_id='t1-3-meas';
"
# Expected: 0 (row deleted)
```

**Verify delete-log marked synced:**
```powershell
sqlite3 .\station.db "SELECT record_id, synced FROM measurements_deletes;"
# Expected: synced=1
```

---

## 11. Dry Run

```powershell
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    't11-1-sess', 'OP-011', NULL,
    '2026-06-10T14:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name) VALUES ('t11-1-sess', 'sessions');
'@

dotnet run --configuration Release -- --dry-run
# Expected:
#   [INF] --dry-run flag detected. SyncAgent will read and log pending records but write nothing.
#   [INF] [DRY RUN] Would sync 1 records for table sessions
#   [INF] Dry run complete. Stopping.
# Process exits after one cycle.

sqlite3 .\station.db "SELECT synced FROM sync_status WHERE record_id='t11-1-sess';"
# Expected: synced=0  (nothing written)

docker exec syncagent-test-postgres psql -U opcore -d testdata -t -c "
SELECT COUNT(*) FROM events.sessions WHERE session_id='t11-1-sess';
"
# Expected: 0  (not inserted)
```

---

## 12. Admin CLI

### --status

```powershell
dotnet run --configuration Release -- --status
```

Expected output:
```
Table                     Pending  DeadLetter  Last synced
----------------------------------------------------------------------
sessions                        0           0  2026-06-10T10:32:14
tests                           0           0  2026-06-10T10:32:14
measurements                    0           0  2026-06-10T10:32:14
audit_log                       0           0  2026-06-10T10:32:14
----------------------------------------------------------------------
TOTAL                           0           0
```

### --reset-dead-letters

```powershell
# Create a dead-letter record first (see Scenario 4), then:
dotnet run --configuration Release -- --reset-dead-letters
# Expected: Reset 1 dead-letter record(s) to pending.

# Scope to one table:
dotnet run --configuration Release -- --reset-dead-letters --table=sessions
# Expected: Reset 1 dead-letter record(s) in table 'sessions' to pending.
```

---

## 13. Windows Service Install/Uninstall

> **Requires Administrator PowerShell.**  
> Use the packaged deliverable folder — the install script must sit next to `SyncAgent.exe`.

```powershell
cd .\SyncAgent-v1.2.0-win-x64-selfcontained\

# Edit syncagent.json with real values before installing.

.\install-service.ps1

sc.exe query SyncAgent
# Expected: STATE : 4  RUNNING

# Check logs
Get-ChildItem .\logs\

# Uninstall
.\uninstall-service.ps1
sc.exe query SyncAgent   # service no longer found
```

---

## Resetting Test State

**SQLite — wipe all data:**

```powershell
sqlite3 .\station.db @'
DELETE FROM sync_status;
DELETE FROM measurements_deletes;
DELETE FROM measurements;
DELETE FROM audit_log;
DELETE FROM tests;
DELETE FROM sessions;
'@
```

**Or recreate the DB entirely:**

```powershell
Remove-Item .\station.db, .\station.db-wal, .\station.db-shm -ErrorAction SilentlyContinue
sqlite3 .\station.db ".read sql\sqlite-syncagent.sql"
sqlite3 .\station.db ".read sql\examples\sqlite-schema.example.sql"
```

**PostgreSQL — wipe all synced data:**

```powershell
docker exec syncagent-test-postgres psql -U opcore -d testdata -c "
TRUNCATE events.measurements, events.tests, events.sessions, audit.audit_log RESTART IDENTITY CASCADE;
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
| 2 | Dead Letter | Exhausted all retries due to data errors — requires manual review and reset |

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

All waits include ±10% jitter. Infrastructure failures (network down, connection refused) are **not counted** as retries — a station offline for days will have all its records ready to sync the moment connectivity is restored.
