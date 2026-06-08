using Microsoft.Extensions.Logging;
using Npgsql;
using SyncAgent.Config;
using SyncAgent.Data.Models;

namespace SyncAgent.Data;

public sealed class PostgresWriter
{
    private readonly string                          _connStr;
    private readonly string                          _stationId;
    private readonly Dictionary<string, TableMap>    _tableMapIndex;
    private readonly ILogger<PostgresWriter>         _logger;

    public PostgresWriter(SyncConfig config, ILogger<PostgresWriter> logger)
    {
        _connStr       = config.PostgresConnStr;
        _stationId     = config.StationId;
        _tableMapIndex = config.Tables.ToDictionary(
            m => m.SourceTable,
            StringComparer.OrdinalIgnoreCase);
        _logger        = logger;
    }

    public async Task<WriteResult> WriteBatchAsync(List<GenericRecord> records, CancellationToken ct)
    {
        NpgsqlTransaction? tx = null;
        try
        {
            await using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(ct);
            tx = await conn.BeginTransactionAsync(ct);

            foreach (var record in records)
            {
                if (!_tableMapIndex.TryGetValue(record.SourceTable, out var map))
                    throw new InvalidOperationException(
                        $"No table mapping for '{record.SourceTable}'. Add it to the Tables array in syncagent.json.");

                await InsertGenericAsync(conn, record, map, ct);
            }

            await tx.CommitAsync(ct);
            _logger.LogDebug("Batch committed: {Count} records", records.Count);
            return WriteResult.Success(records.Select(r => r.RecordId).ToList());
        }
        catch (Exception ex)
        {
            if (tx is not null)
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch (Exception rollbackEx)
                {
                    // Npgsql 8 automatically rolls back the transaction when a command error
                    // occurs, so an explicit rollback here may fail with "transaction already
                    // completed". Swallow and continue so the original exception is preserved.
                    _logger.LogDebug(rollbackEx, "Rollback attempt failed (transaction may have been auto-rolled back by the driver)");
                }
            }
            _logger.LogWarning(ex, "Batch rolled back: {Count} records", records.Count);
            return WriteResult.AllFailed(records, ex);
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Generic INSERT builder ────────────────────────────────────────────────
    // Builds: INSERT INTO <TargetTable> (col1, col2, ...) VALUES (@col1, @col2::timestamptz, ...)
    //         ON CONFLICT (<PrimaryKey>) DO NOTHING
    //
    // station_id is injected when InjectStationId=true and the source row does not already carry it.
    // TimestampColumns get a ::timestamptz cast so string ISO8601 values are parsed by PostgreSQL.

    private async Task InsertGenericAsync(
        NpgsqlConnection conn, GenericRecord record, TableMap map, CancellationToken ct)
    {
        var sourceColumns = record.Columns.Keys.ToList();

        bool injectStation = map.InjectStationId
            && !sourceColumns.Contains("station_id", StringComparer.OrdinalIgnoreCase);

        var allColumns = injectStation
            ? [.. sourceColumns, "station_id"]
            : sourceColumns;

        var tsSet   = new HashSet<string>(map.TimestampColumns, StringComparer.OrdinalIgnoreCase);
        var colList = string.Join(", ", allColumns);
        var valList = string.Join(", ", allColumns.Select(c =>
            tsSet.Contains(c) ? $"@{c}::timestamptz" : $"@{c}"));

        var sql = $"""
            INSERT INTO {map.TargetTable} ({colList})
            VALUES ({valList})
            ON CONFLICT ({map.PrimaryKey}) DO NOTHING
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);

        foreach (var (col, val) in record.Columns)
            cmd.Parameters.AddWithValue($"@{col}", val ?? DBNull.Value);

        if (injectStation)
            cmd.Parameters.AddWithValue("@station_id", _stationId);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
