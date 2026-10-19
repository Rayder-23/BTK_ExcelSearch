using System.Diagnostics;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.Data.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IStagingStore"/>. Hand-written (not scaffolded), so it survives
/// a re-scaffold. Every statement filters on ImportBatchId, so concurrent batches never touch each other.
/// A fresh short-lived context is created per operation/chunk from the factory; nothing is kept between calls.
/// </summary>
public sealed partial class SqlServerStagingStore : StagingStoreBase
{
    private const int BulkBatchSize = 10_000;
    private const int ProgressEvery = 5_000;
    private const int DeleteChunk = 50_000;

    // Below SQL Server's ~5,000-lock escalation threshold, so a classify chunk never locks the whole table
    // and blocks other users who are loading their own batches.
    private const int ClassifyChunk = 4_000;

    private readonly ILogger<SqlServerStagingStore>? _logger;

    private readonly StagingStoreOptions _options;

    public SqlServerStagingStore(IDbContextFactory<AppDbContext> factory, ILogger<SqlServerStagingStore>? logger = null,
        StagingStoreOptions? options = null) : base(factory)
    {
        _logger = logger;
        _options = options ?? new StagingStoreOptions();
    }

    public override async Task<LoadResult> LoadRowsAsync(int batchId, IEnumerable<ImportRow> rows, IProgress<int>? progress,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var loaded = 0;

        try
        {
            // SqlBulkCopy needs a raw SqlConnection; take it from a context made by the factory so the
            // connection string still lives in one place. The context owns (and disposes) the connection.
            await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            var connection = (SqlConnection)db.Database.GetDbConnection();

            // Default options on purpose: no TableLock, so other users can stage their own batches meanwhile.
            using var bulk = new SqlBulkCopy(connection)
            {
                DestinationTableName = "dbo.ImportStaging",
                BatchSize = BulkBatchSize,           // each 10,000-row batch commits on its own
                EnableStreaming = true,              // read from our reader as we go instead of buffering it
                BulkCopyTimeout = CommandTimeoutSeconds,
                NotifyAfter = ProgressEvery,
            };
            foreach (var name in ImportRowDataReader.ColumnNames)
                bulk.ColumnMappings.Add(name, name); // by NAME, so column order never matters

            bulk.SqlRowsCopied += (_, e) => progress?.Report((int)e.RowsCopied);

            // The reader pulls the lazy row stream one row at a time; no list or DataTable of the file exists.
            using var reader = new ImportRowDataReader(batchId, rows);
            await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
            loaded = reader.RowsRead;
            progress?.Report(loaded);

            await db.ImportBatches.Where(b => b.Id == batchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.TotalRows, loaded), ct).ConfigureAwait(false);
        }
        catch
        {
            // Batches already committed by SqlBulkCopy stay in the table, so never leave this one as 'Staged'.
            await DiscardAsync(batchId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        sw.Stop();
        return new LoadResult(loaded, sw.Elapsed);
    }

    public override async Task ClassifyAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);

        // (a) Repeats of the same key inside this file, among Pending rows only.
        //     COUNT(DISTINCT) is not allowed in window functions, so "all hashes identical" is MIN = MAX.
        //     Identical group -> every row after the first (lowest ExcelRowNumber) is a FileDuplicate (4).
        //     Differing group -> every row is Invalid (1), with the other row numbers in ErrorText.
        //     Only repeated rows are updated, so this touches few rows even for a huge file.
        //     (A CTE with window functions is not updatable, hence the join back to the staging table.)
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            ;WITH g AS (
                SELECT s.ExcelRowNumber, s.PayOrderNo, s.TxnRefSeqNo, s.Status, s.ErrorText,
                       COUNT(*)       OVER (PARTITION BY s.PayOrderNo, s.TxnRefSeqNo) AS Cnt,
                       MIN(s.RowHash) OVER (PARTITION BY s.PayOrderNo, s.TxnRefSeqNo) AS MinHash,
                       MAX(s.RowHash) OVER (PARTITION BY s.PayOrderNo, s.TxnRefSeqNo) AS MaxHash,
                       ROW_NUMBER()   OVER (PARTITION BY s.PayOrderNo, s.TxnRefSeqNo ORDER BY s.ExcelRowNumber) AS Rn
                FROM dbo.ImportStaging AS s
                WHERE s.ImportBatchId = {batchId}
                  AND s.Status = 0
                  AND s.PayOrderNo IS NOT NULL AND s.TxnRefSeqNo IS NOT NULL
            )
            UPDATE s
            SET Status = CASE WHEN g.MinHash = g.MaxHash THEN 4 ELSE 1 END,
                ErrorText = CASE WHEN g.MinHash = g.MaxHash THEN NULL ELSE LEFT(
                    N'Same PayOrderNo + TxnRefSeqNo as row(s) ' + (
                        -- FOR XML PATH instead of STRING_AGG (2017+): the app must also run on SQL Server 2012
                        SELECT STUFF((SELECT N', ' + CAST(o.ExcelRowNumber AS NVARCHAR(10))
                                      FROM dbo.ImportStaging AS o
                                      WHERE o.ImportBatchId = {batchId}
                                        AND o.PayOrderNo = g.PayOrderNo AND o.TxnRefSeqNo = g.TxnRefSeqNo
                                        AND o.ExcelRowNumber <> g.ExcelRowNumber
                                      ORDER BY o.ExcelRowNumber
                                      FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N''))
                    + N' in this file, but the values differ', {ErrorTextMax}) END
            FROM dbo.ImportStaging AS s
            JOIN g ON g.ExcelRowNumber = s.ExcelRowNumber
            WHERE s.ImportBatchId = {batchId}
              AND g.Cnt > 1 AND (g.MinHash <> g.MaxHash OR g.Rn > 1);
            """, ct).ConfigureAwait(false);

        // (a2) Repeats inside the file among rows WITHOUT a full key: identical content (same RowHash) repeats are
        //      FileDuplicates (the first copy stays Pending and is classified below). They are flagged, not rejected,
        //      and are imported unless the user chooses to skip flagged duplicates.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            ;WITH g AS (
                SELECT s.ExcelRowNumber,
                       ROW_NUMBER() OVER (PARTITION BY s.RowHash ORDER BY s.ExcelRowNumber) AS Rn
                FROM dbo.ImportStaging AS s
                WHERE s.ImportBatchId = {batchId}
                  AND s.Status = 0
                  AND (s.PayOrderNo IS NULL OR s.TxnRefSeqNo IS NULL)
            )
            UPDATE s
            SET Status = 4
            FROM dbo.ImportStaging AS s
            JOIN g ON g.ExcelRowNumber = s.ExcelRowNumber
            WHERE s.ImportBatchId = {batchId} AND g.Rn > 1;
            """, ct).ConfigureAwait(false);

        // (b) Rows still Pending: compare with dbo.Transactions (the rule is in StatusSql: by full key for keyed rows,
        //     by content and slip number for key-less ones). A keyed match remembers the hash we saw so commit can
        //     detect that the row changed since the preview.
        //     Done in row-number ranges (see ClassifyChunk) to avoid table-level lock escalation.
        var maxRow = await db.ImportStagings
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Pending)
            .MaxAsync(s => (int?)s.ExcelRowNumber, ct).ConfigureAwait(false);

        for (var lo = 0; maxRow is not null && lo <= maxRow; lo += ClassifyChunk)
        {
            var hi = lo + ClassifyChunk - 1;
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE s SET Status = " + StatusSql.NewStatus + ", ExistingRowHash = " + StatusSql.ExistingHash + ", " +
                "MatchReason = CASE WHEN " + StatusSql.NewStatus + " = 6 THEN m.R END " +
                "FROM dbo.ImportStaging AS s " +
                "LEFT JOIN dbo.Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo " +
                "CROSS APPLY (SELECT " + StatusSql.Reason + " AS R) AS m " +
                "WHERE s.ImportBatchId = {0} AND s.Status = 0 AND s.ExcelRowNumber BETWEEN {1} AND {2};",
                new object[] { batchId, lo, hi }, ct).ConfigureAwait(false);
        }
    }

    public override async Task<DiscardResult> DiscardAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);

        // Step 1: claim the batch with a conditional UPDATE (see TryClaimForDiscardAsync). If a commit is running it holds an
        // UPDLOCK on this row, so the UPDATE waits for the commit to finish, then sees 'Committed', affects 0 rows and we refuse.
        var refused = await TryClaimForDiscardAsync(db, batchId, ct).ConfigureAwait(false);
        if (refused is not null) return refused;

        // Step 2: only now remove the rows. Once the status is 'Discarded' no commit can pick this batch up, and if
        // we crash here, CleanupStaleBatchesAsync deletes the leftover rows of Discarded batches.
        await DeleteStagingRowsAsync(db, batchId, ct).ConfigureAwait(false);
        return new DiscardResult(true);
    }

    /// <summary>Small DELETE chunks keep locks below the escalation threshold, so other users' inserts are not blocked.</summary>
    private static async Task DeleteStagingRowsAsync(AppDbContext db, int batchId, CancellationToken ct)
    {
        int deleted;
        do
        {
            deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({DeleteChunk}) FROM dbo.ImportStaging WHERE ImportBatchId = {batchId}", ct)
                .ConfigureAwait(false);
        } while (deleted > 0);
    }
}
