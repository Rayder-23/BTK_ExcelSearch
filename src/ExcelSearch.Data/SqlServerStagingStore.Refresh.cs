using System.Data;
using System.Diagnostics;
using ExcelSearch.Core.Staging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ExcelSearch.Data;

// Re-check against the database, conflict totals, and keyset-paged reads of a batch's Invalid rows.
public sealed partial class SqlServerStagingStore
{
    // Same idea as ClassifyChunk: bounded statements so one re-check cannot escalate to a table lock on ImportStaging.
    private const int RefreshChunk = 4_000;

    // New (2), Already in database (3) and Conflict (5) rows are recomputed against Transactions on BOTH key columns.
    // A row is only written when its status or ExistingRowHash actually changed, so an unchanged batch writes nothing.
    // Overwrite survives only for a row that was a Conflict, still is one, and whose existing row did not change;
    // in every other changed case it is reset to 0. (In an UPDATE's SET list every s.* is the value before the update.)
    private const string RefreshSql = """
        UPDATE s
        SET Status = n.NewStatus,
            ExistingRowHash = n.NewExisting,
            Overwrite = CASE WHEN n.NewStatus = 5 AND s.Status = 5 AND s.Overwrite = 1
                              AND s.ExistingRowHash = n.NewExisting THEN 1 ELSE 0 END
        OUTPUT deleted.Status, inserted.Status, deleted.Overwrite, inserted.Overwrite
        FROM dbo.ImportStaging AS s
        LEFT JOIN dbo.Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo
        CROSS APPLY (SELECT CASE WHEN t.PayOrderNo IS NULL THEN CAST(2 AS tinyint)
                                 WHEN t.RowHash = s.RowHash THEN CAST(3 AS tinyint)
                                 ELSE CAST(5 AS tinyint) END AS NewStatus,
                            CASE WHEN t.PayOrderNo IS NULL THEN CAST(NULL AS varchar(64)) ELSE t.RowHash END AS NewExisting) AS n
        WHERE s.ImportBatchId = @b
          AND s.Status IN (2, 3, 5)
          AND s.ExcelRowNumber BETWEEN @lo AND @hi
          AND (s.Status <> n.NewStatus OR ISNULL(s.ExistingRowHash, '') <> ISNULL(n.NewExisting, ''));
        """;

    public async Task<RefreshResult> RefreshAgainstDatabaseAsync(int batchId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        await using (var cmd = Cmd("SELECT MAX(ExcelRowNumber) FROM dbo.ImportStaging WHERE ImportBatchId = @b AND Status IN (2, 3, 5)"))
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

    public async Task<ConflictCounts> GetConflictCountsAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var conflicts = db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Conflict);
        var total = await conflicts.CountAsync(ct).ConfigureAwait(false);
        var overwrite = await conflicts.CountAsync(s => s.Overwrite, ct).ConfigureAwait(false);
        return new ConflictCounts(total, overwrite);
    }

    // ---- Invalid rows for the export, in keyset pages (WHERE ExcelRowNumber > last ORDER BY ExcelRowNumber) ----

    public async Task<BatchInfo?> GetBatchAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ImportBatches.AsNoTracking().Where(b => b.Id == batchId)
            .Select(b => new BatchInfo(b.Id, b.FileName, b.SheetName, b.FileHash, b.Status))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InvalidRowError>> GetInvalidErrorsAfterAsync(int batchId, int afterRow, int take,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Invalid && s.ExcelRowNumber > afterRow)
            .OrderBy(s => s.ExcelRowNumber).Take(take)
            .Select(s => new InvalidRowError(s.ExcelRowNumber, s.ErrorText))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StagedRow>> GetInvalidRowsAfterAsync(int batchId, int afterRow, int take,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Invalid && s.ExcelRowNumber > afterRow)
            .OrderBy(s => s.ExcelRowNumber).Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
        return entities.Select(ToStagedRow).ToList();
    }
}
