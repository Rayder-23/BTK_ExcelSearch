using ExcelSearch.Core.Import;

namespace ExcelSearch.Core.Staging;

/// <summary>A staged row as read back for the preview screen (a flat copy of one ImportStaging row).</summary>
public sealed class StagedRow
{
    public int ExcelRowNumber { get; init; }
    public RowStatus Status { get; init; }
    public bool Overwrite { get; init; }
    public string? ErrorText { get; init; }
    public string? RowHash { get; init; }

    /// <summary>For Conflict rows: the hash of the Transactions row as it was when the batch was classified.</summary>
    public string? ExistingRowHash { get; init; }

    public DateTime? ValueDate { get; init; }
    public string? BranchCode { get; init; }
    public string? BranchName { get; init; }
    public string? RegNo { get; init; }
    public string? ApplicationNo { get; init; }
    public string? PlotNo { get; init; }
    public string? StreetNo { get; init; }
    public string? ChallanNo { get; init; }
    public string? ChequeInstNo { get; init; }
    public string? CustomerName { get; init; }
    public string? Cnic { get; init; }
    public string? PayOrderNo { get; init; }
    public string? TxnRefSeqNo { get; init; }
    public decimal? Discount { get; init; }
    public decimal? DownPaymentAmount { get; init; }
    public decimal? Debit { get; init; }
    public decimal? Credit { get; init; }
    public decimal? RunningBalance { get; init; }
    public string? BankName { get; init; }
    public string? AccountNo { get; init; }
    public string? Project { get; init; }
    public string? DealerName { get; init; }
    public string? DataSource { get; init; }
    public string? Narration1 { get; init; }
    public string? Narration2 { get; init; }
    public string? Narration3 { get; init; }
    public string? Narration4 { get; init; }
    public string? Narration5 { get; init; }
    public string? SlipNo { get; init; }
    public string? NarrationCnic { get; init; }
}

/// <summary>One page of staged rows plus the total number of rows with that status.</summary>
public sealed record PagedRows(IReadOnlyList<StagedRow> Rows, int Total);

/// <summary>Outcome of loading a stream of rows into staging.</summary>
public sealed record LoadResult(int RowsLoaded, TimeSpan Elapsed)
{
    public double RowsPerSecond => Elapsed.TotalSeconds > 0 ? RowsLoaded / Elapsed.TotalSeconds : 0;
}

/// <summary>
/// Staging-table operations. EVERY method is scoped by batch id, so several users can stage and discard
/// different batches at the same time without touching each other's rows.
/// </summary>
public interface IStagingStore : IInvalidRowSource
{
    /// <summary>Creates an ImportBatches row with Status 'Staged' and returns its id.</summary>
    Task<int> CreateBatchAsync(string fileName, string? sheetName, string fileHash, string? importedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Writes the rows to staging in chunks, so the (lazy) stream is never held in memory.
    /// Rows with errors are stored as Invalid with their messages; the rest as Pending with their hash.
    /// <paramref name="progress"/> receives the number of rows loaded so far.
    /// </summary>
    Task<LoadResult> LoadRowsAsync(int batchId, IEnumerable<ImportRow> rows, IProgress<int>? progress,
        CancellationToken ct = default);

    /// <summary>Set-based classification of the batch's Pending rows (see RowStatus).</summary>
    Task ClassifyAsync(int batchId, CancellationToken ct = default);

    /// <summary>Row count per status; every status is present, with 0 when there are none.</summary>
    Task<IReadOnlyDictionary<RowStatus, int>> GetCountsAsync(int batchId, CancellationToken ct = default);

    /// <summary>A page (1-based) of the batch's rows with the given status, ordered by ExcelRowNumber.</summary>
    Task<PagedRows> GetPageAsync(int batchId, RowStatus status, int pageNumber, int pageSize,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a 'Staged' batch 'Discarded' and only then deletes its staging rows (in chunks). A batch that is
    /// already committed or discarded is refused (nothing deleted). Never touches Transactions.
    /// </summary>
    Task<DiscardResult> DiscardAsync(int batchId, CancellationToken ct = default);

    /// <summary>
    /// Marks Conflict rows (status 5) of this batch to be overwritten (or not) on commit. A null
    /// <paramref name="excelRowNumbers"/> means every Conflict row. Returns how many rows were set.
    /// </summary>
    Task<int> SetConflictOverwriteAsync(int batchId, bool overwrite, IReadOnlyCollection<int>? excelRowNumbers,
        CancellationToken ct = default);

    /// <summary>
    /// Writes the batch into Transactions in one transaction, under an exclusive application lock so two
    /// commits never run at once. Expected failures are returned in the result, not thrown.
    /// Key-less rows flagged as duplicates (Already in database, Repeated in file, Possible duplicate) are
    /// imported too, unless <paramref name="skipFlaggedDuplicates"/> is true. Rows with a full key that
    /// duplicate something are always skipped.
    /// </summary>
    Task<CommitResult> CommitAsync(int batchId, bool skipFlaggedDuplicates = false, CancellationToken ct = default);

    /// <summary>
    /// Housekeeping: discards 'Staged' batches older than 24 hours, and deletes leftover staging rows of
    /// 'Committed'/'Discarded' batches. A recent 'Staged' batch (someone may be working on it) is never touched.
    /// </summary>
    Task<CleanupResult> CleanupStaleBatchesAsync(CancellationToken ct = default);

    /// <summary>
    /// A page (1-based) of Conflict rows, each with the existing Transactions row compared column by column,
    /// so the screen can show exactly what differs.
    /// </summary>
    Task<PagedConflicts> GetConflictPageAsync(int batchId, int pageNumber, int pageSize, CancellationToken ct = default);

    /// <summary>How many Conflict rows the batch has and how many are set to overwrite, read from the database.</summary>
    Task<ConflictCounts> GetConflictCountsAsync(int batchId, CancellationToken ct = default);

    /// <summary>
    /// How many rows without a full key are flagged as duplicates (Already in database, Repeated in file, Possible duplicate):
    /// the rows a commit imports unless the user chooses to skip flagged duplicates.
    /// </summary>
    Task<int> GetFlaggedKeylessCountAsync(int batchId, CancellationToken ct = default);

    /// <summary>
    /// Re-compares the batch's New, Already-in-database and Conflict rows with Transactions as it is now (other users may have
    /// committed since the preview). Only for a 'Staged' batch; otherwise a refusal is returned. Invalid and repeated-in-file rows are not touched.
    /// </summary>
    Task<RefreshResult> RefreshAgainstDatabaseAsync(int batchId, CancellationToken ct = default);
}
