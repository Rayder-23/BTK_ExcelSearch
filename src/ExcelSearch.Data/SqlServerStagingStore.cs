using System.Diagnostics;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.Data;

/// <summary>
/// SQL Server implementation of <see cref="IStagingStore"/>. Hand-written (not scaffolded), so it survives
/// a re-scaffold. Every statement filters on ImportBatchId, so concurrent batches never touch each other.
/// A fresh short-lived context is created per operation/chunk from the factory; nothing is kept between calls.
/// </summary>
public sealed partial class SqlServerStagingStore : IStagingStore
{
    private const int BulkBatchSize = 10_000;
    private const int ProgressEvery = 5_000;
    private const int DeleteChunk = 50_000;

    // Below SQL Server's ~5,000-lock escalation threshold, so a classify chunk never locks the whole table
    // and blocks other users who are loading their own batches.
    private const int ClassifyChunk = 4_000;

    private const int CommandTimeoutSeconds = 600;
    private const int ErrorTextMax = ImportRowDataReader.ErrorTextMax;

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<SqlServerStagingStore>? _logger;

    public SqlServerStagingStore(IDbContextFactory<AppDbContext> factory, ILogger<SqlServerStagingStore>? logger = null)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<int> CreateBatchAsync(string fileName, string? sheetName, string fileHash, string? importedBy,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var batch = new ImportBatch
        {
            FileName = Cut(fileName, 260)!,
            SheetName = Cut(sheetName, 100),
            FileHash = fileHash,
            ImportedBy = Cut(importedBy, 256),
            Status = "Staged",
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return batch.Id;
    }

    public async Task<LoadResult> LoadRowsAsync(int batchId, IEnumerable<ImportRow> rows, IProgress<int>? progress,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var loaded = 0;

        try
        {
            // SqlBulkCopy needs a raw SqlConnection; take it from a context made by the factory so the
            // connection string still lives in one place. The context owns (and disposes) the connection.
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
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

    public async Task ClassifyAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
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
                        SELECT STRING_AGG(CAST(o.ExcelRowNumber AS NVARCHAR(10)), N', ')
                               WITHIN GROUP (ORDER BY o.ExcelRowNumber)
                        FROM dbo.ImportStaging AS o
                        WHERE o.ImportBatchId = {batchId}
                          AND o.PayOrderNo = g.PayOrderNo AND o.TxnRefSeqNo = g.TxnRefSeqNo
                          AND o.ExcelRowNumber <> g.ExcelRowNumber)
                    + N' in this file, but the values differ', {ErrorTextMax}) END
            FROM dbo.ImportStaging AS s
            JOIN g ON g.ExcelRowNumber = s.ExcelRowNumber
            WHERE s.ImportBatchId = {batchId}
              AND g.Cnt > 1 AND (g.MinHash <> g.MaxHash OR g.Rn > 1);
            """, ct).ConfigureAwait(false);

        // (b) Rows still Pending: compare with dbo.Transactions on the full business key.
        //     No match -> New (2); same hash -> ExactDuplicate (3); different hash -> Conflict (5),
        //     remembering the hash we saw so commit can detect that the row changed since the preview.
        //     Done in row-number ranges (see ClassifyChunk) to avoid table-level lock escalation.
        var maxRow = await db.ImportStagings
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Pending)
            .MaxAsync(s => (int?)s.ExcelRowNumber, ct).ConfigureAwait(false);

        for (var lo = 0; maxRow is not null && lo <= maxRow; lo += ClassifyChunk)
        {
            var hi = lo + ClassifyChunk - 1;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE s
                SET Status = CASE WHEN t.PayOrderNo IS NULL THEN 2
                                  WHEN t.RowHash = s.RowHash THEN 3
                                  ELSE 5 END,
                    ExistingRowHash = CASE WHEN t.PayOrderNo IS NULL THEN NULL ELSE t.RowHash END
                FROM dbo.ImportStaging AS s
                LEFT JOIN dbo.Transactions AS t
                       ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo
                WHERE s.ImportBatchId = {batchId}
                  AND s.Status = 0
                  AND s.ExcelRowNumber BETWEEN {lo} AND {hi};
                """, ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<RowStatus, int>> GetCountsAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var grouped = await db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId)
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = Enum.GetValues<RowStatus>().ToDictionary(s => s, _ => 0);
        foreach (var g in grouped) result[(RowStatus)g.Status] = g.Count;
        return result;
    }

    public async Task<PagedRows> GetPageAsync(int batchId, RowStatus status, int pageNumber, int pageSize,
        CancellationToken ct = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber), "Pages are 1-based.");
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)status);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var entities = await query.OrderBy(s => s.ExcelRowNumber)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return new PagedRows(entities.Select(ToStagedRow).ToList(), total);
    }

    public async Task DiscardAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);

        await DeleteStagingRowsAsync(db, batchId, ct).ConfigureAwait(false);

        // Only a batch that is still 'Staged' can be discarded; a Committed batch keeps its status.
        await db.ImportBatches.Where(b => b.Id == batchId && b.Status == "Staged")
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Discarded"), ct).ConfigureAwait(false);
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

    private static string? Cut(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static StagedRow ToStagedRow(ImportStaging s) => new()
    {
        ExcelRowNumber = s.ExcelRowNumber,
        Status = (RowStatus)s.Status,
        Overwrite = s.Overwrite,
        ErrorText = s.ErrorText,
        RowHash = s.RowHash,
        ExistingRowHash = s.ExistingRowHash,
        ValueDate = s.ValueDate?.ToDateTime(TimeOnly.MinValue),
        BranchCode = s.BranchCode,
        BranchName = s.BranchName,
        RegNo = s.RegNo,
        ApplicationNo = s.ApplicationNo,
        PlotNo = s.PlotNo,
        StreetNo = s.StreetNo,
        ChallanNo = s.ChallanNo,
        ChequeInstNo = s.ChequeInstNo,
        CustomerName = s.CustomerName,
        Cnic = s.Cnic,
        PayOrderNo = s.PayOrderNo,
        TxnRefSeqNo = s.TxnRefSeqNo,
        Discount = s.Discount,
        DownPaymentAmount = s.DownPaymentAmount,
        Debit = s.Debit,
        Credit = s.Credit,
        RunningBalance = s.RunningBalance,
        BankName = s.BankName,
        AccountNo = s.AccountNo,
        Project = s.Project,
        DealerName = s.DealerName,
        DataSource = s.DataSource,
        Narration1 = s.Narration1,
        Narration2 = s.Narration2,
        Narration3 = s.Narration3,
        Narration4 = s.Narration4,
        Narration5 = s.Narration5,
    };
}
