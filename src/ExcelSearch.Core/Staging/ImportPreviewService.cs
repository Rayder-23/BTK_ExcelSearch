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
public sealed class ImportPreviewService
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

    /// <summary>Sets the Overwrite flag on Conflict rows (null row numbers = all of them).</summary>
    public Task<int> SetConflictOverwriteAsync(int batchId, bool overwrite, IReadOnlyCollection<int>? excelRowNumbers = null,
        CancellationToken ct = default) => _store.SetConflictOverwriteAsync(batchId, overwrite, excelRowNumbers, ct);

    /// <summary>Commits a staged batch into Transactions.</summary>
    public Task<CommitResult> CommitAsync(int batchId, CancellationToken ct = default) => _store.CommitAsync(batchId, ct);

    /// <summary>Removes abandoned batches and leftover staging rows; safe to run at any time.</summary>
    public Task<CleanupResult> CleanupStaleBatchesAsync(CancellationToken ct = default) => _store.CleanupStaleBatchesAsync(ct);
}
