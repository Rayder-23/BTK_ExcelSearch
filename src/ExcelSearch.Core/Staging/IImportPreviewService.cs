namespace ExcelSearch.Core.Staging;

/// <summary>
/// What the import screen needs from the application layer. The ViewModels depend on this interface, not on
/// ImportPreviewService, so tests can hand them a fake with no database or Excel file.
/// </summary>
public interface IImportPreviewService
{
    /// <summary>Parse -> create batch -> load -> classify. A rejected file comes back with FileErrors and no batch.</summary>
    Task<StageResult> StageFileAsync(string path, string? importedBy, IProgress<int>? progress,
        CancellationToken ct = default);

    /// <summary>One page (1-based) of the batch's rows with the given status, sorted over the whole status (default: file order).</summary>
    Task<PagedRows> GetPageAsync(int batchId, RowStatus status, int pageNumber, int pageSize,
        StagedSort sort = default, CancellationToken ct = default);

    Task<int> SetConflictOverwriteAsync(int batchId, bool overwrite, IReadOnlyCollection<int>? excelRowNumbers = null,
        CancellationToken ct = default);

    Task<CommitResult> CommitAsync(int batchId, bool skipFlaggedDuplicates = false, CancellationToken ct = default);

    Task<DiscardResult> DiscardAsync(int batchId, CancellationToken ct = default);

    Task<CleanupResult> CleanupStaleBatchesAsync(CancellationToken ct = default);

    /// <summary>One page (1-based) of Conflict rows with what differs from the row already in the database.</summary>
    Task<PagedConflicts> GetConflictPageAsync(int batchId, int pageNumber, int pageSize, StagedSort sort = default,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<RowStatus, int>> GetCountsAsync(int batchId, CancellationToken ct = default);

    /// <summary>Conflict totals (all conflicts, and how many will overwrite) straight from the database.</summary>
    Task<ConflictCounts> GetConflictCountsAsync(int batchId, CancellationToken ct = default);

    /// <summary>Key-less rows flagged as duplicates: imported on commit unless flagged duplicates are skipped.</summary>
    Task<int> GetFlaggedKeylessCountAsync(int batchId, CancellationToken ct = default);

    /// <summary>Re-checks the batch against the database; refused (not thrown) if it is not 'Staged'.</summary>
    Task<RefreshResult> RefreshAgainstDatabaseAsync(int batchId, CancellationToken ct = default);
}
