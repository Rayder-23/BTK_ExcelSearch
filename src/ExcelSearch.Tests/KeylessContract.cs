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
    /// <summary>
    /// Cash rows (no reference at all): file A has two deposits of 240000 at branch 0786 (slips S1, S2). File B:
    ///   row 2  same date, amount, branch, NO slip                 -> Possible duplicate, reason 2 (date + amount + branch)
    ///   row 3  same date, amount, branch, a different slip        -> New (two different slips are two deposits)
    ///   row 4  same slip S1, date, account, amount (text differs)  -> Possible duplicate, reason 1 (slip)
    ///   row 5  same date and amount, another branch                -> New
    ///   row 6  like row 3, same file                              -> New (repeats inside the file are not flagged by this rule)
    /// </summary>
    [SkippableFact]
    public async Task Cash_rows_are_matched_by_date_amount_and_branch_but_not_when_the_slip_numbers_differ()
    {
        await using var env = NewEnv();
        var bank = env.Prefix + "-HBL";
        var a = await env.StageAsync(new[]
        {
            CashRow(2, bank, env.Prefix, "0786", "S0000001", 240000m),
            CashRow(3, bank, env.Prefix, "0786", "S0000002", 240000m),
        });
        Assert.True((await env.Store.CommitAsync(a)).Success);

        var b = await env.StageAsync(new[]
        {
            CashRow(2, bank, env.Prefix, "0786", null, 240000m),
            CashRow(3, bank, env.Prefix, "0786", "S0000003", 240000m),
            CashRow(4, bank, env.Prefix, "0786", "S0000001", 240000m, narration2: "changed"),
            CashRow(5, bank, env.Prefix, "0999", null, 240000m),
            CashRow(6, bank, env.Prefix, "0786", "S0000004", 240000m),
        });

        var counts = await env.Store.GetCountsAsync(b);
        Assert.Equal(3, counts[RowStatus.New]);
        Assert.Equal(2, counts[RowStatus.LikelyDuplicate]);
        Assert.Equal(0, counts[RowStatus.FileDuplicate]);
        Assert.Equal((byte?)2, (await env.StagingRowAsync(b, 2)).MatchReason);
        Assert.Equal((byte?)1, (await env.StagingRowAsync(b, 4)).MatchReason);
        Assert.Null((await env.StagingRowAsync(b, 3)).MatchReason);

        // Another user deletes file A's rows: nothing is left to match, and the reasons are cleared.
        await env.DeleteTransactionsOfBatchAsync(a);
        Assert.True((await env.Store.RefreshAgainstDatabaseAsync(b)).Success);
        Assert.Equal(5, (await env.Store.GetCountsAsync(b))[RowStatus.New]);
        Assert.Null((await env.StagingRowAsync(b, 2)).MatchReason);
    }

    /// <summary>
    /// Rows with a Txn Ref but no pay order no (UBL): the ref identifies them. File A has txn T1. File B: T1 identical -> Already in
    /// database; T1 with other text -> Possible duplicate (reason 3); a NEW ref with the same date, amount and branch -> New (a ref
    /// is never matched by date and amount).
    /// </summary>
    [SkippableFact]
    public async Task Rows_with_only_a_txn_ref_are_recognised_by_that_ref()
    {
        await using var env = NewEnv();
        var bank = env.Prefix + "-UBL";
        var t1 = env.Key("t1");
        var a = await env.StageAsync(new[] { CashRow(2, bank, env.Prefix, "1872", null, 5000m, txnRef: t1) });
        Assert.True((await env.Store.CommitAsync(a)).Success);

        var b = await env.StageAsync(new[]
        {
            CashRow(2, bank, env.Prefix, "1872", null, 5000m, txnRef: t1),
            CashRow(3, bank, env.Prefix, "1872", null, 5000m, txnRef: t1, narration2: "changed"),
            CashRow(4, bank, env.Prefix, "1872", null, 5000m, txnRef: env.Key("t2")),
        });

        var counts = await env.Store.GetCountsAsync(b);
        Assert.Equal(1, counts[RowStatus.ExactDuplicate]);
        Assert.Equal(1, counts[RowStatus.LikelyDuplicate]);
        Assert.Equal(1, counts[RowStatus.New]);
        Assert.Equal((byte?)3, (await env.StagingRowAsync(b, 3)).MatchReason);
    }
}
