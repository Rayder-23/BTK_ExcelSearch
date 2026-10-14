namespace ExcelSearch.Core.Staging;

/// <summary>How many rows moved from one status to another during a re-check.</summary>
public sealed record StatusMove(RowStatus From, RowStatus To, int Count);

/// <summary>
/// Outcome of RefreshAgainstDatabaseAsync. A refusal (batch not 'Staged') is an expected answer, not an error.
/// </summary>
public sealed record RefreshResult(
    bool Success,
    string? RefusalReason,
    IReadOnlyList<StatusMove> Moves,
    int OverwriteChoicesCleared = 0,
    double ElapsedSeconds = 0)
{
    public int RowsChanged => Moves.Sum(m => m.Count);

    public static RefreshResult Refused(string reason) => new(false, reason, Array.Empty<StatusMove>());

    /// <summary>"12 rows changed: 10 New → Already in database, 2 New → Conflict." (or a no-change sentence).</summary>
    public string Describe()
    {
        if (!Success) return RefusalReason ?? "The batch could not be re-checked.";

        var cleared = OverwriteChoicesCleared > 0
            ? $" {OverwriteChoicesCleared:N0} overwrite choice(s) were cleared because the database row changed."
            : "";
        if (RowsChanged == 0)
            return "Nothing changed: the preview still matches the database." + cleared;

        var parts = Moves.OrderByDescending(m => m.Count)
            .Select(m => $"{m.Count:N0} {Name(m.From)} → {Name(m.To)}");
        return $"{RowsChanged:N0} row{(RowsChanged == 1 ? "" : "s")} changed: {string.Join(", ", parts)}." + cleared;
    }

    /// <summary>The names the screen uses for statuses.</summary>
    public static string Name(RowStatus s) => s switch
    {
        RowStatus.New => "New",
        RowStatus.ExactDuplicate => "Already in database",
        RowStatus.Conflict => "Conflict",
        RowStatus.Invalid => "Invalid",
        RowStatus.FileDuplicate => "Repeated in file",
        RowStatus.LikelyDuplicate => "Possible duplicate",
        _ => s.ToString(),
    };
}

/// <summary>How many Conflict rows a batch has, and how many of them are set to overwrite (both read from the database).</summary>
public sealed record ConflictCounts(int Total, int Overwrite);

/// <summary>The batch facts the exporter needs: where the data came from, and whether the file can be trusted.</summary>
public sealed record BatchInfo(int Id, string FileName, string? SheetName, string FileHash, string Status);

/// <summary>An Invalid staged row's number and its error text (all the source-file export needs from staging).</summary>
public sealed record InvalidRowError(int ExcelRowNumber, string? ErrorText);

/// <summary>Read access to a batch's Invalid rows in keyset-paged chunks, so an export never loads them all at once.</summary>
public interface IInvalidRowSource
{
    Task<BatchInfo?> GetBatchAsync(int batchId, CancellationToken ct = default);

    /// <summary>Up to <paramref name="take"/> Invalid rows with ExcelRowNumber greater than <paramref name="afterRow"/>, ascending.</summary>
    Task<IReadOnlyList<InvalidRowError>> GetInvalidErrorsAfterAsync(int batchId, int afterRow, int take,
        CancellationToken ct = default);

    /// <summary>Same keyset paging, but with the parsed values too (used when the source file cannot be used).</summary>
    Task<IReadOnlyList<StagedRow>> GetInvalidRowsAfterAsync(int batchId, int afterRow, int take,
        CancellationToken ct = default);
}
