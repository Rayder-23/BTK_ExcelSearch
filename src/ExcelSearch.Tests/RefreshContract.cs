using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using Microsoft.EntityFrameworkCore;
using static ExcelSearch.Tests.StagingEnv;

namespace ExcelSearch.Tests;

/// <summary>
/// RefreshAgainstDatabaseAsync and the conflict Overwrite flags/counts, written once and run against every provider.
/// Same rules as the other contract tests: unique TEST_ keys, every batch tracked, only the test's own rows deleted.
/// </summary>
public abstract class RefreshContract
{
    protected abstract StagingEnv NewEnv(bool large = false);

    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    private static Task<ExcelSearch.Data.Entities.ImportStaging> StagingRowAsync(StagingEnv env, int batchId, int excelRow) =>
        env.StagingRowAsync(batchId, excelRow);

    /// <summary>Stage one row whose key is already in Transactions with a different hash, so it is a Conflict (existing hash = HashA).</summary>
    private static async Task<int> StageConflictAsync(StagingEnv env, string key, int excelRow = 2)
    {
        var seed = await env.NewBatchAsync();
        await env.SeedTransactionAsync(seed, key, "T1", HashA);
        var id = await env.StageAsync(new[] { Row(excelRow, key, "T1", 5m) });
        Assert.Equal(RowStatus.Conflict, (RowStatus)(await StagingRowAsync(env, id, excelRow)).Status);
        return id;
    }

    [SkippableFact]
    public async Task New_row_committed_meanwhile_with_equal_values_becomes_ExactDuplicate()
    {
        await using var env = NewEnv();
        var key = env.Key("k");
        var row = Row(2, key, "T1", 5m);
        var id = await env.StageAsync(new[] { row });
        Assert.Equal((byte)RowStatus.New, (await StagingRowAsync(env, id, 2)).Status);

        await env.SeedTransactionAsync(await env.NewBatchAsync(), key, "T1", row.RowHash);   // someone imported the same row
        var result = await env.Store.RefreshAgainstDatabaseAsync(id);

        Assert.True(result.Success);
        var move = Assert.Single(result.Moves);
        Assert.Equal((RowStatus.New, RowStatus.ExactDuplicate, 1), (move.From, move.To, move.Count));
        var staged = await StagingRowAsync(env, id, 2);
        Assert.Equal((byte)RowStatus.ExactDuplicate, staged.Status);
        Assert.Equal(row.RowHash, staged.ExistingRowHash);
        Assert.StartsWith("1 row changed: 1 New → Already in database", result.Describe());
    }

    [SkippableFact]
    public async Task New_row_committed_meanwhile_with_different_values_becomes_Conflict_with_the_existing_hash_and_overwrite_reset()
    {
        await using var env = NewEnv();
        var key = env.Key("k");
        var id = await env.StageAsync(new[] { Row(2, key, "T1", 5m) });
        await env.SetStagingOverwriteAsync(id, 2, true);   // stray flag

        await env.SeedTransactionAsync(await env.NewBatchAsync(), key, "T1", HashA);
        var result = await env.Store.RefreshAgainstDatabaseAsync(id);

        var move = Assert.Single(result.Moves);
        Assert.Equal((RowStatus.New, RowStatus.Conflict, 1), (move.From, move.To, move.Count));
        var staged = await StagingRowAsync(env, id, 2);
        Assert.Equal((byte)RowStatus.Conflict, staged.Status);
        Assert.Equal(HashA, staged.ExistingRowHash);
        Assert.False(staged.Overwrite);                                  // never inherited: the user has not looked at this conflict
        Assert.Equal(1, result.OverwriteChoicesCleared);
    }

    [SkippableFact]
    public async Task Conflict_with_overwrite_whose_existing_row_did_not_change_keeps_overwrite()
    {
        await using var env = NewEnv();
        var id = await StageConflictAsync(env, env.Key("k"));
        await env.Store.SetConflictOverwriteAsync(id, true, null);

        var result = await env.Store.RefreshAgainstDatabaseAsync(id);

        Assert.True(result.Success);
        Assert.Empty(result.Moves);
        Assert.Equal(0, result.OverwriteChoicesCleared);
        var staged = await StagingRowAsync(env, id, 2);
        Assert.Equal((byte)RowStatus.Conflict, staged.Status);
        Assert.True(staged.Overwrite);
        Assert.StartsWith("Nothing changed", result.Describe());
    }

    [SkippableFact]
    public async Task Conflict_with_overwrite_whose_existing_row_changed_gets_overwrite_reset_and_the_new_existing_hash()
    {
        await using var env = NewEnv();
        var key = env.Key("k");
        var id = await StageConflictAsync(env, key);
        await env.Store.SetConflictOverwriteAsync(id, true, null);

        await env.SetTransactionHashAsync(HashB, key);
        var result = await env.Store.RefreshAgainstDatabaseAsync(id);

        Assert.Empty(result.Moves);                                      // still a Conflict: only the choice is cleared
        Assert.Equal(1, result.OverwriteChoicesCleared);
        var staged = await StagingRowAsync(env, id, 2);
        Assert.Equal((byte)RowStatus.Conflict, staged.Status);
        Assert.False(staged.Overwrite);
        Assert.Equal(HashB, staged.ExistingRowHash);
        Assert.Contains("overwrite choice", result.Describe());
    }

    [SkippableFact]
    public async Task Exact_duplicate_whose_existing_row_changed_becomes_Conflict_and_a_deleted_existing_row_makes_a_Conflict_New()
    {
        await using var env = NewEnv();
        var keyDup = env.Key("dup");
        var dupRow = Row(2, keyDup, "T1", 5m);
        var seed = await env.NewBatchAsync();
        await env.SeedTransactionAsync(seed, keyDup, "T1", dupRow.RowHash);              // identical -> ExactDuplicate
        var keyGone = env.Key("gone");
        await env.SeedTransactionAsync(seed, keyGone, "T1", HashA);                      // different -> Conflict
        var id = await env.StageAsync(new[] { dupRow, Row(3, keyGone, "T1", 7m) });
        Assert.Equal((byte)RowStatus.ExactDuplicate, (await StagingRowAsync(env, id, 2)).Status);
        Assert.Equal((byte)RowStatus.Conflict, (await StagingRowAsync(env, id, 3)).Status);

        await env.SetTransactionHashAsync(HashB, keyDup);
        await env.DeleteTransactionAsync(keyGone);
        var result = await env.Store.RefreshAgainstDatabaseAsync(id);

        Assert.Equal(2, result.RowsChanged);
        Assert.Contains(result.Moves, m => m.From == RowStatus.ExactDuplicate && m.To == RowStatus.Conflict && m.Count == 1);
        Assert.Contains(result.Moves, m => m.From == RowStatus.Conflict && m.To == RowStatus.New && m.Count == 1);
        var nowNew = await StagingRowAsync(env, id, 3);
        Assert.Equal((byte)RowStatus.New, nowNew.Status);
        Assert.Null(nowNew.ExistingRowHash);
    }

    [SkippableFact]
    public async Task Invalid_and_FileDuplicate_rows_are_never_touched()
    {
        await using var env = NewEnv();
        var kInvalid = env.Key("bad");
        var kDup = env.Key("dup");
        var seed = await env.NewBatchAsync();
        // Both keys exist in Transactions, with a different hash: a refresh must still leave these rows alone.
        await env.SeedTransactionAsync(seed, kInvalid, "T1", HashA);
        await env.SeedTransactionAsync(seed, kDup, "T1", HashA);
        var id = await env.StageAsync(new[]
        {
            Row(2, kInvalid, "T1", 1m, invalid: true),
            Row(3, kDup, "T1", 5m), Row(4, kDup, "T1", 5m),      // identical repeat -> row 3 is Conflict/New, row 4 FileDuplicate
        });
        var before = (await StagingRowAsync(env, id, 2), await StagingRowAsync(env, id, 4));
        Assert.Equal(((byte)RowStatus.Invalid, (byte)RowStatus.FileDuplicate), (before.Item1.Status, before.Item2.Status));

        await env.SetTransactionHashAsync(HashB, kInvalid, kDup);
        var result = await env.Store.RefreshAgainstDatabaseAsync(id);

        Assert.True(result.Success);
        var invalid = await StagingRowAsync(env, id, 2);
        var repeat = await StagingRowAsync(env, id, 4);
        Assert.Equal((byte)RowStatus.Invalid, invalid.Status);
        Assert.Null(invalid.ExistingRowHash);
        Assert.Equal((byte)RowStatus.FileDuplicate, repeat.Status);
        Assert.Null(repeat.ExistingRowHash);
        Assert.Equal(before.Item1.ErrorText, invalid.ErrorText);
    }

    [SkippableFact]
    public async Task Refresh_is_refused_for_a_batch_that_is_not_Staged_and_changes_nothing()
    {
        await using var env = NewEnv();
        var key = env.Key("k");
        var id = await env.StageAsync(new[] { Row(2, key, "T1", 5m) });
        await env.SeedTransactionAsync(await env.NewBatchAsync(), key, "T1", HashA);       // would move New -> Conflict if it ran

        await env.Store.DiscardAsync(id);
        var discarded = await env.Store.RefreshAgainstDatabaseAsync(id);
        Assert.False(discarded.Success);
        Assert.Contains("'Discarded'", discarded.RefusalReason);

        var committedBatch = await env.StageAsync(new[] { Row(2, env.Key("c"), "T1", 5m) });
        await env.SetBatchStatusAsync(committedBatch, "Committed");
        var committed = await env.Store.RefreshAgainstDatabaseAsync(committedBatch);
        Assert.False(committed.Success);
        Assert.Contains("'Committed'", committed.RefusalReason);
        Assert.Equal((byte)RowStatus.New, (await StagingRowAsync(env, committedBatch, 2)).Status);

        var missing = await env.Store.RefreshAgainstDatabaseAsync(int.MaxValue);
        Assert.False(missing.Success);
        Assert.Contains("does not exist", missing.RefusalReason);
    }

    [SkippableFact]
    public async Task Refresh_touches_only_its_own_batch()
    {
        await using var env = NewEnv();
        var key = env.Key("shared");
        var a = await env.StageAsync(new[] { Row(2, key, "T1", 5m) });
        var b = await env.StageAsync(new[] { Row(2, key, "T1", 5m) });                     // same key, other batch
        await env.SeedTransactionAsync(await env.NewBatchAsync(), key, "T1", HashA);

        await env.Store.RefreshAgainstDatabaseAsync(a);

        Assert.Equal((byte)RowStatus.Conflict, (await StagingRowAsync(env, a, 2)).Status);
        Assert.Equal((byte)RowStatus.New, (await StagingRowAsync(env, b, 2)).Status);      // batch B still shows its old preview
    }

    [SkippableFact]
    public async Task Refresh_works_in_several_chunks_and_leaves_unchanged_rows_alone()
    {
        await using var env = NewEnv();
        var rows = Enumerable.Range(0, 4_500).Select(i => Row(i + 2, env.Key($"r{i}"), "T1", i)).ToList();
        var id = await env.StageAsync(rows);
        var late = rows[4_400];                                         // excel row 4402: beyond the first 4,000-row chunk
        await env.SeedTransactionAsync(await env.NewBatchAsync(), late.PayOrderNo!, "T1", late.RowHash);

        var result = await env.Store.RefreshAgainstDatabaseAsync(id);

        var move = Assert.Single(result.Moves);
        Assert.Equal((RowStatus.New, RowStatus.ExactDuplicate, 1), (move.From, move.To, move.Count));
        var counts = await env.Store.GetCountsAsync(id);
        Assert.Equal(4_499, counts[RowStatus.New]);
        Assert.Equal(1, counts[RowStatus.ExactDuplicate]);
    }

    // ---- conflict page flags and counts -------------------------------------------------------------------

    [SkippableFact]
    public async Task Conflict_page_returns_each_rows_overwrite_flag_and_choices_survive_paging()
    {
        await using var env = NewEnv();
        var seed = await env.NewBatchAsync();
        var rows = new List<ImportRow>();
        for (var i = 0; i < 3; i++)
        {
            await env.SeedTransactionAsync(seed, env.Key($"c{i}"), "T1", HashA);
            rows.Add(Row(i + 2, env.Key($"c{i}"), "T1", 5m + i));
        }
        var id = await env.StageAsync(rows);

        Assert.Equal(new ConflictCounts(3, 0), await env.Store.GetConflictCountsAsync(id));

        await env.Store.SetConflictOverwriteAsync(id, true, new[] { 3 });                   // only row 3
        Assert.Equal(new ConflictCounts(3, 1), await env.Store.GetConflictCountsAsync(id));

        // Page size 1: the flag must come back on the right row when that page is read again.
        var page1 = await env.Store.GetConflictPageAsync(id, 1, 1);
        var page2 = await env.Store.GetConflictPageAsync(id, 2, 1);
        var page3 = await env.Store.GetConflictPageAsync(id, 3, 1);
        Assert.Equal(new[] { false, true, false },
            new[] { page1, page2, page3 }.Select(p => Assert.Single(p.Rows).Incoming.Overwrite));

        await env.Store.SetConflictOverwriteAsync(id, true, null);                          // Overwrite all
        Assert.Equal(new ConflictCounts(3, 3), await env.Store.GetConflictCountsAsync(id));
        await env.Store.SetConflictOverwriteAsync(id, false, null);                         // Skip all
        Assert.Equal(new ConflictCounts(3, 0), await env.Store.GetConflictCountsAsync(id));
        Assert.All((await env.Store.GetConflictPageAsync(id, 1, 100)).Rows, r => Assert.False(r.Incoming.Overwrite));
    }
}
