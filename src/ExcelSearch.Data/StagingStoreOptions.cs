namespace ExcelSearch.Data;

/// <summary>Settings for <see cref="SqlServerStagingStore"/>, read from the app configuration (top-level keys).</summary>
public sealed class StagingStoreOptions
{
    public const int DefaultCommitLockTimeoutSeconds = 120;

    /// <summary>
    /// How long a commit waits for another commit to finish before giving up with "try again in a moment".
    /// Should be longer than the slowest commit you expect (200,000 rows take about 30 s).
    /// </summary>
    public int CommitLockTimeoutSeconds { get; init; } = DefaultCommitLockTimeoutSeconds;
}
