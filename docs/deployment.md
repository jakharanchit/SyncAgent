# SyncAgent — Deployment Guide

**Version 1.1.0 · Windows x64**

SyncAgent is a background service that continuously synchronises your station's local SQLite database to the central PostgreSQL server. Once installed it requires no interaction — it starts with Windows, recovers from network outages automatically, and writes a health status file your application can read at any time.

---

## Before You Start

You will need:

- The delivery package: `SyncAgent-v1.1.0-win-x64-selfcontained.zip`
- Administrator access on the station machine
- The PostgreSQL connection string for your central server (host, port, database name, username, password)
- The full path to the station's SQLite database file

The package includes `sqlite3.exe` for the database setup steps below. You do not need to download anything separately.

---

## 1. Extract the Package

Unzip the delivery package to a permanent location on the station machine. We recommend:

```
C:\Program Files\SyncAgent\
```

The folder contains:

```
SyncAgent.exe
sqlite3.exe
syncagent.json
syncagent.example.json
install-service.ps1
uninstall-service.ps1
sql\
    sqlite-syncagent.sql
    examples\
        sqlite-schema.example.sql
        postgres-schema.example.sql
ABOUT_THESE_FILES.txt
```

---

## 2. Prepare the SQLite Database

SyncAgent adds two tables to your station database — `sync_status` and `schema_version` — and enables WAL journal mode so SyncAgent and your application can write to the database at the same time without blocking each other.

Run this once per station (PowerShell):

```powershell
& "C:\Program Files\SyncAgent\sqlite3.exe" "C:\<path-to-your>\station.db" ".read `"C:\Program Files\SyncAgent\sql\sqlite-syncagent.sql`""
```

Confirm it worked:

```powershell
& "C:\Program Files\SyncAgent\sqlite3.exe" "C:\<path-to-your>\station.db" "SELECT version FROM schema_version;"
# Expected: 1

& "C:\Program Files\SyncAgent\sqlite3.exe" "C:\<path-to-your>\station.db" "PRAGMA journal_mode;"
# Expected: wal
```

> **Safe to re-run.** The script uses `CREATE TABLE IF NOT EXISTS` and will not touch existing data. SyncAgent also enables WAL mode automatically on every startup, so an existing database that was not set up with this script will be migrated on first run.

---

## 3. Configure syncagent.json

Open `C:\Program Files\SyncAgent\syncagent.json` in any text editor and fill in your values. A fully annotated example with all options is in `syncagent.example.json` alongside it.

### Minimum required changes

```json
"Station": {
  "StationId": "ST-01",
  "SiteName":  "Building A — Bay 3"
},

"Postgres": {
  "ConnectionString": "Host=<server>;Port=5432;Database=<dbname>;Username=<user>;Password=<password>"
},

"Sync": {
  "SQLitePath":     "C:\\<path-to-your>\\station.db",
  "HealthFilePath": "C:\\<path-to-your>\\sync-health.json"
},

"Logging": {
  "LogPath": "C:\\<path-to-your>\\logs"
}
```

`StationId` is stamped onto every record pushed to the central server and must be unique across all stations writing to the same database (e.g. `ST-01`, `ST-02`, `ST-03`).

### Table mappings

Add one entry to the `Tables` array for each SQLite table you want synced. This is the only configuration that controls what data moves and where it lands.

```json
"Tables": [
  {
    "SourceTable":      "your_table",
    "TargetTable":      "schema.your_table",
    "PrimaryKey":       "your_id_column",
    "InjectStationId":  true,
    "TimestampColumns": ["created_at"],
    "BooleanColumns":   []
  }
]
```

| Field | What it does |
|---|---|
| `SourceTable` | Table name in the station SQLite database |
| `TargetTable` | Destination on PostgreSQL, as `schema.table` |
| `PrimaryKey` | Primary key column — used to prevent duplicate inserts on retry |
| `InjectStationId` | When `true`, adds `station_id` (from `Station.StationId`) to every insert |
| `TimestampColumns` | SQLite TEXT columns that map to `TIMESTAMPTZ` on PostgreSQL |
| `BooleanColumns` | SQLite `0`/`1` INTEGER columns that map to `BOOLEAN` on PostgreSQL |

To add a table later — with no redeployment — append an entry here and restart the service.

---

## 4. Install the Windows Service

Open PowerShell **as Administrator**, then:

```powershell
cd "C:\Program Files\SyncAgent"
.\install-service.ps1
```

Expected output:
```
[SC] CreateService SUCCESS
[SC] ChangeServiceConfig2 SUCCESS
SyncAgent service installed and started.
STATE : 4  RUNNING
```

SyncAgent is now running and will start automatically at every boot. The service is configured to restart itself if it exits unexpectedly (up to 3 times with a 60-second delay between attempts).

---

## 5. Verify the Installation

```powershell
sc.exe query "SyncAgent"
# STATE : 4  RUNNING
```

Check the log file for a clean startup:

```powershell
Get-ChildItem "C:\<path-to-your>\logs\" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content -Tail 20
```

A healthy startup looks like:

```
[INF] SyncAgent starting. StationId=ST-01 SiteName=Building A — Bay 3 Interval=30s
[INF] SQLite schema version OK: 1
[INF] Table mappings loaded: your_table→schema.your_table
[INF] PostgreSQL connection verified.
[INF] SyncAgent startup complete. StationId=ST-01 SiteName=Building A — Bay 3
```

Check the health file:

```powershell
Get-Content "C:\<path-to-your>\sync-health.json" | ConvertFrom-Json
```

```json
{
  "postgresReachable": true,
  "pendingCount":      0,
  "deadLetterCount":   0
}
```

---

## Service Management

| Action | Command |
|---|---|
| Start | `sc.exe start SyncAgent` |
| Stop | `sc.exe stop SyncAgent` |
| Restart | `sc.exe stop SyncAgent` then `sc.exe start SyncAgent` |
| Status | `sc.exe query SyncAgent` |
| Live logs | `Get-Content "C:\<path>\logs\syncagent-<date>.log" -Wait -Tail 50` |
| Uninstall | `cd "C:\Program Files\SyncAgent"` then `.\uninstall-service.ps1` |

---

## Updating to a New Version

1. Stop the service: `sc.exe stop SyncAgent`
2. Uninstall: `.\uninstall-service.ps1`
3. Replace all files in the install folder with the new package contents
4. Keep your existing `syncagent.json` — check the release notes for any new fields
5. Reinstall: `.\install-service.ps1`

---

## Troubleshooting

**Service fails to start**
Check the log file first. The most common causes:
- `SQLitePath` does not exist — create and initialise the database (Step 2)
- `schema_version` table missing — the SQLite migration script was not run, or was run against a different database file
- PostgreSQL connection string is wrong — the service starts anyway and retries, so check `sync-health.json` for `postgresReachable: false`

**`postgresReachable: false` in health file**
Records are not lost — SyncAgent continues to buffer them in SQLite and will flush automatically when connectivity is restored. To diagnose:
- Confirm the station can reach the PostgreSQL server: `Test-NetConnection <host> -Port 5432`
- Check firewall rules and VPN connection if applicable
- Verify the connection string has the correct host, port, database name, and credentials

**`pendingCount` keeps growing**
This is normal offline-operation behaviour. Records accumulate in SQLite while the server is unreachable. Once connectivity is restored SyncAgent flushes the backlog automatically, oldest records first.

**Dead-letter records (`deadLetterCount > 0`)**
A record failed to sync after 10 attempts and has been frozen to prevent infinite retries. The `failure_reason` column in `sync_status` shows the specific error. After fixing the root cause:

```sql
UPDATE sync_status
SET    synced = 0, retry_count = 0, next_attempt = NULL, failure_reason = NULL
WHERE  synced = 2;
```

Then restart the service:
```powershell
sc.exe stop SyncAgent
sc.exe start SyncAgent
```

**A table is not syncing**
Confirm the table has an entry in the `Tables` array in `syncagent.json` and that `SourceTable` matches the SQLite table name exactly (case-insensitive). Restart the service after any configuration change.

**Multi-station setup**
Each station has its own `station.db` and its own SyncAgent installation with a unique `StationId`. All stations push to the same PostgreSQL server. The `station_id` column on every PostgreSQL row identifies which station the record came from.
