# SyncAgent — LabVIEW Integration Guide

**Version:** 1.0.0  
**Audience:** LabVIEW developers setting up a test station for the first time  
**Target:** LabVIEW 2021+, Windows 10/11, NI TestStand (optional)

---

## How It Works

SyncAgent is a Windows Service that runs in the background. LabVIEW never calls it directly — both sides share a single SQLite file (`station.db`):

```
LabVIEW VI / TestStand
    │
    │  1. INSERT session / test / measurement / audit_log into station.db
    │  2. INSERT row into sync_status (synced=0) ← signals SyncAgent
    ▼
station.db  (SQLite file on disk)
    │
    │  SyncAgent polls every 30 s
    │  Reads sync_status WHERE synced=0
    │  Pushes batch to PostgreSQL
    │  Sets synced=1 on success
    ▼
PostgreSQL (central server)

SyncAgent also writes:
sync-health.json  ← LabVIEW reads this to display sync status on the front panel
```

**LabVIEW's only two jobs:**
1. Write test data to `station.db` via standard SQL
2. Read `sync-health.json` to display sync status

SyncAgent handles everything else — retry, backoff, dead-letter, PostgreSQL connection management.

---

## Prerequisites

| Item | Requirement |
|---|---|
| OS | Windows 10 / 11 (64-bit) |
| LabVIEW | 2021 SP1 or later |
| NI Database Connectivity Toolkit | Provides DB Open/Execute/Close Connection VIs — separate NI license |
| SQLite ODBC Driver (32-bit + 64-bit) | [sqliteodbc.com](http://www.ch-werner.de/sqliteodbc/) — free |
| SyncAgent installed as Windows Service | See [client-deployment.md](client-deployment.md) |

> **No Database Connectivity Toolkit?** See [Alternative: .NET Interop Node](#alternative-net-interop-node) at the end of this guide.

---

## Part 1 — One-Time Station Setup

### Step 1.1 — Create station.db

Run once per station (PowerShell):

```powershell
sqlite3 "C:\TestData\station.db" ".read sql\sqlite-syncagent.sql"
sqlite3 "C:\TestData\station.db" ".read sql\examples\sqlite-schema.example.sql"
sqlite3 "C:\TestData\station.db" "SELECT version FROM schema_version;"
# Expected output: 1
```

No `sqlite3` CLI? Use **DB Browser for SQLite**: New Database → `C:\TestData\station.db` → Execute SQL → paste `sql/sqlite-syncagent.sql`, then `sql/examples/sqlite-schema.example.sql`.

### Step 1.2 — Install the SQLite ODBC Driver

1. Download and install both `sqliteodbc.exe` (32-bit) and `sqliteodbc_w64.exe` (64-bit) from sqliteodbc.com.
2. Open **ODBC Data Sources (64-bit)** from the Start menu.
3. **System DSN** tab → **Add** → **SQLite3 ODBC Driver**.
4. Set:
   - **Data Source Name:** `StationDB`
   - **Database Name:** `C:\TestData\station.db`
5. Click **OK**.

> Match bitness to your LabVIEW installation. LabVIEW 64-bit → use the 64-bit ODBC driver and the 64-bit ODBC Administrator.

### Step 1.3 — Configure SyncAgent

Ensure `syncagent.local.json` (station-specific overrides, never committed) points to the same database:

```json
{
  "Postgres": {
    "ConnectionString": "Host=10.0.1.50;Port=5432;Database=testdata;Username=opcore;Password=<password>"
  },
  "Station": {
    "StationId": "ST-03",
    "SiteName":  "Building A"
  },
  "Sync": {
    "SQLitePath":     "C:\\TestData\\station.db",
    "HealthFilePath": "C:\\TestData\\sync-health.json"
  },
  "Logging": {
    "LogPath": "C:\\TestData\\logs"
  }
}
```

### Step 1.4 — Verify SyncAgent is Running

```powershell
sc.exe query "SyncAgent"   # STATE : 4  RUNNING
```

---

## Part 2 — LabVIEW Database Connection

### Open a connection (call once at VI startup)

```
DB Open Connection.vi
  Connection String: "DSN=StationDB"
  → Connection refnum (keep in shift register or Functional Global Variable)
```

Reuse one connection for the session — do not open/close per query.

### Close the connection (call at VI shutdown)

```
DB Close Connection.vi
  ← Connection refnum
```

---

## Part 3 — Writing Test Data

Insert order matters due to foreign keys: **session → test → measurements**. Audit log is independent.

### 3.1 — Generate a UUID

**Option A — .NET Interop Node (recommended):**
```
.NET Constructor: System.Guid  (assembly: mscorlib)
.NET Invoke Node → NewGuid() → .ToString()
```

**Option B — Timestamp-based ID (simpler, not collision-proof):**
```
Get Date/Time In Seconds → Format Into String → "sess-%016X"
```

### 3.2 — Open a Session

SubVI `SyncDB_OpenSession.vi` — inputs: `connection`, `session_id`, `operator_id`, `work_order`

```
DB Execute Query.vi
  Query: "INSERT INTO sessions (session_id, operator_id, work_order, started_at, status)
          VALUES (?, ?, ?, datetime('now'), 'OPEN')"
  Parameters: [session_id, operator_id, work_order]

DB Execute Query.vi
  Query: "INSERT INTO sync_status (record_id, table_name) VALUES (?, 'sessions')"
  Parameters: [session_id]
```

> Always insert into `sync_status` immediately after inserting into the data table. Wrap both in the same transaction (see §3.7).

### 3.3 — Start a Test

SubVI `SyncDB_StartTest.vi` — inputs: `connection`, `test_id`, `session_id`, `part_serial`, `test_name`

```
DB Execute Query.vi
  Query: "INSERT INTO tests (test_id, session_id, part_serial, test_name, started_at)
          VALUES (?, ?, ?, ?, datetime('now'))"
  Parameters: [test_id, session_id, part_serial, test_name]

DB Execute Query.vi
  Query: "INSERT INTO sync_status (record_id, table_name) VALUES (?, 'tests')"
  Parameters: [test_id]
```

### 3.4 — Record a Measurement

SubVI `SyncDB_RecordMeasurement.vi` — inputs: `connection`, `measurement_id`, `test_id`, `channel_name`, `value`, `unit`, `lower_limit`, `upper_limit`, `in_limit` (I32: 1=pass, 0=fail)

```
DB Execute Query.vi
  Query: "INSERT INTO measurements
            (measurement_id, test_id, channel_name, value, unit,
             lower_limit, upper_limit, in_limit, captured_at)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, datetime('now'))"
  Parameters: [measurement_id, test_id, channel_name, value(dbl), unit,
               lower_limit(dbl), upper_limit(dbl), in_limit(i32)]

DB Execute Query.vi
  Query: "INSERT INTO sync_status (record_id, table_name) VALUES (?, 'measurements')"
  Parameters: [measurement_id]
```

### 3.5 — Close a Test (set verdict)

```
DB Execute Query.vi
  Query: "UPDATE tests SET completed_at=datetime('now'), verdict=? WHERE test_id=?"
  Parameters: [verdict ("PASS"/"FAIL"), test_id]
```

No new `sync_status` row needed — the test was already queued when created. SyncAgent pushes the completed row (with `completed_at` and `verdict`) on its next cycle.

### 3.6 — Close a Session

```
DB Execute Query.vi
  Query: "UPDATE sessions SET closed_at=datetime('now'), status='CLOSED' WHERE session_id=?"
  Parameters: [session_id]
```

### 3.7 — Write an Audit Record

SubVI `SyncDB_Audit.vi` — inputs: `connection`, `audit_id`, `session_id`, `operator_id`, `action`, `detail`, `signature`

```
DB Execute Query.vi
  Query: "INSERT INTO audit_log
            (audit_id, session_id, operator_id, action, detail, signature, logged_at)
          VALUES (?, ?, ?, ?, ?, ?, datetime('now'))"
  Parameters: [audit_id, session_id, operator_id, action, detail, signature]

DB Execute Query.vi
  Query: "INSERT INTO sync_status (record_id, table_name) VALUES (?, 'audit_log')"
  Parameters: [audit_id]
```

### 3.8 — Transactions (recommended)

Wrap each logical unit in a transaction so `station.db` is always consistent, even if LabVIEW crashes mid-sequence.

```
DB Execute Query.vi  →  "BEGIN TRANSACTION"

  ... INSERT statements ...

DB Execute Query.vi  →  "COMMIT"

(in error handler:)
DB Execute Query.vi  →  "ROLLBACK"
```

---

## Part 4 — Complete Test Sequence Walkthrough

```
[Startup]
  DB Open Connection ("DSN=StationDB") → conn

[Begin Session]
  session_id = NewGuid()
  SyncDB_OpenSession(conn, session_id, operator_id="OP-001", work_order="WO-2026-042")

[For each part under test]

  test_id = NewGuid()
  SyncDB_StartTest(conn, test_id, session_id, part_serial="SN-001", test_name="Voltage Check")

    m1_id = NewGuid()
    SyncDB_RecordMeasurement(conn, m1_id, test_id,
        channel="CH1_VOLTAGE", value=12.04, unit="V", low=11.5, high=12.5, in_limit=1)

    m2_id = NewGuid()
    SyncDB_RecordMeasurement(conn, m2_id, test_id,
        channel="CH1_CURRENT", value=1.98, unit="A", low=1.8, high=2.2, in_limit=1)

  verdict = all in_limit ? "PASS" : "FAIL"
  SyncDB_CloseTest(conn, test_id, verdict)

  audit_id = NewGuid()
  SyncDB_Audit(conn, audit_id, session_id, "OP-001",
      action="TEST_COMPLETE", detail="Part SN-001: " + verdict)

[End Session]
  SyncDB_CloseSession(conn, session_id)
  audit_id = NewGuid()
  SyncDB_Audit(conn, audit_id, session_id, "OP-001", action="SESSION_CLOSED", detail="")

[Shutdown]
  DB Close Connection(conn)
```

**Verify sync within ~30 seconds:**

```powershell
Get-Content "C:\TestData\sync-health.json" | ConvertFrom-Json
# pendingCount: 0   postgresReachable: true
```

---

## Part 5 — Displaying Sync Status on the Front Panel

### 5.1 — Create a SyncStatus cluster type def (`SyncStatus.ctl`)

| Name | Type |
|---|---|
| Server Connected | Boolean |
| Pending Count | I32 |
| Dead Letter Count | I32 |
| Last Synced | String |
| Agent Version | String |

### 5.2 — ReadSyncHealth SubVI

`ReadSyncHealth.vi` — input: `health_file_path` (string), output: `SyncStatus` cluster

```
Read Text File (health_file_path) → json_string

Unflatten From JSON (json_string, SyncStatus type def) → SyncStatus cluster

(Error handler: if file not found → return default cluster with Server Connected=FALSE)
```

> Health file uses camelCase (`pendingCount`, `postgresReachable`). Match exactly in LabVIEW type def.

### 5.3 — Timed polling loop

Place a **Timed Loop** (or While Loop + Elapsed Time Express VI) calling `ReadSyncHealth.vi` every 15 seconds. Wire the cluster to front-panel indicators.

**Recommended front-panel indicators:**

```
┌─────────────────────────────────────┐
│  SYNC STATUS                        │
│  ● Server Connected  [LED: green]   │
│  Pending:   [0     ] records        │
│  Dead Letters: [0  ] (needs review) │
│  Last Synced: [10:32:14 UTC]        │
└─────────────────────────────────────┘
```

**Alert logic:**
```
IF Dead Letter Count > 0 THEN
    Show Dialog: "Sync dead letters detected — check C:\TestData\logs\"
END IF

IF Server Connected = FALSE THEN
    Set indicator: "OFFLINE — data buffered locally"
END IF
```

### Health file fields reference

| Field | Type | Meaning |
|---|---|---|
| `stationId` | string | Confirms which station wrote the file |
| `lastCycleAt` | ISO 8601 UTC | When SyncAgent last ran a cycle |
| `lastSyncedAt` | ISO 8601 UTC or null | When a record was last successfully pushed |
| `pendingCount` | int | Records waiting to be pushed (0 = fully caught up) |
| `deadLetterCount` | int | Records that exhausted retries — require manual action |
| `postgresReachable` | bool | Whether PostgreSQL was reachable last cycle |
| `agentVersion` | string | SyncAgent binary version |

---

## Part 6 — TestStand Integration

| TestStand Step | Action | SubVI |
|---|---|---|
| Setup → ProcessSetup | Open DB connection, open session | `SyncDB_OpenSession.vi` |
| Main → PreUUT | Start test record | `SyncDB_StartTest.vi` |
| Main → each measurement step | Record measurement | `SyncDB_RecordMeasurement.vi` |
| Main → PostUUT | Close test (set verdict) | `SyncDB_CloseTest.vi` |
| Cleanup → ProcessCleanup | Close session, close DB connection | `SyncDB_CloseSession.vi` |
| Any step (on operator action) | Write audit event | `SyncDB_Audit.vi` |

Pass `connection` refnum and `session_id` through TestStand **Locals** or **Sequence Globals**.

---

## Part 7 — Confirming the Full Flow

**1 — Check station.db (PowerShell or DB Browser)**

```powershell
sqlite3 "C:\TestData\station.db" @'
SELECT s.session_id, s.status, ss.synced
FROM sessions s
JOIN sync_status ss ON ss.record_id = s.session_id AND ss.table_name='sessions'
ORDER BY s.started_at DESC LIMIT 5;
'@
# Expected: synced = 1 on all rows after SyncAgent runs
```

**2 — Check PostgreSQL**

```powershell
docker exec -it <postgres-container> psql -U opcore -d testdata -c "
SELECT session_id, station_id, operator_id, status, synced_at
FROM events.sessions ORDER BY synced_at DESC LIMIT 5;
"
# station_id should match StationId from syncagent.local.json (injected by SyncAgent)
# synced_at is set by PostgreSQL DEFAULT NOW() — not by LabVIEW
```

**3 — Check health file**

```powershell
Get-Content "C:\TestData\sync-health.json" | ConvertFrom-Json
# Expected: pendingCount=0, deadLetterCount=0, postgresReachable=true
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `DB Open Connection` error | ODBC DSN not found or wrong bitness | Re-check ODBC Data Source Admin — use 64-bit admin for 64-bit LabVIEW |
| Inserts succeed but `synced` stays 0 | SyncAgent not running | `sc.exe query SyncAgent` — start if stopped |
| `synced=1` but no row in PostgreSQL | Wrong `StationId` or connection string | Check `syncagent.local.json` |
| Measurement insert fails with FK error | `test_id` not yet committed | Ensure sessions/tests INSERT commits before measurements (use transactions) |
| `Unflatten From JSON` returns defaults | Field name case mismatch | Health file uses camelCase — match exactly in LabVIEW type def |
| `sync-health.json` not found | SyncAgent hasn't completed its first cycle | Wait up to `IntervalSeconds` (default 30 s) after service starts |

### Dead-letter recovery

When a record reaches `synced=2` it is frozen. After fixing the root cause:

```powershell
sqlite3 "C:\TestData\station.db" @'
UPDATE sync_status
SET synced=0, retry_count=0, next_attempt=NULL, failure_reason=NULL
WHERE synced = 2;
'@
```

Then restart SyncAgent — it re-attempts all recovered records on the next cycle.

---

## Multi-Station Deployment

Each station has its own `station.db` and its own SyncAgent service with a **unique** `StationId`. All push to the same PostgreSQL server.

```
Station ST-01  ──► SyncAgent (StationId=ST-01) ──┐
Station ST-02  ──► SyncAgent (StationId=ST-02) ──┼──► PostgreSQL
Station ST-03  ──► SyncAgent (StationId=ST-03) ──┘
```

`station_id` is injected by SyncAgent at push time — it is not stored in `station.db`. Central queries can filter by `station_id` across all tables.

---

## AS9100D Compliance Notes

SyncAgent preserves the two-timestamp pattern required for aerospace audits:

| Timestamp | Set by | Meaning |
|---|---|---|
| `started_at` / `captured_at` / `logged_at` | Station clock | When the measurement or action occurred |
| `synced_at` | PostgreSQL `DEFAULT NOW()` | When the record entered the central system |

SyncAgent **never sends `synced_at`** — PostgreSQL sets it at insert time. This means:

- Station clock drift cannot retroactively alter the central receipt timestamp.
- Auditors see measurement time and central receipt time independently.
- No record is ever silently discarded — dead-letter records remain in `sync_status` with `failure_reason` visible.

---

## Deployment Checklist

- [ ] `station.db` created with `sql/sqlite-syncagent.sql` (`schema_version = 1`)
- [ ] Example application tables applied via `sql/examples/sqlite-schema.example.sql`
- [ ] `sql/examples/postgres-schema.example.sql` applied to the central PostgreSQL server
- [ ] `syncagent.local.json` created on station with unique `StationId`, correct connection string
- [ ] `Sync.SQLitePath` matches the path LabVIEW writes to
- [ ] `Sync.HealthFilePath` matches the path LabVIEW reads
- [ ] SyncAgent published self-contained (`--self-contained true`)
- [ ] Installed as Windows Service (`start= auto`)
- [ ] Service recovery policy set (restart on failure)
- [ ] `sc.exe query SyncAgent` shows `STATE : 4  RUNNING`
- [ ] Log file shows `SQLite schema version OK: 1` and `PostgreSQL connection verified.`
- [ ] Health file shows `postgresReachable: true` after first cycle
- [ ] LabVIEW front panel polls `sync-health.json` and displays status

---

## Alternative: .NET Interop Node

If you do not have the NI Database Connectivity Toolkit, you can use `Microsoft.Data.Sqlite.dll` directly from LabVIEW via the **.NET Interop Node**. The DLL ships in SyncAgent's published output folder.

### Setup

1. Place a **.NET Constructor Node** on your block diagram.
2. Assembly: browse to `C:\Program Files\SyncAgent\Microsoft.Data.Sqlite.dll`
3. Class: `Microsoft.Data.Sqlite.SqliteConnection`
4. Constructor argument: `"Data Source=C:\\TestData\\station.db"`

Use **.NET Invoke Node** to call `Open()`, `CreateCommand()`, and `ExecuteNonQuery()` as you would in C#. The SQL strings and parameter patterns are identical to the ODBC examples above.

> The .NET Interop Node approach is slightly more verbose in LabVIEW but avoids the ODBC driver installation and licensing dependencies.
