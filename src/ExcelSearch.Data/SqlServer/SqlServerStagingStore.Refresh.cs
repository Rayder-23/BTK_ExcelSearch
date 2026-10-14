using System.Data;
using System.Diagnostics;
using ExcelSearch.Core.Staging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ExcelSearch.Data.SqlServer;

// Re-check against the database.
public sealed partial class SqlServerStagingStore
{
    // Same idea as ClassifyChunk: bounded statements so one re-check cannot escalate to a table lock on ImportStaging.
    private const int RefreshChunk = 4_000;

    // New (2), Already in database (3), Conflict (5) and Possible duplicate (6) rows are recomputed against Transactions
    // by the same rule as classification (StatusSql): by key for keyed rows, by content / slip number for key-less ones.
    // A row is only written when its status or ExistingRowHash actually changed, so an unchanged batch writes nothing.
    // Overwrite survives only for a row that was a Conflict, still is one, and whose existing row did not change;
    // in every other changed case it is reset to 0. (In an UPDATE's SET list every s.* is the value before the update.)
    private static readonly string RefreshSql =
        "UPDATE s SET Status = n.NewStatus, ExistingRowHash = n.NewExisting, " +
        "Overwrite = CASE WHEN n.NewStatus = 5 AND s.Status = 5 AND s.Overwrite = 1 AND s.ExistingRowHash = n.NewExisting THEN 1 ELSE 0 END " +
        "OUTPUT deleted.Status, inserted.Status, deleted.Overwrite, inserted.Overwrite " +
        "FROM dbo.ImportStaging AS s " +
        "LEFT JOIN dbo.Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo " +
        "CROSS APPLY (SELECT CAST(" + StatusSql.NewStatus + " AS tinyint) AS NewStatus, " + StatusSql.ExistingHash + " AS NewExisting) AS n " +
        "WHERE s.ImportBatchId = @b AND s.Status IN (2, 3, 5, 6) AND s.ExcelRowNumber BETWEEN @lo AND @hi " +
        "AND (s.Status <> n.NewStatus OR ISNULL(s.ExistingRowHash, '') <> ISNULL(n.NewExisting, ''));";

    public override async Task<RefreshResult> RefreshAgainstDatabaseAsync(int batchId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // One transaction whose first act is to lock the batch row (UPDLOCK, HOLDLOCK), exactly like CommitAsync:
        // a commit or discard of this batch waits for the re-check, and the re-check sees the batch's final status.
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var conn = (SqlConnection)db.Database.GetDbConnection();
        var dbTx = (SqlTransaction)tx.GetDbTransaction();

        SqlCommand Cmd(string sql)
        {
            var cmd = new SqlCommand(sql, conn, dbTx) { CommandTimeout = CommandTimeoutSeconds };
            cmd.Parameters.Add(new SqlParameter("@b", SqlDbType.Int) { Value = batchId });
            return cmd;
        }

        string? status;
        await using (var cmd = Cmd("SELECT Status FROM dbo.ImportBatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @b"))
            status = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (status is null) return RefreshResult.Refused($"Batch {batchId} does not exist.");
        if (status != "Staged")
            return RefreshResult.Refused($"Batch {batchId} is '{status}', not 'Staged', so it can no longer be re-checked.");

        int? maxRow;
        await using (var cmd = Cmd("SELECT MAX(ExcelRowNumber) FROM dbo.ImportStaging WHERE ImportBatchId = @b AND Status IN (2, 3, 5, 6)"))
        {
            var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            maxRow = v is null or DBNull ? null : Convert.ToInt32(v);
        }

        var moves = new Dictionary<(RowStatus From, RowStatus To), int>();
        var cleared = 0;
        for (var lo = 0; maxRow is not null && lo <= maxRow; lo += RefreshChunk)
        {
            await using var cmd = Cmd(RefreshSql);
            cmd.Parameters.Add(new SqlParameter("@lo", SqlDbType.Int) { Value = lo });
            cmd.Parameters.Add(new SqlParameter("@hi", SqlDbType.Int) { Value = lo + RefreshChunk - 1 });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var from = (RowStatus)r.GetByte(0);
                var to = (RowStatus)r.GetByte(1);
                if (from != to) moves[(from, to)] = moves.GetValueOrDefault((from, to)) + 1;
                if (r.GetBoolean(2) && !r.GetBoolean(3)) cleared++;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        var list = moves.Select(m => new StatusMove(m.Key.From, m.Key.To, m.Value)).ToList();
        return new RefreshResult(true, null, list, cleared, sw.Elapsed.TotalSeconds);
    }
}
