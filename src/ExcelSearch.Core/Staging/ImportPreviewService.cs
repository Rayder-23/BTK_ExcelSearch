using System.Diagnostics;
using ExcelSearch.Core.Import;

namespace ExcelSearch.Core.Staging;

/// <summary>Result of staging a file. BatchId is null when the file itself was rejected (see FileErrors).</summary>
public sealed record StageResult(
    int? BatchId,
    IReadOnlyList<string> FileErrors,
    IReadOnlyDictionary<RowStatus, int> Counts,
    int RowsLoaded,
    double LoadRowsPerSecond,
    TimeSpan TotalElapsed);

/// <summary>Parse -> create batch -> load -> classify. Nothing is written to Transactions.</summary>
public sealed class ImportPreviewService : IImportPreviewService
{
    private readonly IExcelParser _parser;
    private readonly IStagingStore _store;

    public ImportPreviewService(IExcelParser parser, IStagingStore store)
    {
        _parser = parser;
        _store = store;
    }

    /// <summary>
    /// The operating-system user (DOMAIN\user) recorded as ImportedBy. Deliberately NOT the SQL login:
    /// everyone connects as sa, so the login says nothing about who imported.
    /// </summary>
    public static string CurrentUser() => Environment.UserDomainName + "\\" + Environment.UserName;

    /// <param name="progress">Rows loaded into staging so far.</param>
    public async Task<StageResult> StageFileAsync(string path, string? importedBy, IProgress<int>? progress,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var empty = new Dictionary<RowStatus, int>();

        // Hashing the file and finding the header are synchronous I/O: keep them off the caller's thread.
        using var file = await Task.Run(() => _parser.Parse(path, null, ct), ct).ConfigureAwait(false);
        if (file.FileErrors.Count > 0)
            return new StageResult(null, file.FileErrors.ToList(), empty, 0, 0, sw.Elapsed);

        var batchId = await _store.CreateBatchAsync(file.FileName, file.SheetName, file.FileHash, importedBy, ct)
            .ConfigureAwait(false);
        try
        {
            var load = await _store.LoadRowsAsync(batchId, file.Rows, progress, ct).ConfigureAwait(false);
            await _store.ClassifyAsync(batchId, ct).ConfigureAwait(false);
            var counts = await _store.GetCountsAsync(batchId, ct).ConfigureAwait(false);
            return new StageResult(batchId, Array.Empty<string>(), counts, load.RowsLoaded, load.RowsPerSecond, sw.Elapsed);
        }
        catch
        {
            // Cancelled or failed half way: do not leave a partial batch behind.
            await _store.DiscardAsync(batchId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A page (1-based) of the batch's rows with the given status.</summary>
    public Task<PagedRows> GetPageAsync(int batchId, RowStatus status, int pageNumber, int pageSize,
        StagedSort sort = default, CancellationToken ct = default) =>
        _store.GetPageAsync(batchId, status, pageNumber, pageSize, sort, ct);

    /// <summary>Sets the Overwrite flag on Conflict rows (null row numbers = all of them).</summary>
    public Task<int> SetConflictOverwriteAsync(int batchId, bool overwrite, IReadOnlyCollection<int>? excelRowNumbers = null,
        CancellationToken ct = default) => _store.SetConflictOverwriteAsync(batchId, overwrite, excelRowNumbers, ct);

    /// <summary>Commits a staged batch into Transactions.</summary>
    public Task<CommitResult> CommitAsync(int batchId, bool skipFlaggedDuplicates = false, CancellationToken ct = default) =>
        _store.CommitAsync(batchId, skipFlaggedDuplicates, ct);

    /// <summary>Discards a staged batch; refused (not thrown) if it was already committed or discarded.</summary>
    public Task<DiscardResult> DiscardAsync(int batchId, CancellationToken ct = default) => _store.DiscardAsync(batchId, ct);

    /// <summary>Removes abandoned batches and leftover staging rows; safe to run at any time.</summary>
    public Task<CleanupResult> CleanupStaleBatchesAsync(CancellationToken ct = default) => _store.CleanupStaleBatchesAsync(ct);

    public Task<PagedConflicts> GetConflictPageAsync(int batchId, int pageNumber, int pageSize, StagedSort sort = default,
        CancellationToken ct = default) => _store.GetConflictPageAsync(batchId, pageNumber, pageSize, sort, ct);

    public Task<IReadOnlyDictionary<RowStatus, int>> GetCountsAsync(int batchId, CancellationToken ct = default) =>
        _store.GetCountsAsync(batchId, ct);

    public Task<ConflictCounts> GetConflictCountsAsync(int batchId, CancellationToken ct = default) =>
        _store.GetConflictCountsAsync(batchId, ct);

    public Task<int> GetFlaggedKeylessCountAsync(int batchId, CancellationToken ct = default) =>
        _store.GetFlaggedKeylessCountAsync(batchId, ct);

    public Task<RefreshResult> RefreshAgainstDatabaseAsync(int batchId, CancellationToken ct = default) =>
        _store.RefreshAgainstDatabaseAsync(batchId, ct);
}
