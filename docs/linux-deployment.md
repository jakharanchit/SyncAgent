# SyncAgent — Linux Deployment Guide

**Version:** 1.0.0

---

## The Short Answer

SyncAgent already supports Linux. The same source code and the same build pipeline produces a binary that runs as a **systemd service** on Linux without any code changes.

`Program.cs` wires both lifetimes together:

```csharp
.UseWindowsService(o => o.ServiceName = "SyncAgent")   // no-op on Linux
.UseSystemd()                                                   // no-op on Windows
```

Each call is silently ignored on the OS it does not apply to. You publish for the target platform, drop the binary on the machine, and register a systemd unit — no platform-specific code paths in the service itself.

---

## When Would You Run SyncAgent on Linux?

| Scenario | Description |
|---|---|
| **Linux test station** | Station software (Python, C++, custom) runs on Linux, writes to `station.db`, SyncAgent syncs it |
| **Edge gateway / Raspberry Pi** | A low-cost ARM Linux device aggregates data from one or more station databases and pushes to PostgreSQL |
| **Docker container** | SyncAgent runs in a container alongside other services; `station.db` is a mounted volume |
| **Linux server** | SyncAgent runs on a server that reads a `station.db` served over NFS from a Windows station |

> LabVIEW itself is Windows-only. If your test station runs LabVIEW, see [labview.md](labview.md) — SyncAgent runs on the same Windows machine as a Windows Service. This guide is for Linux stations and non-LabVIEW setups.

---

## Step 1 — Publish for Linux

On any machine with the .NET 8 SDK (Windows or Linux):

```bash
# x64 (most servers, desktops)
dotnet publish --configuration Release \
               --runtime linux-x64 \
               --self-contained true \
               -o ./publish-linux/

# ARM64 (Raspberry Pi 4/5, AWS Graviton, Apple Silicon VMs)
dotnet publish --configuration Release \
               --runtime linux-arm64 \
               --self-contained true \
               -o ./publish-linux-arm64/

# ARM32 (Raspberry Pi 2/3)
dotnet publish --configuration Release \
               --runtime linux-arm \
               --self-contained true \
               -o ./publish-linux-arm/
```

Self-contained means no .NET runtime installation is needed on the target machine.

---

## Step 2 — Copy to the Target Machine

```bash
scp -r ./publish-linux/ user@station-host:/opt/syncagent/
```

Or copy via USB / network share and place at:

```
/opt/syncagent/
    SyncAgent              ← the executable (no .exe extension on Linux)
    syncagent.json         ← template (from source control)
    syncagent.local.json   ← station-specific overrides (never committed)
    Microsoft.Data.Sqlite.dll
    <all other runtime files>
```

Make the binary executable:

```bash
chmod +x /opt/syncagent/SyncAgent
```

---

## Step 3 — Configure for Linux

`syncagent.json` ships with Windows placeholder paths. Create `syncagent.local.json` on the station to override them with Linux paths:

```json
{
  "Postgres": {
    "ConnectionString": "Host=10.0.1.50;Port=5432;Database=testdata;Username=opcore;Password=<password>"
  },
  "Station": {
    "StationId": "ST-LX-01",
    "SiteName":  "Building A"
  },
  "Sync": {
    "SQLitePath":     "/var/testdata/station.db",
    "HealthFilePath": "/var/testdata/sync-health.json"
  },
  "Logging": {
    "LogPath": "/var/log/syncagent"
  }
}
```

Alternatively, use `SYNCAGENT_` environment variables (useful in Docker/CI):

```bash
export SYNCAGENT_Postgres__ConnectionString="Host=10.0.1.50;..."
export SYNCAGENT_Station__StationId="ST-LX-01"
```

Create the data and log directories:

```bash
sudo mkdir -p /var/testdata /var/log/syncagent
sudo chown opcore:opcore /var/testdata /var/log/syncagent
```

---

## Step 4 — Prepare the SQLite Database

```bash
# Install sqlite3 CLI if not present
sudo apt install sqlite3          # Debian/Ubuntu
sudo dnf install sqlite           # Fedora/RHEL

# Create station.db with the correct schema
sqlite3 /var/testdata/station.db < sql/sqlite-syncagent.sql

# Verify
sqlite3 /var/testdata/station.db "SELECT version FROM schema_version;"
# Expected: 1
```

---

## Step 5 — Create a Dedicated System User

Run SyncAgent as a non-root user with access only to what it needs:

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin opcore
sudo chown -R opcore:opcore /opt/syncagent/
sudo chown opcore:opcore /var/testdata/station.db
```

---

## Step 6 — Install as a systemd Service

Create the unit file:

```bash
sudo nano /etc/systemd/system/syncagent.service
```

Paste:

```ini
[Unit]
Description=SyncAgent
Documentation=https://github.com/your-org/SyncAgent
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=opcore
Group=opcore
WorkingDirectory=/opt/syncagent
ExecStart=/opt/syncagent/SyncAgent
Restart=on-failure
RestartSec=10s

# Logging — redirect to journald (SyncAgent also writes its own file)
StandardOutput=journal
StandardError=journal
SyslogIdentifier=syncagent

# Hardening
NoNewPrivileges=true
ProtectSystem=full
ReadWritePaths=/var/testdata /var/log/syncagent

[Install]
WantedBy=multi-user.target
```

Enable and start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable  syncagent   # start at boot
sudo systemctl start   syncagent
sudo systemctl status  syncagent
```

Expected status output:
```
● syncagent.service - SyncAgent
     Loaded: loaded (/etc/systemd/system/syncagent.service; enabled)
     Active: active (running) since ...
```

---

## Step 7 — Verify It Is Syncing

Check the journal (live log):

```bash
journalctl -u syncagent -f
```

Expected output after first cycle:
```
[HH:mm:ss INF] SyncAgent starting. StationId=ST-LX-01 SiteName=Building A Interval=30s
[HH:mm:ss INF] SQLite schema version OK: 1
[HH:mm:ss INF] PostgreSQL connection verified.
```

Check the health file:

```bash
cat /var/testdata/sync-health.json
```

---

## Writing to station.db from Linux Station Software

Any process that can write SQLite can act as the station software. Below are examples for the most common languages on Linux test stations.

### Python

```python
import sqlite3
import uuid

DB_PATH = "/var/testdata/station.db"

def open_session(operator_id: str, work_order: str | None = None) -> str:
    session_id = str(uuid.uuid4())
    with sqlite3.connect(DB_PATH) as conn:
        conn.execute(
            "INSERT INTO sessions (session_id, operator_id, work_order, started_at, status) "
            "VALUES (?, ?, ?, datetime('now'), 'OPEN')",
            (session_id, operator_id, work_order)
        )
        conn.execute(
            "INSERT INTO sync_status (record_id, table_name) VALUES (?, 'sessions')",
            (session_id,)
        )
        conn.commit()
    return session_id

def record_measurement(test_id: str, channel: str, value: float,
                        unit: str, low: float, high: float) -> str:
    m_id = str(uuid.uuid4())
    in_limit = 1 if low <= value <= high else 0
    with sqlite3.connect(DB_PATH) as conn:
        conn.execute(
            "INSERT INTO measurements "
            "(measurement_id, test_id, channel_name, value, unit, "
            " lower_limit, upper_limit, in_limit, captured_at) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, datetime('now'))",
            (m_id, test_id, channel, value, unit, low, high, in_limit)
        )
        conn.execute(
            "INSERT INTO sync_status (record_id, table_name) VALUES (?, 'measurements')",
            (m_id,)
        )
        conn.commit()
    return m_id

def close_session(session_id: str):
    with sqlite3.connect(DB_PATH) as conn:
        conn.execute(
            "UPDATE sessions SET closed_at=datetime('now'), status='CLOSED' WHERE session_id=?",
            (session_id,)
        )
        conn.commit()
```

> Python's `sqlite3` module is in the standard library — no pip install needed.

### C / C++ (via libsqlite3)

```c
#include <sqlite3.h>
#include <stdio.h>

void insert_measurement(sqlite3 *db,
    const char *m_id, const char *test_id,
    const char *channel, double value, const char *unit,
    double low, double high)
{
    int in_limit = (value >= low && value <= high) ? 1 : 0;
    sqlite3_stmt *stmt;

    sqlite3_prepare_v2(db,
        "INSERT INTO measurements "
        "(measurement_id, test_id, channel_name, value, unit, "
        " lower_limit, upper_limit, in_limit, captured_at) "
        "VALUES (?,?,?,?,?,?,?,?,datetime('now'))", -1, &stmt, NULL);

    sqlite3_bind_text(stmt, 1, m_id,    -1, SQLITE_STATIC);
    sqlite3_bind_text(stmt, 2, test_id, -1, SQLITE_STATIC);
    sqlite3_bind_text(stmt, 3, channel, -1, SQLITE_STATIC);
    sqlite3_bind_double(stmt, 4, value);
    sqlite3_bind_text(stmt, 5, unit,    -1, SQLITE_STATIC);
    sqlite3_bind_double(stmt, 6, low);
    sqlite3_bind_double(stmt, 7, high);
    sqlite3_bind_int(stmt,   8, in_limit);
    sqlite3_step(stmt);
    sqlite3_finalize(stmt);

    sqlite3_prepare_v2(db,
        "INSERT INTO sync_status (record_id, table_name) VALUES (?,'measurements')",
        -1, &stmt, NULL);
    sqlite3_bind_text(stmt, 1, m_id, -1, SQLITE_STATIC);
    sqlite3_step(stmt);
    sqlite3_finalize(stmt);
}
```

---

## Reading sync-health.json from Linux Station Software

### Python

```python
import json, datetime

def get_sync_status(health_path="/var/testdata/sync-health.json") -> dict:
    try:
        with open(health_path) as f:
            return json.load(f)
    except FileNotFoundError:
        return {"postgresReachable": False, "pendingCount": -1, "deadLetterCount": -1}

status = get_sync_status()
print(f"Server connected: {status['postgresReachable']}")
print(f"Pending:          {status['pendingCount']}")
print(f"Dead letters:     {status['deadLetterCount']}")

if status["deadLetterCount"] > 0:
    print("WARNING: dead-letter records require manual review")
```

---

## Docker

To run SyncAgent in a container:

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY publish-linux/ .
COPY syncagent.json .
RUN chmod +x SyncAgent
ENTRYPOINT ["./SyncAgent"]
```

Mount `station.db`, the log directory, and your local config overrides as volumes:

```yaml
# docker-compose.yml (excerpt)
services:
  syncagent:
    build: .
    volumes:
      - /var/testdata/station.db:/var/testdata/station.db
      - /var/log/syncagent:/var/log/syncagent
      - ./syncagent.local.json:/app/syncagent.local.json:ro
    restart: unless-stopped
    # Alternatively, override individual settings via env vars:
    # environment:
    #   - SYNCAGENT_Postgres__ConnectionString=Host=10.0.1.50;...
    #   - SYNCAGENT_Station__StationId=ST-LX-01
```

---

## Service Management Cheatsheet

| Task | Command |
|---|---|
| Start | `sudo systemctl start syncagent` |
| Stop | `sudo systemctl stop syncagent` |
| Restart | `sudo systemctl restart syncagent` |
| Enable at boot | `sudo systemctl enable syncagent` |
| Disable at boot | `sudo systemctl disable syncagent` |
| Status | `sudo systemctl status syncagent` |
| Live logs | `journalctl -u syncagent -f` |
| Last 50 log lines | `journalctl -u syncagent -n 50` |
| Logs since reboot | `journalctl -u syncagent -b` |

---

## Dead-Letter Recovery on Linux

```bash
sqlite3 /var/testdata/station.db \
  "UPDATE sync_status SET synced=0, retry_count=0, next_attempt=NULL, failure_reason=NULL WHERE synced=2;"

sudo systemctl restart syncagent
```
