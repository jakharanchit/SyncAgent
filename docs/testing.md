# SyncAgent — Testing Guide

**Component:** SyncAgent  
**Version:** 1.0.0

This guide covers how to set up, inject test data, and verify SyncAgent behaviour across all scenarios on a development machine.

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
| .NET 8 SDK | Build and run SyncAgent | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| Docker Desktop | Run PostgreSQL | [docker.com](https://www.docker.com/products/docker-desktop) |
| DB Browser for SQLite *(optional)* | Inspect `station.db` visually | [sqlitebrowser.org](https://sqlitebrowser.org) |
| psql / pgAdmin *(optional)* | Query PostgreSQL directly | ships with PostgreSQL or Docker |

Verify installs:

```powershell
dotnet --version   # 8.x.x or higher
docker --version
```

---

## Build

```powershell
cd <repo-root>
dotnet build --configuration Release
```

Expected:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
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
sqlite3 .\station.db "SELECT version FROM schema_version;"
# Expected: 1
```

> **No sqlite3 CLI?** Open DB Browser for SQLite → New Database → `station.db` → Execute SQL → paste `sql/sqlite-syncagent.sql`, then `sql/examples/sqlite-schema.example.sql`.

### Configure syncagent.local.json

`syncagent.json` ships with placeholder values. Create a `syncagent.local.json` alongside it (gitignored) to override them with your local credentials:

```powershell
Copy-Item .\syncagent.json .\syncagent.local.json
# Edit syncagent.local.json — set Postgres:ConnectionString, Station:StationId, Station:SiteName
```

Only the keys you need to override must be in `syncagent.local.json`; everything else falls through to `syncagent.json`.

---

## 1. Happy Path — All Four Tables

Insert one record of each type plus their `sync_status` rows. The foreign key chain is `sessions → tests → measurements`; `audit_log` is independent.

```powershell
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    '018f0001-0000-7000-0000-000000000001',
    'OP-001', 'WO-2026-001',
    '2026-06-05T10:00:00', NULL, 'CLOSED'
);
INSERT INTO tests VALUES (
    '018f0001-0000-7000-0000-000000000002',
    '018f0001-0000-7000-0000-000000000001',
    'SN-ABC-001', 'Voltage Check',
    '2026-06-05T10:01:00', '2026-06-05T10:02:00', 'PASS'
);
INSERT INTO measurements VALUES (
    '018f0001-0000-7000-0000-000000000003',
    '018f0001-0000-7000-0000-000000000002',
    'CH1_VOLTAGE', 12.04, 'V',
    11.5, 12.5, 1,
    '2026-06-05T10:01:30'
);
INSERT INTO audit_log VALUES (
    '018f0001-0000-7000-0000-000000000004',
    '018f0001-0000-7000-0000-000000000001',
    'OP-001', 'SESSION_CLOSED',
    'Closed by operator after passing test', NULL,
    '2026-06-05T10:03:00'
);
INSERT INTO sync_status (record_id, table_name) VALUES
    ('018f0001-0000-7000-0000-000000000001', 'sessions'),
    ('018f0001-0000-7000-0000-000000000002', 'tests'),
    ('018f0001-0000-7000-0000-000000000003', 'measurements'),
    ('018f0001-0000-7000-0000-000000000004', 'audit_log');
'@
```

Run SyncAgent:

```powershell
dotnet run --configuration Release
```

**Expected console output after one cycle:**
```
[HH:mm:ss INF] SyncAgent starting. StationId=ST-01 SiteName=Test Factory Interval=30s
[HH:mm:ss INF] SQLite schema version OK: 1
[HH:mm:ss INF] PostgreSQL connection verified.
[HH:mm:ss INF] Cycle complete. Synced=4 Pending=0 Failed=0 DeadLetter=0
```

**Verify SQLite sync_status:**

```powershell
sqlite3 .\station.db "SELECT record_id, table_name, synced FROM sync_status;"
# Expected: all four rows show synced=1
```

**Verify PostgreSQL:**

```powershell
docker exec -it syncagent-test-postgres psql -U opcore -d testdata -c "
SELECT session_id, station_id, operator_id, status, synced_at FROM events.sessions;
SELECT test_id, station_id, verdict, synced_at FROM events.tests;
SELECT measurement_id, channel_name, value, in_limit, synced_at FROM events.measurements;
SELECT audit_id, station_id, action, synced_at FROM audit.audit_log;
"
```

Key checks:
- `station_id` on every row (SyncAgent injected this — not present in SQLite)
- `synced_at` is recent (set by PostgreSQL `DEFAULT NOW()`)
- All four tables have exactly one row each

---

## 2. Offline + Recovery

Verifies records accumulate in SQLite while PostgreSQL is unreachable, then flush on reconnect.

```powershell
docker stop syncagent-test-postgres

sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    '018f0002-0000-7000-0000-000000000001',
    'OP-002', 'WO-2026-002',
    '2026-06-05T11:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name)
VALUES ('018f0002-0000-7000-0000-000000000001', 'sessions');
'@

dotnet run --configuration Release
# Expected: PostgreSQL unreachable warning, Retry 1/10
# Ctrl+C to stop

docker start syncagent-test-postgres
Start-Sleep -Seconds 5
dotnet run --configuration Release
# Expected: Cycle complete. Synced=1 Pending=0
```

---

## 3. Retry and Backoff

Verifies `next_attempt` is respected — a record is not retried until its backoff window expires.

```powershell
# Insert a record with next_attempt far in the future
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    '018f0003-0000-7000-0000-000000000001',
    'OP-003', NULL, '2026-06-05T12:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name, synced, retry_count, next_attempt, failure_reason)
VALUES (
    '018f0003-0000-7000-0000-000000000001', 'sessions',
    0, 1, datetime('now', '+1 hour'), 'Previous simulated failure'
);
'@

dotnet run --configuration Release
# Record should NOT appear in cycle output — next_attempt is in the future

# Fast-forward next_attempt to now, then rerun
sqlite3 .\station.db "UPDATE sync_status SET next_attempt = datetime('now', '-1 second') WHERE record_id = '018f0003-0000-7000-0000-000000000001';"
# Next cycle (within 30 s): Cycle complete. Synced=1
```

---

## 4. Dead Letter

Verifies a record reaching `MaxRetries` (default: 10) is permanently moved to `synced=2`.

```powershell
# Insert record at retry 9 (one attempt from dead letter)
sqlite3 .\station.db @'
INSERT INTO sessions VALUES (
    '018f0004-0000-7000-0000-000000000001',
    'OP-004', NULL, '2026-06-05T13:00:00', NULL, 'OPEN'
);
INSERT INTO sync_status (record_id, table_name, synced, retry_count, next_attempt, failure_reason)
VALUES (
    '018f0004-0000-7000-0000-000000000001', 'sessions',
    0, 9, datetime('now', '-1 second'), 'Approaching dead letter'
);
'@

docker stop syncagent-test-postgres
dotnet run --configuration Release
# Expected: ERR DeadLetter: 018f0004-... after 10 retries

sqlite3 .\station.db "SELECT record_id, synced, retry_count FROM sync_status WHERE record_id LIKE '018f0004%';"
# Expected: synced=2, retry_count=10

Get-Content .\sync-health.json | ConvertFrom-Json
# Expected: deadLetterCount=1, postgresReachable=False
```

**Manual recovery after fixing the root cause:**

```powershell
sqlite3 .\station.db "UPDATE sync_status SET synced=0, retry_count=0, next_attempt=NULL, failure_reason=NULL WHERE record_id='018f0004-0000-7000-0000-000000000001';"
```

---

## 5. Idempotency — Duplicate Sends

```powershell
# Reset an already-synced record to pending
sqlite3 .\station.db "UPDATE sync_status SET synced=0, retry_count=0, next_attempt=NULL WHERE record_id='018f0001-0000-7000-0000-000000000001';"

dotnet run --configuration Release
# Expected: Cycle complete. Synced=1 — no error

# Verify no duplicate row in PostgreSQL
docker exec -it syncagent-test-postgres psql -U opcore -d testdata -c "
SELECT COUNT(*) FROM events.sessions WHERE session_id = '018f0001-0000-7000-0000-000000000001';
"
# Expected: count = 1
```

---

## 6. Schema Version Mismatch

```powershell
sqlite3 .\station.db "UPDATE schema_version SET version = 99;"
dotnet run --configuration Release
# Expected: ERR Schema version mismatch. SQLite=99 Expected=1. → process exits

sqlite3 .\station.db "UPDATE schema_version SET version = 1;"
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
  "stationId":         "ST-01",
  "lastCycleAt":       "2026-06-05T10:32:15.0000000+00:00",
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

---

## 8. Windows Service Install/Uninstall

> **Requires Administrator PowerShell.**

```powershell
# Publish self-contained binary
dotnet publish --configuration Release --runtime win-x64 --self-contained true -o .\publish\
Copy-Item .\syncagent.json         .\publish\
Copy-Item .\syncagent.local.json   .\publish\
Copy-Item .\scripts\install-service.ps1    .\publish\
Copy-Item .\scripts\uninstall-service.ps1  .\publish\

# Install and verify
cd .\publish\
.\install-service.ps1
sc.exe query "SyncAgent"   # STATE : 4  RUNNING

# Clean up
.\uninstall-service.ps1
```

---

## Resetting Test State

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

**PostgreSQL — wipe all synced data:**

```powershell
docker exec -it syncagent-test-postgres psql -U opcore -d testdata -c "
TRUNCATE events.measurements, events.tests, events.sessions, audit.audit_log, core.station_sync_state RESTART IDENTITY CASCADE;
"
```

**Logs:**
```powershell
Remove-Item -Recurse -Force .\logs -ErrorAction SilentlyContinue
```

---

## sync_status Reference

| `synced` value | Name | Meaning |
|---|---|---|
| 0 | Pending | Written locally, not yet pushed to PostgreSQL |
| 1 | Synced | Confirmed in PostgreSQL — will not be retried |
| 2 | DeadLetter | Exhausted all retries — requires manual review |

## Backoff Schedule

| Retry | Wait | Cumulative |
|---|---|---|
| 1 | 30 s | 30 s |
| 2 | 60 s | 1.5 min |
| 3 | 2 min | 3.5 min |
| 4 | 5 min | 8.5 min |
| 5 | 15 min | 23.5 min |
| 6–10 | 1 hr each | ~5.5 hrs total |
| 10 | DeadLetter | — |

All waits include ±10% jitter to prevent thundering herd when multiple stations reconnect simultaneously.
