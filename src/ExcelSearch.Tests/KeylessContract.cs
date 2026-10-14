using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using Xunit.Abstractions;
using static ExcelSearch.Tests.StagingEnv;

namespace ExcelSearch.Tests;

/// <summary>
/// Rows WITHOUT a pay order no / txn ref (bank-statement sheets): duplicates are flagged, never blocked, and are imported unless the user
/// asks to skip them. Rows that have a full key keep their old rules. Written once, run on both providers (see ContractTestShells).
/// </summary>
public abstract class KeylessContract
{
    private readonly ITestOutputHelper _output;

    protected KeylessContract(ITestOutputHelper output) => _output = output;

    protected abstract StagingEnv NewEnv(bool large = false);

    /// <summary>
    /// File A is committed: one key-less row (slip S1) and one keyed row. Then file B is staged against it:
    ///   row 2  identical to A's key-less row                      -> Already in database   (flagged)
    ///   row 3  same slip, date, account, amount; other text differs -> Possible duplicate    (flagged)
    ///   row 4  a new slip                                           -> New
    ///   row 5,6 identical repeats of row 4                          -> Repeated in file      (flagged)
    ///   row 7  a new keyed row                                      -> New
    ///   row 8  identical to A's keyed row                           -> Already in database   (keyed: always skipped)
    /// </summary>
    private static async Task<(int A, int B)> StageScenarioAsync(StagingEnv env)
    {
        var acct = env.Prefix;
        var a = await env.StageAsync(new[]
        {
            KeylessRow(2, acct, "11111111", 1000m),
            Row(3, env.Key("k"), "T1", 5m),
        });
        Assert.True((await env.Store.CommitAsync(a)).Success);

        var b = await env.StageAsync(new[]
        {
            KeylessRow(2, acct, "11111111", 1000m),
            KeylessRow(3, acct, "11111111", 1000m, narration2: "changed"),
            KeylessRow(4, acct, "22222222", 2000m),
            KeylessRow(5, acct, "22222222", 2000m),
            KeylessRow(6, acct, "22222222", 2000m),
            Row(7, env.Key("k2"), "T1", 6m),
            Row(8, env.Key("k"), "T1", 5m),
        });
        return (a, b);
    }

    [SkippableFact]
    public async Task Keyless_rows_are_flagged_as_duplicates_and_keyed_rows_keep_their_rules()
    {
        await using var env = NewEnv();
        var (_, b) = await StageScenarioAsync(env);

        var counts = await env.Store.GetCountsAsync(b);
        Assert.Equal(2, counts[RowStatus.New]);                 // row 4 (first copy) and the new keyed row
        Assert.Equal(2, counts[RowStatus.ExactDuplicate]);      // row 2 (key-less) and row 8 (keyed)
        Assert.Equal(1, counts[RowStatus.LikelyDuplicate]);     // row 3
        Assert.Equal(2, counts[RowStatus.FileDuplicate]);       // rows 5 and 6
        Assert.Equal(0, counts[RowStatus.Invalid]);
        Assert.Equal(0, counts[RowStatus.Conflict]);

        // The flagged rows a commit would import: row 2, 3, 5, 6 (the keyed duplicate is never imported).
        Assert.Equal(4, await env.Store.GetFlaggedKeylessCountAsync(b));
        Assert.Equal((byte)RowStatus.LikelyDuplicate, (await env.StagingRowAsync(b, 3)).Status);
    }

    [SkippableTheory]
    [InlineData(false, 6, 4, 1)]   // flagged duplicates are imported: 2 new + 4 flagged; only the keyed duplicate is skipped
    [InlineData(true, 2, 0, 5)]    // skipped on request: just the 2 new rows; 5 rows left out
    public async Task Commit_imports_flagged_keyless_duplicates_unless_asked_to_skip_them(bool skip, int inserted, int flagged, int skippedRows)
    {
        await using var env = NewEnv();
        var (_, b) = await StageScenarioAsync(env);

        var result = await env.Store.CommitAsync(b, skip);

        Assert.True(result.Success, result.Message);
        _output.WriteLine(result.Message);
        Assert.Equal(inserted, await env.CountTransactionsAsync(b));
        Assert.Equal(2, result.NewRows);                         // flagged rows that were imported are counted separately
        Assert.Equal(flagged, result.ImportedFlaggedDuplicates);
        Assert.Equal(skippedRows, result.SkippedExactDuplicates + result.SkippedFileDuplicates + result.SkippedLikelyDuplicates + result.SkippedConflicts);

        var batch = await env.GetBatchAsync(b);
        Assert.Equal(inserted, batch.NewRows);                   // ImportBatches counts every row written
        Assert.Equal(skippedRows, batch.SkippedRows);            // and every valid row left out
    }

    [SkippableFact]
    public async Task Recheck_recomputes_keyless_rows_against_the_database()
    {
        await using var env = NewEnv();
        var (a, b) = await StageScenarioAsync(env);

        // Another user deletes file A's rows: the key-less duplicates of them are no longer duplicates of anything.
        await env.DeleteTransactionsOfBatchAsync(a);
        var result = await env.Store.RefreshAgainstDatabaseAsync(b);

        Assert.True(result.Success);
        var counts = await env.Store.GetCountsAsync(b);
        Assert.Equal(5, counts[RowStatus.New]);                 // row 2, 3, 4, the new keyed row and the (now new) keyed row 8
        Assert.Equal(0, counts[RowStatus.ExactDuplicate]);
        Assert.Equal(0, counts[RowStatus.LikelyDuplicate]);
        Assert.Equal(2, counts[RowStatus.FileDuplicate]);       // repeats inside the file do not depend on the database
    }
}
