using System.Data;
using System.Diagnostics;
using ExcelSearch.Core.Staging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.Data;

// Commit step: staging -> dbo.Transactions, plus housekeeping.
public sealed partial class SqlServerStagingStore
{
    private const int CommitTimeoutSeconds = 30 * 60;
    private const int AppLockTimeoutMs = 60_000;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    // Business columns copied between ImportStaging and Transactions (same names on both sides).
    private static readonly string[] BusinessColumns =
    {
        "ValueDate", "BranchCode", "BranchName", "RegNo", "ApplicationNo", "PlotNo", "StreetNo", "ChallanNo",
        "ChequeInstNo", "CustomerName", "Cnic", "Discount", "DownPaymentAmount", "Debit", "Credit",
        "RunningBalance", "BankName", "AccountNo", "Project", "DealerName", "DataSource",
        "Narration1", "Narration2", "Narration3", "Narration4", "Narration5",
    };

    // Id is left out on purpose: the column default (NEWSEQUENTIALID) fills it.
    private static readonly string InsertSql =
        "INSERT INTO dbo.Transactions (PayOrderNo, TxnRefSeqNo, ImportBatchId, SourceRowNumber, RowHash, " +
        string.Join(", ", BusinessColumns) + ") " +
        "SELECT s.PayOrderNo, s.TxnRefSeqNo, s.ImportBatchId, s.ExcelRowNumber, s.RowHash, " +
        string.Join(", ", BusinessColumns.Select(c => "s." + c)) + " " +
        "FROM dbo.ImportStaging AS s WHERE s.ImportBatchId = @b AND s.Status = 2;";

    private static readonly string UpdateSql =
        "UPDATE t SET t.RowHash = s.RowHash, t.ImportBatchId = s.ImportBatchId, t.SourceRowNumber = s.ExcelRowNumber, " +
        "t.UpdatedAt = SYSUTCDATETIME(), " +
        string.Join(", ", BusinessColumns.Select(c => $"t.{c} = s.{c}")) + " " +
        "FROM dbo.Transactions AS t " +
        "JOIN dbo.ImportStaging AS s ON s.PayOrderNo = t.PayOrderNo AND s.TxnRefSeqNo = t.TxnRefSeqNo " +
        "WHERE s.ImportBatchId = @b AND s.Status = 5 AND s.Overwrite = 1;";

    public async Task<int> SetConflictOverwriteAsync(int batchId, bool overwrite,
        IReadOnlyCollection<int>? excelRowNumbers, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);

        var query = db.ImportStagings.Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Conflict);
        if (excelRowNumbers is not null)
        {
            var rows = excelRowNumbers.ToArray();
            query = query.Where(s => rows.Contains(s.ExcelRowNumber));
        }
        return await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.Overwrite, overwrite), ct).ConfigureAwait(false);
    }

    public async Task<CommitResult> CommitAsync(int batchId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        CommitResult Fail(CommitFailure reason, string message) =>
            new(false, reason, message, ElapsedSeconds: sw.Elapsed.TotalSeconds);

        CommitResult ok;
        await using (var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            // One explicit transaction: everything below commits together or not at all. If we return early
            // or throw, disposing the transaction rolls it back (and releases the application lock).
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var conn = (SqlConnection)db.Database.GetDbConnection();
            var dbTx = (SqlTransaction)tx.GetDbTransaction();

            SqlCommand Cmd(string sql)
            {
                var cmd = new SqlCommand(sql, conn, dbTx) { CommandTimeout = CommitTimeoutSeconds };
                cmd.Parameters.Add(new SqlParameter("@b", SqlDbType.Int) { Value = batchId });
                return cmd;
            }

            async Task<int> Scalar(string sql)
            {
                await using var cmd = Cmd(sql);
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            async Task<int> NonQuery(string sql)
            {
                await using var cmd = Cmd(sql);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // a. Only one commit at a time. The lock belongs to the transaction, so commit/rollback releases it.
            await using (var lockCmd = new SqlCommand("sp_getapplock", conn, dbTx)
                         { CommandType = CommandType.StoredProcedure, CommandTimeout = CommitTimeoutSeconds })
            {
                lockCmd.Parameters.AddWithValue("@Resource", "ExcelSearch.Commit");
                lockCmd.Parameters.AddWithValue("@LockMode", "Exclusive");
                lockCmd.Parameters.AddWithValue("@LockOwner", "Transaction");
                lockCmd.Parameters.AddWithValue("@LockTimeout", AppLockTimeoutMs);
                var rc = new SqlParameter("@rc", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
                lockCmd.Parameters.Add(rc);
                await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if ((int)rc.Value < 0)
                    return Fail(CommitFailure.LockTimeout,
                        "Another import is committing, please try again in a moment.");
            }

            // b. Guards (read under the lock, so nobody else can commit this batch meanwhile).
            string status;
            int totalRows;
            await using (var cmd = Cmd("SELECT Status, TotalRows FROM dbo.ImportBatches WHERE Id = @b"))
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await r.ReadAsync(ct).ConfigureAwait(false))
                    return Fail(CommitFailure.BatchNotStaged, $"Batch {batchId} does not exist.");
                status = r.GetString(0);
                totalRows = r.GetInt32(1);
            }
            if (status != "Staged")
                return Fail(CommitFailure.BatchNotStaged, $"Batch {batchId} is '{status}', not 'Staged', so it cannot be committed.");

            var staged = await Scalar("SELECT COUNT(*) FROM dbo.ImportStaging WHERE ImportBatchId = @b");
            if (totalRows <= 0 || staged != totalRows)
                return Fail(CommitFailure.IncompleteLoad,
                    $"Batch {batchId} is incomplete: {staged:N0} staged rows but {totalRows:N0} expected.");
            var pending = await Scalar("SELECT COUNT(*) FROM dbo.ImportStaging WHERE ImportBatchId = @b AND Status = 0");
            if (pending > 0)
                return Fail(CommitFailure.IncompleteLoad, $"Batch {batchId} still has {pending:N0} unclassified (Pending) rows.");

            // c. Re-check against Transactions as it is NOW (others may have committed since the preview).
            //    (i) a New row whose key now exists with the same hash is simply an exact duplicate.
            await NonQuery("""
                UPDATE s SET Status = 3
                FROM dbo.ImportStaging AS s
                JOIN dbo.Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo
                WHERE s.ImportBatchId = @b AND s.Status = 2 AND t.RowHash = s.RowHash;
                """);

            //    (ii) a New row whose key now exists with a different hash became a conflict nobody reviewed.
            var changedNew = await Scalar("""
                SELECT COUNT(*) FROM dbo.ImportStaging AS s
                JOIN dbo.Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo
                WHERE s.ImportBatchId = @b AND s.Status = 2;
                """);
            if (changedNew > 0)
                return Fail(CommitFailure.RowsChangedSincePreview,
                    $"{changedNew:N0} new row(s) now exist in Transactions with different values (another import committed them). " +
                    "Nothing was written. Discard this batch and import the file again.");

            //    (iii) an overwrite is only safe if the existing row is still the one the user looked at.
            var changedConflicts = await Scalar("""
                SELECT COUNT(*) FROM dbo.ImportStaging AS s
                LEFT JOIN dbo.Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo
                WHERE s.ImportBatchId = @b AND s.Status = 5 AND s.Overwrite = 1
                  AND (t.PayOrderNo IS NULL OR t.RowHash <> s.ExistingRowHash);
                """);
            if (changedConflicts > 0)
                return Fail(CommitFailure.RowsChangedSincePreview,
                    $"{changedConflicts:N0} row(s) marked for overwrite were changed or removed in Transactions since the preview. " +
                    "Nothing was written. Discard this batch and import the file again.");

            // Final tallies (step c(i) may have turned some New rows into exact duplicates).
            int newRows = 0, exact = 0, fileDup = 0, invalid = 0, overwritable = 0, skippedConflicts = 0;
            await using (var cmd = Cmd("""
                SELECT Status, COUNT(*), SUM(CASE WHEN Overwrite = 1 THEN 1 ELSE 0 END)
                FROM dbo.ImportStaging WHERE ImportBatchId = @b GROUP BY Status
                """))
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    int n = r.GetInt32(1), ow = r.GetInt32(2);
                    switch ((RowStatus)r.GetByte(0))
                    {
                        case RowStatus.Invalid: invalid = n; break;
                        case RowStatus.New: newRows = n; break;
                        case RowStatus.ExactDuplicate: exact = n; break;
                        case RowStatus.FileDuplicate: fileDup = n; break;
                        case RowStatus.Conflict: overwritable = ow; skippedConflicts = n - ow; break;
                    }
                }
            }

            // d. + e. The writes. Keys cannot collide: every New key was verified absent above, under the lock.
            var inserted = await NonQuery(InsertSql);
            var updated = await NonQuery(UpdateSql);
            if (inserted != newRows || updated != overwritable)
                throw new InvalidOperationException(
                    $"Commit of batch {batchId} wrote {inserted} inserts/{updated} updates but expected {newRows}/{overwritable}; rolled back.");

            // f. Counters and status, in the same transaction.
            await using (var cmd = Cmd("""
                UPDATE dbo.ImportBatches
                SET NewRows = @new, UpdatedRows = @upd, SkippedRows = @skip, RejectedRows = @rej,
                    Status = 'Committed', CommittedAt = SYSUTCDATETIME()
                WHERE Id = @b AND Status = 'Staged'
                """))
            {
                cmd.Parameters.AddWithValue("@new", inserted);
                cmd.Parameters.AddWithValue("@upd", updated);
                cmd.Parameters.AddWithValue("@skip", exact + fileDup + skippedConflicts);
                cmd.Parameters.AddWithValue("@rej", invalid);
                if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException($"Batch {batchId} changed state during commit; rolled back.");
            }

            await tx.CommitAsync(ct).ConfigureAwait(false); // releases the application lock too

            ok = new CommitResult(true, CommitFailure.None,
                $"Committed batch {batchId}: {inserted:N0} new, {updated:N0} updated, " +
                $"{exact:N0} exact duplicates, {fileDup:N0} duplicates inside the file, " +
                $"{skippedConflicts:N0} conflicts kept as they were, {invalid:N0} invalid rows rejected.",
                inserted, updated, exact, fileDup, skippedConflicts, invalid);
        }

        // g. Outside the lock and the transaction: the staging rows are no longer needed. A failure here is
        //    harmless (CleanupStaleBatchesAsync removes leftovers of Committed batches later).
        try
        {
            await using var cleanup = await _factory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            cleanup.Database.SetCommandTimeout(CommandTimeoutSeconds);
            await DeleteStagingRowsAsync(cleanup, batchId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Batch {BatchId} committed, but its staging rows could not be deleted; cleanup will remove them.", batchId);
        }

        return ok with { ElapsedSeconds = sw.Elapsed.TotalSeconds };
    }

    public async Task<CleanupResult> CleanupStaleBatchesAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);

        // Age is judged by the server clock (ImportedAt defaults to SYSUTCDATETIME()), not this PC's.
        var staleHours = (int)StaleAfter.TotalHours;
        var stale = await db.Database.SqlQuery<int>($"""
            SELECT Id AS Value FROM dbo.ImportBatches
            WHERE Status = 'Staged' AND ImportedAt < DATEADD(HOUR, {-staleHours}, SYSUTCDATETIME())
            """).ToListAsync(ct).ConfigureAwait(false);

        // DiscardAsync flips the status only while the batch is still 'Staged'.
        foreach (var id in stale) await DiscardAsync(id, ct).ConfigureAwait(false);

        var leftovers = await db.Database.SqlQuery<int>($"""
            SELECT b.Id AS Value FROM dbo.ImportBatches AS b
            WHERE b.Status IN ('Committed', 'Discarded')
              AND EXISTS (SELECT 1 FROM dbo.ImportStaging AS s WHERE s.ImportBatchId = b.Id)
            """).ToListAsync(ct).ConfigureAwait(false);

        foreach (var id in leftovers) await DeleteStagingRowsAsync(db, id, ct).ConfigureAwait(false);

        return new CleanupResult(stale.Count, leftovers.Count);
    }
}
