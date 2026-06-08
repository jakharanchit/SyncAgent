# SyncAgent — Deployment Guide

**Version 1.0.0 · Windows x64**

SyncAgent is a background service that continuously synchronises your station's local database to the central server. Once installed it requires no interaction — it starts with Windows, recovers from network outages automatically, and writes a health status file your application can read at any time.

---

## Before You Start

You will need:

- The delivery package: `SyncAgent-v1.0.0-win-x64-selfcontained.zip`
- Administrator access on the station machine
- The PostgreSQL connection string for your central server
- The full path to the station's SQLite database file

For the schema migration steps, you also need `sqlite3.exe`. If it is not already on the machine, download the `sqlite-tools-win-x64` zip from [sqlite.org/download](https://www.sqlite.org/download.html) and extract `sqlite3.exe` anywhere on the PATH.

---

## 1. Extract the Package

Unzip the delivery package to a permanent location on the station machine. We recommend:

```
C:\Program Files\SyncAgent\
```

The folder should contain:

```
SyncAgent.exe
syncagent.json
install-service.ps1
uninstall-service.ps1
sql\
    sqlite-syncagent.sql
    examples\
```

---

## 2. Prepare the SQLite Database

SyncAgent adds two tables to your station database — `sync_status` and `schema_version`. Run this once per station:

```powershell
sqlite3 "C:\<path-to-your>\station.db" ".read `"C:\Program Files\SyncAgent\sql\sqlite-syncagent.sql`""
```

Confirm it worked:

```powershell
sqlite3 "C:\<path-to-your>\station.db" "SELECT version FROM schema_version;"
```

Expected output: `1`

> This script is safe to re-run. It uses `CREATE TABLE IF NOT EXISTS` throughout and will not modify existing data.

---

## 3. Configure syncagent.json

Open `C:\Program Files\SyncAgent\syncagent.json` in any text editor and fill in the fields below. A fully-annotated example is in `syncagent.example.json` alongside it.

### Connection and paths

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

`StationId` is stamped onto every record pushed to the central server, so it must be unique across all stations writing to the same database (e.g. `ST-01`, `ST-02`).

### Table mappings

Add one entry per SQLite table you want synced. This is the only configuration that affects what data moves and where it lands.

```json
"Tables": [
  {
    "SourceTable":      "sessions",
    "TargetTable":      "events.sessions",
    "PrimaryKey":       "session_id",
    "InjectStationId":  true,
    "TimestampColumns": ["started_at", "closed_at"],
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

SyncAgent is now running and will start automatically at every boot. The service is configured to restart itself if it exits unexpectedly.

---

## 5. Verify the Installation

```powershell
# Confirm the service is running
sc.exe query "SyncAgent"
```

Then check the log file for a clean startup (adjust the date):

```powershell
Get-Content "C:\<path-to-your>\logs\syncagent-2026-01-01.log" -Tail 20
```

A healthy startup looks like:

```
[INF] SyncAgent starting. StationId=ST-01 SiteName=Building A — Bay 3 Interval=30s
[INF] SQLite schema version OK: 1
[INF] Table mappings loaded: sessions→events.sessions
[INF] PostgreSQL connection verified.
[INF] SyncAgent startup complete.
```

And the health file:

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

## Troubleshooting

**Service fails to start**
Check the log file first. The most common causes are an incorrect `SQLitePath` (file does not exist at the configured path) or a missing `schema_version` table (the SQLite migration in step 2 was not run on this database).

**`postgresReachable: false` in health file**
SyncAgent will continue retrying — records are not lost. Confirm the station can reach the PostgreSQL server on port 5432. Check firewall rules and the VPN connection if applicable. Verify the connection string in `syncagent.json` has the correct hostname, port, and credentials.

**`pendingCount` keeps growing**
This is the offline-operation mode — records are accumulating in SQLite while the server is unreachable. Once connectivity is restored SyncAgent will flush the backlog automatically, oldest records first.

**Dead-letter records (`deadLetterCount > 0`)**
A record has failed to sync after 10 attempts and has been frozen. The `failure_reason` column in the station's `sync_status` table shows the specific error. After resolving the underlying issue, reset the record and restart:

```sql
UPDATE sync_status
SET    synced = 0, retry_count = 0, next_attempt = NULL, failure_reason = NULL
WHERE  synced = 2;
```

```powershell
sc.exe stop SyncAgent
sc.exe start SyncAgent
```

**A table is not syncing**
Confirm the table has an entry in the `Tables` array in `syncagent.json` and that the `SourceTable` name matches exactly. Restart the service after any configuration change.
