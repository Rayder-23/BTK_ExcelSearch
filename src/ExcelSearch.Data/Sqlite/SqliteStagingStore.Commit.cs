using ExcelSearch.Core.Staging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace ExcelSearch.Data.Sqlite;

// Commit step: staging -> Transactions, plus housekeeping.
public sealed partial class SqliteStagingStore
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    // SQLite result codes for "another connection holds the write lock" (the second is the shared-cache variant).
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    // Business columns copied between ImportStaging and Transactions (same names on both sides; money is cents on both sides).
    private static readonly string[] BusinessColumns =
    {
        "ValueDate", "BranchCode", "BranchName", "RegNo", "ApplicationNo", "PlotNo", "StreetNo", "ChallanNo",
        "ChequeInstNo", "CustomerName", "Cnic", "Discount", "DownPaymentAmount", "Debit", "Credit",
        "RunningBalance", "BankName", "AccountNo", "Project", "DealerName", "DataSource",
        "Narration1", "Narration2", "Narration3", "Narration4", "Narration5", "SlipNo", "NarrationCnic",
    };

    // Id and CreatedAt are left out on purpose: the column defaults (a random GUID, "now") fill them, as on SQL Server.
    private static readonly string InsertSql =
        "INSERT INTO Transactions (PayOrderNo, TxnRefSeqNo, ImportBatchId, SourceRowNumber, RowHash, " +
        string.Join(", ", BusinessColumns) + ") " +
        "SELECT s.PayOrderNo, s.TxnRefSeqNo, s.ImportBatchId, s.ExcelRowNumber, s.RowHash, " +
        string.Join(", ", BusinessColumns.Select(c => "s." + c)) + " " +
        // New rows, plus (unless the user chose to skip them; $skip is replaced by 1 or 0) flagged key-less rows:
        // already in the database, repeated in the file, possible duplicate.
        "FROM ImportStaging AS s WHERE s.ImportBatchId = $b AND (s.Status = 2 OR " +
        "($skip = 0 AND s.Status IN (3, 4, 6) AND " + SqliteStatusSql.KeyLess + "));";

    // UPDATE ... FROM (SQLite 3.33+): in SET the target columns are unqualified; the join is in WHERE.
    private static readonly string UpdateSql =
        $"UPDATE Transactions SET RowHash = s.RowHash, ImportBatchId = s.ImportBatchId, SourceRowNumber = s.ExcelRowNumber, " +
        $"UpdatedAt = {NowSql}, " +
        string.Join(", ", BusinessColumns.Select(c => $"{c} = s.{c}")) + " " +
        "FROM ImportStaging AS s " +
        "WHERE s.ImportBatchId = $b AND s.Status = 5 AND s.Overwrite = 1 " +
        "AND Transactions.PayOrderNo = s.PayOrderNo AND Transactions.TxnRefSeqNo = s.TxnRefSeqNo;";

    // How long BEGIN IMMEDIATE waits for another writer; a zero or negative setting falls back to the default (like SQL Server's lock).
    private int LockTimeoutSeconds =>
        _options.CommitLockTimeoutSeconds > 0
            ? _options.CommitLockTimeoutSeconds
            : StagingStoreOptions.DefaultCommitLockTimeoutSeconds;

    public override async Task<CommitResult> CommitAsync(int batchId, bool skipFlaggedDuplicates = false, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        CommitResult Fail(CommitFailure reason, string message) =>
            new(false, reason, message, ElapsedSeconds: sw.Elapsed.TotalSeconds);

        CommitResult ok;
        await using (var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            var conn = (SqliteConnection)db.Database.GetDbConnection();

            async Task<int> Scalar(string sql)
            {
                await using var cmd = Cmd(conn, sql, batchId);
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            async Task<int> NonQuery(string sql)
            {
                await using var cmd = Cmd(conn, sql, batchId);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // a. Only one commit (one writer) at a time. BEGIN IMMEDIATE takes the database's write lock now and holds it until
            //    COMMIT/ROLLBACK; another writer - a second commit, a discard, a staging load - waits for it (busy_timeout).
            //    Waiting longer than the configured time is the same "try again in a moment" answer SQL Server gives.
            try
            {
                // Waiting for the lock has two parts that add up (measured): SQLite's own busy handler (PRAGMA busy_timeout) and then
                // Microsoft.Data.Sqlite retrying until CommandTimeout. Both are set to the configured lock timeout so the wait is that long.
                // (The next time EF opens a connection the interceptor puts busy_timeout back to 30 s.)
                Exec(conn, $"PRAGMA busy_timeout = {LockTimeoutSeconds * 1000}");
                using var begin = conn.CreateCommand();
                begin.CommandText = "BEGIN IMMEDIATE";
                begin.CommandTimeout = LockTimeoutSeconds;
                begin.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteBusy or SqliteLocked)
            {
                return Fail(CommitFailure.LockTimeout, "Another import is committing, please try again in a moment.");
            }

            var committed = false;
            try
            {
                // b. Guards. We own the write lock, so a concurrent DiscardAsync (whose conditional UPDATE needs it) waits for this
                //    commit to finish, then finds the batch 'Committed' and refuses. If the discard got in first, we read
                //    'Discarded' here and stop. Either way the outcome is all-or-nothing, never a mix.
                string status;
                int totalRows;
                await using (var cmd = Cmd(conn, "SELECT Status, TotalRows FROM ImportBatches WHERE Id = $b", batchId))
                await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    if (!await r.ReadAsync(ct).ConfigureAwait(false))
                        return Fail(CommitFailure.BatchNotStaged, $"Batch {batchId} does not exist.");
                    status = r.GetString(0);
                    totalRows = r.GetInt32(1);
                }
                if (status != "Staged")
                    return Fail(CommitFailure.BatchNotStaged, $"Batch {batchId} is '{status}', not 'Staged', so it cannot be committed.");

                var staged = await Scalar("SELECT COUNT(*) FROM ImportStaging WHERE ImportBatchId = $b");
                if (totalRows <= 0 || staged != totalRows)
                    return Fail(CommitFailure.IncompleteLoad,
                        $"Batch {batchId} is incomplete: {staged:N0} staged rows but {totalRows:N0} expected.");
                var pending = await Scalar("SELECT COUNT(*) FROM ImportStaging WHERE ImportBatchId = $b AND Status = 0");
                if (pending > 0)
                    return Fail(CommitFailure.IncompleteLoad, $"Batch {batchId} still has {pending:N0} unclassified (Pending) rows.");

                // c. Re-check against Transactions as it is NOW (others may have committed since the preview).
                //    (These three checks join on the key, so they only ever see rows that have a full key; a key-less row
                //    cannot collide with anything, it is at worst a duplicate, which is allowed.)
                //    (i) a New row whose key now exists with the same hash is simply an exact duplicate.
                await NonQuery("""
                    UPDATE ImportStaging SET Status = 3
                    WHERE ImportBatchId = $b AND Status = 2
                      AND EXISTS (SELECT 1 FROM Transactions AS t
                                  WHERE t.PayOrderNo = ImportStaging.PayOrderNo AND t.TxnRefSeqNo = ImportStaging.TxnRefSeqNo
                                    AND t.RowHash = ImportStaging.RowHash);
                    """);

                //    (ii) a New row whose key now exists with a different hash became a conflict nobody reviewed.
                var changedNew = await Scalar("""
                    SELECT COUNT(*) FROM ImportStaging AS s
                    JOIN Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo
                    WHERE s.ImportBatchId = $b AND s.Status = 2;
                    """);
                if (changedNew > 0)
                    return Fail(CommitFailure.RowsChangedSincePreview,
                        $"{changedNew:N0} new row(s) now exist in Transactions with different values (another import committed them). " +
                        "Nothing was written. Discard this batch and import the file again.");

                //    (iii) an overwrite is only safe if the existing row is still the one the user looked at.
                var changedConflicts = await Scalar("""
                    SELECT COUNT(*) FROM ImportStaging AS s
                    LEFT JOIN Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo
                    WHERE s.ImportBatchId = $b AND s.Status = 5 AND s.Overwrite = 1
                      AND (t.Id IS NULL OR t.RowHash <> s.ExistingRowHash);
                    """);
                if (changedConflicts > 0)
                    return Fail(CommitFailure.RowsChangedSincePreview,
                        $"{changedConflicts:N0} row(s) marked for overwrite were changed or removed in Transactions since the preview. " +
                        "Nothing was written. Discard this batch and import the file again.");

                // Final tallies (step c(i) may have turned some New rows into exact duplicates).
                int newRows = 0, exact = 0, fileDup = 0, likely = 0, invalid = 0, overwritable = 0, skippedConflicts = 0;
                int exactKeyless = 0, fileDupKeyless = 0, likelyKeyless = 0; // the key-less part of each: these are importable
                await using (var cmd = Cmd(conn, """
                    SELECT Status, COUNT(*), SUM(CASE WHEN Overwrite = 1 THEN 1 ELSE 0 END),
                           SUM(CASE WHEN PayOrderNo IS NULL OR TxnRefSeqNo IS NULL THEN 1 ELSE 0 END)
                    FROM ImportStaging WHERE ImportBatchId = $b GROUP BY Status
                    """, batchId))
                await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        int n = r.GetInt32(1), ow = r.GetInt32(2), keyless = r.GetInt32(3);
                        switch ((RowStatus)r.GetInt32(0))
                        {
                            case RowStatus.Invalid: invalid = n; break;
                            case RowStatus.New: newRows = n; break;
                            case RowStatus.ExactDuplicate: exact = n; exactKeyless = keyless; break;
                            case RowStatus.FileDuplicate: fileDup = n; fileDupKeyless = keyless; break;
                            case RowStatus.LikelyDuplicate: likely = n; likelyKeyless = keyless; break;
                            case RowStatus.Conflict: overwritable = ow; skippedConflicts = n - ow; break;
                        }
                    }
                }
                // Flagged key-less rows are imported unless the user asked to skip them.
                int importedExact = skipFlaggedDuplicates ? 0 : exactKeyless;
                int importedFile = skipFlaggedDuplicates ? 0 : fileDupKeyless;
                int importedLikely = skipFlaggedDuplicates ? 0 : likelyKeyless;
                int flagged = importedExact + importedFile + importedLikely;

                // d. + e. The writes. Keys cannot collide: every New key was verified absent above, under the write lock.
                var inserted = await NonQuery(InsertSql.Replace("$skip", skipFlaggedDuplicates ? "1" : "0"));
                var updated = await NonQuery(UpdateSql);
                if (inserted != newRows + flagged || updated != overwritable)
                    throw new InvalidOperationException(
                        $"Commit of batch {batchId} wrote {inserted} inserts/{updated} updates but expected {newRows + flagged}/{overwritable}; rolled back.");

                // f. Counters and status, in the same transaction.
                await using (var cmd = Cmd(conn, $"""
                    UPDATE ImportBatches
                    SET NewRows = $new, UpdatedRows = $upd, SkippedRows = $skip, RejectedRows = $rej,
                        Status = 'Committed', CommittedAt = {NowSql}
                    WHERE Id = $b AND Status = 'Staged'
                    """, batchId))
                {
                    cmd.Parameters.AddWithValue("$new", inserted);
                    cmd.Parameters.AddWithValue("$upd", updated);
                    cmd.Parameters.AddWithValue("$skip", exact + fileDup + likely + skippedConflicts - flagged);
                    cmd.Parameters.AddWithValue("$rej", invalid);
                    if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                        throw new InvalidOperationException($"Batch {batchId} changed state during commit; rolled back.");
                }

                Exec(conn, "COMMIT");   // releases the write lock
                committed = true;

                ok = new CommitResult(true, CommitFailure.None,
                    $"Committed batch {batchId}: {inserted:N0} imported ({flagged:N0} of them flagged duplicates), {updated:N0} updated, " +
                    $"{exact - importedExact:N0} already in the database skipped, {fileDup - importedFile:N0} repeats inside the file skipped, " +
                    $"{likely - importedLikely:N0} possible duplicates skipped, " +
                    $"{skippedConflicts:N0} conflicts kept as they were, {invalid:N0} invalid rows rejected.",
                    inserted - flagged, updated, exact - importedExact, fileDup - importedFile, skippedConflicts, invalid,
                    ImportedFlaggedDuplicates: flagged, SkippedLikelyDuplicates: likely - importedLikely);
            }
            finally
            {
                // Any early return or exception above lands here without a COMMIT: roll back and release the lock.
                if (!committed) TryRollback(conn);
            }
        }

        // g. Outside the transaction: the staging rows are no longer needed. A failure here is harmless
        //    (CleanupStaleBatchesAsync removes leftovers of Committed batches later).
        try
        {
            await using var cleanup = await Factory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            await DeleteStagingRowsAsync(cleanup, batchId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Batch {BatchId} committed, but its staging rows could not be deleted; cleanup will remove them.", batchId);
        }

        return ok with { ElapsedSeconds = sw.Elapsed.TotalSeconds };
    }

    public override async Task<CleanupResult> CleanupStaleBatchesAsync(CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // ImportedAt is ISO UTC text, so "older than 24 hours" is a plain text comparison against a computed cut-off.
        var ago = $"-{(int)StaleAfter.TotalHours} hours";
        var stale = await db.Database.SqlQuery<int>(
            $"SELECT Id AS Value FROM ImportBatches WHERE Status = 'Staged' AND ImportedAt < strftime('%Y-%m-%dT%H:%M:%fZ', 'now', {ago})")
            .ToListAsync(ct).ConfigureAwait(false);

        // The same discard path as the Discard button: it refuses a batch that was committed meanwhile.
        var discarded = 0;
        foreach (var id in stale)
            if ((await DiscardAsync(id, ct).ConfigureAwait(false)).Discarded) discarded++;

        var leftovers = await db.Database.SqlQueryRaw<int>("""
            SELECT b.Id AS Value FROM ImportBatches AS b
            WHERE b.Status IN ('Committed', 'Discarded')
              AND EXISTS (SELECT 1 FROM ImportStaging AS s WHERE s.ImportBatchId = b.Id)
            """).ToListAsync(ct).ConfigureAwait(false);

        foreach (var id in leftovers) await DeleteStagingRowsAsync(db, id, ct).ConfigureAwait(false);

        return new CleanupResult(discarded, leftovers.Count);
    }
}
