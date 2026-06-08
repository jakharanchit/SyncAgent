# Changelog

All notable changes to OpCore SyncAgent are documented here.

## [1.0.0] — 2026-06-05

### Added
- Initial release.
- SQLite-to-PostgreSQL sync for four entity types: sessions, tests, measurements, audit_log.
- Configurable batch size (default 100 records per cycle) and poll interval (default 30 s).
- 10-step exponential backoff with ±10% jitter: 30 s → 60 s → 2 min → 5 min → 15 min → 1 hr (×5).
- Dead-letter state (`synced=2`) after MaxRetries exhausted — record frozen until manually reset.
- Idempotent writes via `ON CONFLICT (primary_key) DO NOTHING` on all four PostgreSQL tables.
- Atomic health file (`sync-health.json`) written via `.tmp` → rename after every cycle.
- Schema version check on startup — refuses to run if SQLite schema does not match expected version.
- Windows Service support via `UseWindowsService()` (auto-starts at boot, recoverable).
- Linux systemd support via `UseSystemd()` (same binary).
- Rolling log files via Serilog (daily rotation, 30-day retention).
- `station_id` injected into every PostgreSQL row by SyncAgent (not present in SQLite source).
- `StationId` and `SiteName` configurable per station via `syncagent.json` (template) and `syncagent.local.json` (gitignored local overrides).
