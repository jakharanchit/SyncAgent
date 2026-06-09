# SyncAgent — LabVIEW Integration Guide

**Version 1.1.0 · LabVIEW 2019 SP1+, Windows 10/11**

This guide shows how to write data from LabVIEW to a local SQLite database so SyncAgent can pick it up and push it to the central PostgreSQL server.

LabVIEW never talks to PostgreSQL directly. The only interface between LabVIEW and SyncAgent is two things on disk:

| File | Written by | Read by |
|---|---|---|
| `station.db` | LabVIEW | SyncAgent |
| `sync-health.json` | SyncAgent | LabVIEW (optional, for status display) |

---

## Prerequisites

### LabVIEW libraries (install via VIPM)

Open **VI Package Manager**, search for and install both packages. Run VIPM as Administrator — the packages copy native DLLs into the LabVIEW directory.

| Package name in VIPM | What it provides |
|---|---|
| `SQLite Library by JDP Science` (`drjdpowell_lib_sqlite_labview`) | SQLite access — no ODBC driver, no NI Database Toolkit license |
| `PostgreSQL Library by JDP Science` (`jdp_science_postgresql`) | PostgreSQL access — no ODBC driver (read-back only, optional) |
| `JDP Science Common Utilities` | Required dependency of the PostgreSQL library |

Both palettes appear under **Functions → Addons → JDP Science** after installation.

### SyncAgent

Install SyncAgent as a Windows Service before testing. See [`deployment.md`](deployment.md) for the full walkthrough. Confirm it is running:

```powershell
sc.exe query SyncAgent
# STATE : 4  RUNNING
```

---

## How LabVIEW writes to station.db

LabVIEW's only job is to write data to its own SQLite tables and add one row to `sync_status` for each record it wants synced. SyncAgent does the rest.

### The contract — two steps per record

```
1.  INSERT INTO your_table (...) VALUES (...)
2.  INSERT OR IGNORE INTO sync_status (record_id, table_name) VALUES (<primary_key>, '<table_name>')
```

That's it. No polling, no callbacks, no direct PostgreSQL connection from LabVIEW.

---

## SQLite palette — key VIs

| VI | Palette path | What it does |
|---|---|---|
| `Open Connection.vi` | JDP Science → SQLite | Opens `station.db` by file path. Returns a Connection refnum. |
| `Execute SQL.vi` | JDP Science → SQLite | Runs a SQL string with typed parameters. |
| `BEGIN.vi` | JDP Science → SQLite | Issues `BEGIN TRANSACTION`. |
| `COMMIT (Rollback on error).vi` | JDP Science → SQLite | Commits, or rolls back if the error wire carries an error. |
| `Close Connection.vi` | JDP Science → SQLite | Closes the connection and releases the file lock. |

**Parameter placeholders:** use `?` (positional). Pass a Bundle cluster of values matching the `?` order. A single value can be passed as a bare string or number without a cluster.

**Open one connection, keep it open** for the duration of a test sequence. Do not open and close per query.

---

## Minimal example — write one record and queue it for sync

This is the simplest possible integration. Adapt the table name, columns, and primary key to your own schema.

### Block diagram flow

```
DB Path (String Control)
    │
Open Connection.vi ──► Connection (shift register)
    │
Execute SQL.vi
  SQL: "INSERT INTO your_table (id, operator, value, recorded_at)
        VALUES (?, ?, ?, datetime('now'))"
  Parameters: Bundle { id (Str), operator (Str), value (DBL) }
    │
Execute SQL.vi
  SQL: "INSERT OR IGNORE INTO sync_status (record_id, table_name)
        VALUES (?, 'your_table')"
  Parameters: id (Str)
    │
Close Connection.vi
```

Wire the **error out** of each VI into the **error in** of the next. Add a **Simple Error Handler** at the end so any failure pops a dialog.

### UUID generation

LabVIEW has no built-in UUID generator. Use the .NET Interop Node:

```
.NET Constructor Node
  Assembly: mscorlib
  Class:    System.Guid
      │
.NET Invoke Node → NewGuid()
      │
.NET Invoke Node → ToString()  ──► id string
```

Call this once per record to get a unique primary key.

---

## Using transactions (recommended)

Wrap each logical write in a transaction so `station.db` is always consistent if LabVIEW crashes mid-sequence:

```
Open Connection.vi
    │
BEGIN.vi
    │
Execute SQL.vi  (INSERT your data)
Execute SQL.vi  (INSERT sync_status)
    │
COMMIT (Rollback on error).vi
    │
Close Connection.vi
```

If any `Execute SQL.vi` raises an error, the error wire flows forward and `COMMIT (Rollback on error).vi` automatically issues `ROLLBACK` instead of `COMMIT`.

---

## Displaying sync status (optional)

SyncAgent writes `sync-health.json` after every cycle. LabVIEW can read this file to display live sync status on the front panel — no database connection needed.

### sync-health.json fields

| Field | Type | Meaning |
|---|---|---|
| `postgresReachable` | bool | Whether PostgreSQL was reachable on the last sync attempt |
| `pendingCount` | int | Records queued but not yet pushed (0 = fully caught up) |
| `deadLetterCount` | int | Records that exhausted all retries — require manual action |
| `lastSyncedAt` | string (ISO 8601 UTC) or null | When a record was last successfully pushed |
| `lastCycleAt` | string (ISO 8601 UTC) | When SyncAgent last ran a cycle |
| `agentVersion` | string | SyncAgent version |

### Reading the file in LabVIEW

```
Read Text File ──► health_file_path ──► json_string
Unflatten From JSON ──► (json_string, SyncStatus.ctl) ──► SyncStatus cluster
```

**SyncStatus.ctl** — cluster element names must match the JSON keys exactly (camelCase):

| Element Name | Type | JSON key |
|---|---|---|
| postgresReachable | Boolean | `postgresReachable` |
| pendingCount | I32 | `pendingCount` |
| deadLetterCount | I32 | `deadLetterCount` |
| lastSyncedAt | String | `lastSyncedAt` |
| agentVersion | String | `agentVersion` |

Place the read inside a **Timed Loop** (15 000 ms is a reasonable interval). Wrap the Read in an error case — if the file does not exist yet (SyncAgent hasn't completed its first cycle), return a default cluster with `postgresReachable = FALSE`.

---

## Reading synced data from PostgreSQL (optional)

To query what arrived in PostgreSQL, use the **JDP Science PostgreSQL Library**.

| VI | Palette path | What it does |
|---|---|---|
| `Connect.vi` | JDP Science → PostgreSQL | Opens a connection using a libPQ connection string. |
| `Execute.vi` | JDP Science → PostgreSQL | Runs a SQL string and returns a Result refnum. |
| `Get Column.vi` | JDP Science → PostgreSQL | Extracts a typed 1D array for one column from a Result. |
| `Disconnect.vi` | JDP Science → PostgreSQL | Closes the connection. |

**Connection string format** (libPQ keyword=value, space-separated — not the semicolon ADO format):

```
host=<server> port=5432 dbname=<dbname> user=<user> password=<password>
```

**Parameter placeholders:** use `$1`, `$2`, ... (libPQ style — different from SQLite's `?`).

### Example — query the last 10 synced records

```
Connect.vi ← connection string
    │
Execute.vi
  SQL: "SELECT id, operator, value, synced_at
        FROM schema.your_table
        WHERE station_id = $1
        ORDER BY synced_at DESC LIMIT 10"
  Parameters: cluster { station_id (Str) }
  → Result
    │
Get Column.vi col=0 → id[]        (1D String array)
Get Column.vi col=1 → operator[]
Get Column.vi col=2 → value[]
Get Column.vi col=3 → synced_at[]
    │
Build 2D Array (transpose) → wire to Multicolumn Listbox
    │
Disconnect.vi
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `Open Connection.vi` error | File path wrong or `station.db` not initialised | Verify path; run `sqlite-syncagent.sql` if `sync_status` table is missing |
| `SQLITE_BUSY` on INSERT | WAL mode not set on the database | Run `PRAGMA journal_mode=WAL;` once, or let SyncAgent set it on first startup |
| INSERT succeeds but `synced` stays 0 | SyncAgent not running | `sc.exe start SyncAgent` |
| `synced = 1` but no row in PostgreSQL | `StationId` mismatch or table not in `Tables` config | Check `syncagent.json` — `Station.StationId` and `Tables` array |
| `Connect.vi` (PostgreSQL) error | Wrong connection string format | Use libPQ format: `host=... port=... dbname=... user=... password=...` |
| `Unflatten From JSON` returns all defaults | Cluster element name case mismatch | JSON keys are camelCase — match exactly in `SyncStatus.ctl` |
| `sync-health.json` not found | SyncAgent hasn't completed its first cycle | Wait up to `IntervalSeconds` (default 30 s) after service start |
