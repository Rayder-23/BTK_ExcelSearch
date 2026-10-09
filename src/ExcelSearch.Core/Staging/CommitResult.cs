namespace ExcelSearch.Core.Staging;

/// <summary>Why a commit did not happen. None means it succeeded.</summary>
public enum CommitFailure
{
    None = 0,
    /// <summary>Another import held the commit lock for more than a minute.</summary>
    LockTimeout = 1,
    /// <summary>The batch does not exist or is no longer 'Staged' (already committed or discarded).</summary>
    BatchNotStaged = 2,
    /// <summary>The staged rows do not add up (partial load) or some are still Pending (not classified).</summary>
    IncompleteLoad = 3,
    /// <summary>Another import changed Transactions after the preview, so what the user saw is no longer true.</summary>
    RowsChangedSincePreview = 4,
}

/// <summary>
/// Outcome of committing a batch. Expected failures come back here (Success = false); nothing is written then.
/// </summary>
public sealed record CommitResult(
    bool Success,
    CommitFailure FailureReason,
    string Message,
    int NewRows = 0,
    int UpdatedRows = 0,
    int SkippedExactDuplicates = 0,
    int SkippedFileDuplicates = 0,
    int SkippedConflicts = 0,
    int RejectedRows = 0,
    double ElapsedSeconds = 0);

/// <summary>What <see cref="IStagingStore.CleanupStaleBatchesAsync"/> did.</summary>
public sealed record CleanupResult(int StaleBatchesDiscarded, int LeftoverBatchesCleaned);
