using ExcelSearch.Core.Staging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace ExcelSearch.Data.Sqlite;

// Re-check against the database.
public sealed partial class SqliteStagingStore
{
    // New (2), Already in database (3) and Conflict (5) rows are recomputed against Transactions on BOTH key columns. Only rows whose
    // status or ExistingRowHash actually changes are listed in "chg", so an unchanged batch writes nothing. Overwrite survives only for a
    // row that was a Conflict, still is one, and whose existing row did not change; in every other changed case it is reset to 0.
    // SQLite has no OUTPUT clause (and RETURNING only gives the new values), so the same "chg" CTE is used twice under one write
    // lock: once to count the moves, once to apply them. Nothing can change between the two statements. "chg" is MATERIALIZED, so
    // the UPDATE works from a snapshot of the changed rows taken before it writes (it reads the table it updates).
    private const string ChangedRowsCte = """
        WITH cur AS (
            SELECT s.ExcelRowNumber AS rn, s.Status AS OldStatus, s.Overwrite AS OldOverwrite, s.ExistingRowHash AS OldExisting,
                   CASE WHEN t.PayOrderNo IS NULL THEN 2 WHEN t.RowHash = s.RowHash THEN 3 ELSE 5 END AS NewStatus,
                   CASE WHEN t.PayOrderNo IS NULL THEN NULL ELSE t.RowHash END AS NewExisting
            FROM ImportStaging AS s
            LEFT JOIN Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo
            WHERE s.ImportBatchId = $b AND s.Status IN (2, 3, 5)
        ),
        chg AS MATERIALIZED (
            SELECT rn, OldStatus, OldOverwrite, NewStatus, NewExisting,
                   CASE WHEN NewStatus = 5 AND OldStatus = 5 AND OldOverwrite = 1 AND OldExisting = NewExisting THEN 1 ELSE 0 END AS NewOverwrite
            FROM cur
            WHERE OldStatus <> NewStatus OR IFNULL(OldExisting, '') <> IFNULL(NewExisting, '')
        )
        """;

    private const string CountMovesSql = ChangedRowsCte + """

        SELECT OldStatus, NewStatus, COUNT(*), SUM(CASE WHEN OldOverwrite = 1 AND NewOverwrite = 0 THEN 1 ELSE 0 END)
        FROM chg GROUP BY OldStatus, NewStatus;
        """;

    private const string ApplyMovesSql = ChangedRowsCte + """

        UPDATE ImportStaging
        SET Status = chg.NewStatus, ExistingRowHash = chg.NewExisting, Overwrite = chg.NewOverwrite
        FROM chg
        WHERE ImportStaging.ImportBatchId = $b AND ImportStaging.ExcelRowNumber = chg.rn;
        """;

    public override async Task<RefreshResult> RefreshAgainstDatabaseAsync(int batchId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var conn = (SqliteConnection)db.Database.GetDbConnection();

        // One transaction whose first act is to take the write lock (like CommitAsync): a commit or discard of this batch waits for
        // the re-check, and the re-check sees the batch's final status.
        Exec(conn, "BEGIN IMMEDIATE");
        var done = false;
        try
        {
            string? status;
            await using (var cmd = Cmd(conn, "SELECT Status FROM ImportBatches WHERE Id = $b", batchId))
                status = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (status is null) return RefreshResult.Refused($"Batch {batchId} does not exist.");
            if (status != "Staged")
                return RefreshResult.Refused($"Batch {batchId} is '{status}', not 'Staged', so it can no longer be re-checked.");

            var moves = new Dictionary<(RowStatus From, RowStatus To), int>();
            var cleared = 0;
            await using (var cmd = Cmd(conn, CountMovesSql, batchId))
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var from = (RowStatus)r.GetInt32(0);
                    var to = (RowStatus)r.GetInt32(1);
                    if (from != to) moves[(from, to)] = moves.GetValueOrDefault((from, to)) + r.GetInt32(2);
                    cleared += r.GetInt32(3);
                }
            }

            await using (var cmd = Cmd(conn, ApplyMovesSql, batchId))
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            Exec(conn, "COMMIT");
            done = true;
            var list = moves.Select(m => new StatusMove(m.Key.From, m.Key.To, m.Value)).ToList();
            return new RefreshResult(true, null, list, cleared, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            if (!done) TryRollback(conn);
        }
    }
}
