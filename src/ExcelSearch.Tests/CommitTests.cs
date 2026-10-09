using System.Diagnostics;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using static ExcelSearch.Tests.StagingEnv;

namespace ExcelSearch.Tests;

/// <summary>
/// Commit and cleanup against the development database. Same rules as StagingStoreTests: unique TEST_ keys,
/// every batch tracked, only the test's own rows deleted in a finally (staging, then Transactions, then batches).
/// </summary>
public class CommitTests
{
    private readonly ITestOutputHelper _output;

    public CommitTests(ITestOutputHelper output) => _output = output;

    [DbFact]
    public async Task Commit_inserts_new_rows_with_correct_values_and_updates_the_batch()
    {
        await using var env = Create();

        var first = Row(2, env.Key("a"), "T1", 12.50m);
        first.Cnic = "3520212345671";
        first.Narration1 = "hello";
        first.RowHash = RowHasher.Compute(first);
        var dupA = Row(5, env.Key("dup"), "T1", 7m);
        var dupB = Row(6, env.Key("dup"), "T1", 7m);   // identical repeat inside the file -> FileDuplicate

        var id = await env.StageAsync(new[]
        {
            first,
            Row(3, env.Key("b"), "T1", 1m),
            Row(4, env.Key("bad"), "T1", invalid: true),
            dupA, dupB,
        });

        var result = await env.Store.CommitAsync(id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(CommitFailure.None, result.FailureReason);
        Assert.Equal(3, result.NewRows);               // a, b, dup (first occurrence)
        Assert.Equal(0, result.UpdatedRows);
        Assert.Equal(1, result.SkippedFileDuplicates);
        Assert.Equal(1, result.RejectedRows);

        var t = await env.GetTransactionAsync(env.Key("a"), "T1");
        Assert.NotNull(t);
        Assert.NotEqual(Guid.Empty, t!.Id);
        Assert.Equal(id, t.ImportBatchId);
        Assert.Equal(2, t.SourceRowNumber);
        Assert.Equal(first.RowHash, t.RowHash);
        Assert.Equal(new DateOnly(2024, 4, 3), t.ValueDate);
        Assert.Equal(12.50m, t.Debit);
        Assert.Equal("Customer", t.CustomerName);
        Assert.Equal("3520212345671", t.Cnic);
        Assert.Equal("hello", t.Narration1);
        Assert.Null(t.UpdatedAt);
        Assert.Equal(3, await env.CountTransactionsWithPrefixAsync());   // the invalid row and the repeat were not written

        var batch = await env.GetBatchAsync(id);
        Assert.Equal("Committed", batch.Status);
        Assert.Equal(5, batch.TotalRows);
        Assert.Equal(3, batch.NewRows);
        Assert.Equal(1, batch.SkippedRows);
        Assert.Equal(0, batch.UpdatedRows);
        Assert.Equal(1, batch.RejectedRows);
        Assert.NotNull(batch.CommittedAt);

        Assert.Equal(0, await env.StagingCountAsync(id));                // staging rows are gone after commit
    }

    [DbFact]
    public async Task Exact_duplicates_are_skipped_and_the_existing_row_is_untouched()
    {
        await using var env = Create();
        var seed = await env.NewBatchAsync();
        var row = Row(2, env.Key("same"), "T1", 10m);
        await env.SeedTransactionAsync(seed, row.PayOrderNo!, row.TxnRefSeqNo!, row.RowHash);

        var id = await env.StageAsync(new[] { row, Row(3, env.Key("fresh"), "T1") });
        var result = await env.Store.CommitAsync(id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.NewRows);
        Assert.Equal(1, result.SkippedExactDuplicates);
        var existing = await env.GetTransactionAsync(row.PayOrderNo!, "T1");
        Assert.Equal(seed, existing!.ImportBatchId);                      // still owned by the earlier batch
        Assert.Null(existing.UpdatedAt);
        Assert.Equal(1, (await env.GetBatchAsync(id)).SkippedRows);
    }

    [DbFact]
    public async Task Conflict_is_skipped_by_default()
    {
        await using var env = Create();
        var seed = await env.NewBatchAsync();
        var existing = Row(2, env.Key("c"), "T1", 99m);
        await env.SeedTransactionAsync(seed, existing.PayOrderNo!, "T1", existing.RowHash);

        var id = await env.StageAsync(new[] { Row(2, env.Key("c"), "T1", 20m) });
        var result = await env.Store.CommitAsync(id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.UpdatedRows);
        Assert.Equal(1, result.SkippedConflicts);
        var t = await env.GetTransactionAsync(existing.PayOrderNo!, "T1");
        Assert.Equal(existing.RowHash, t!.RowHash);                       // unchanged
        Assert.Equal(seed, t.ImportBatchId);
        Assert.Equal(1, (await env.GetBatchAsync(id)).SkippedRows);
    }

    [DbFact]
    public async Task Conflict_is_updated_only_when_Overwrite_is_set()
    {
        await using var env = Create();
        var seed = await env.NewBatchAsync();
        var e1 = Row(2, env.Key("c1"), "T1", 99m);
        var e2 = Row(3, env.Key("c2"), "T1", 99m);
        await env.SeedTransactionAsync(seed, e1.PayOrderNo!, "T1", e1.RowHash);
        await env.SeedTransactionAsync(seed, e2.PayOrderNo!, "T1", e2.RowHash);

        var in1 = Row(2, env.Key("c1"), "T1", 20m);
        var in2 = Row(3, env.Key("c2"), "T1", 30m);
        var id = await env.StageAsync(new[] { in1, in2 });

        // Overwrite only row 2: row 3 must stay a skipped conflict.
        var set = await env.Store.SetConflictOverwriteAsync(id, true, new[] { 2 });
        Assert.Equal(1, set);

        var result = await env.Store.CommitAsync(id);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.UpdatedRows);
        Assert.Equal(1, result.SkippedConflicts);

        var updated = await env.GetTransactionAsync(in1.PayOrderNo!, "T1");
        Assert.Equal(in1.RowHash, updated!.RowHash);
        Assert.Equal(20m, updated.Debit);
        Assert.Equal(new DateOnly(2024, 4, 3), updated.ValueDate);
        Assert.Equal(id, updated.ImportBatchId);
        Assert.Equal(2, updated.SourceRowNumber);
        Assert.NotNull(updated.UpdatedAt);

        var skipped = await env.GetTransactionAsync(in2.PayOrderNo!, "T1");
        Assert.Equal(e2.RowHash, skipped!.RowHash);
        Assert.Null(skipped.UpdatedAt);

        var batch = await env.GetBatchAsync(id);
        Assert.Equal(1, batch.UpdatedRows);
        Assert.Equal(1, batch.SkippedRows);
    }

    [DbFact]
    public async Task SetConflictOverwrite_with_null_rows_touches_all_conflicts_of_that_batch_only()
    {
        await using var env = Create();
        var seed = await env.NewBatchAsync();
        var existing = Row(2, env.Key("x"), "T1", 99m);
        await env.SeedTransactionAsync(seed, existing.PayOrderNo!, "T1", existing.RowHash);

        var mine = await env.StageAsync(new[] { Row(2, env.Key("x"), "T1", 1m), Row(3, env.Key("n"), "T1") });
        var other = await env.StageAsync(new[] { Row(2, env.Key("x"), "T1", 2m) });

        Assert.Equal(1, await env.Store.SetConflictOverwriteAsync(mine, true, null));   // the New row is not a conflict

        var mineRow = Assert.Single((await env.Store.GetPageAsync(mine, RowStatus.Conflict, 1, 10)).Rows);
        var otherRow = Assert.Single((await env.Store.GetPageAsync(other, RowStatus.Conflict, 1, 10)).Rows);
        Assert.True(mineRow.Overwrite);
        Assert.False(otherRow.Overwrite);
    }

    [DbFact]
    public async Task Committing_the_same_batch_twice_fails_with_BatchNotStaged()
    {
        await using var env = Create();
        var id = await env.StageAsync(new[] { Row(2, env.Key("a"), "T1") });

        Assert.True((await env.Store.CommitAsync(id)).Success);
        var second = await env.Store.CommitAsync(id);

        Assert.False(second.Success);
        Assert.Equal(CommitFailure.BatchNotStaged, second.FailureReason);
        Assert.Equal(1, await env.CountTransactionsWithPrefixAsync());
    }

    [DbFact]
    public async Task Commit_of_an_unknown_batch_fails_with_BatchNotStaged()
    {
        await using var env = Create();
        var result = await env.Store.CommitAsync(int.MaxValue);
        Assert.Equal(CommitFailure.BatchNotStaged, result.FailureReason);
    }

    [DbFact]
    public async Task TotalRows_not_matching_the_staging_count_is_IncompleteLoad()
    {
        await using var env = Create();
        var id = await env.StageAsync(new[] { Row(2, env.Key("a"), "T1"), Row(3, env.Key("b"), "T1") });
        await env.ExecuteAsync($"UPDATE dbo.ImportBatches SET TotalRows = 3 WHERE Id = {id}");

        var result = await env.Store.CommitAsync(id);

        Assert.False(result.Success);
        Assert.Equal(CommitFailure.IncompleteLoad, result.FailureReason);
        Assert.Equal(0, await env.CountTransactionsWithPrefixAsync());
        Assert.Equal("Staged", await env.BatchStatusAsync(id));
    }

    [DbFact]
    public async Task A_batch_with_a_Pending_row_is_IncompleteLoad()
    {
        await using var env = Create();
        var id = await env.NewBatchAsync();
        await env.Store.LoadRowsAsync(id, new[] { Row(2, env.Key("a"), "T1") }, null);   // loaded, never classified

        var result = await env.Store.CommitAsync(id);

        Assert.Equal(CommitFailure.IncompleteLoad, result.FailureReason);
        Assert.Equal(0, await env.CountTransactionsWithPrefixAsync());
    }

    [DbFact]
    public async Task New_key_committed_by_someone_else_with_different_values_aborts_with_RowsChangedSincePreview()
    {
        await using var env = Create();
        var a = await env.StageAsync(new[] { Row(2, env.Key("k"), "T1", 1m), Row(3, env.Key("only-a"), "T1") });
        var b = await env.StageAsync(new[] { Row(2, env.Key("k"), "T1", 2m) });

        Assert.True((await env.Store.CommitAsync(b)).Success);
        var result = await env.Store.CommitAsync(a);

        Assert.False(result.Success);
        Assert.Equal(CommitFailure.RowsChangedSincePreview, result.FailureReason);
        Assert.Contains("1", result.Message);
        Assert.Null(await env.GetTransactionAsync(env.Key("only-a"), "T1"));          // nothing from A was written
        Assert.Equal(2m, (await env.GetTransactionAsync(env.Key("k"), "T1"))!.Debit); // B's row is intact
        Assert.Equal("Staged", await env.BatchStatusAsync(a));                        // A can still be discarded
        Assert.Equal(2, await env.StagingCountAsync(a));
    }

    [DbFact]
    public async Task New_key_committed_by_someone_else_with_identical_values_counts_as_exact_duplicate()
    {
        await using var env = Create();
        var a = await env.StageAsync(new[] { Row(2, env.Key("k"), "T1", 1m), Row(3, env.Key("only-a"), "T1") });
        var b = await env.StageAsync(new[] { Row(2, env.Key("k"), "T1", 1m) });

        Assert.True((await env.Store.CommitAsync(b)).Success);
        var result = await env.Store.CommitAsync(a);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.NewRows);
        Assert.Equal(1, result.SkippedExactDuplicates);
        Assert.Equal(b, (await env.GetTransactionAsync(env.Key("k"), "T1"))!.ImportBatchId);
        Assert.Equal(a, (await env.GetTransactionAsync(env.Key("only-a"), "T1"))!.ImportBatchId);
    }

    [DbFact]
    public async Task Overwrite_whose_existing_row_changed_since_the_preview_aborts()
    {
        await using var env = Create();
        var seed = await env.NewBatchAsync();
        var existing = Row(2, env.Key("c"), "T1", 99m);
        await env.SeedTransactionAsync(seed, existing.PayOrderNo!, "T1", existing.RowHash);

        var id = await env.StageAsync(new[] { Row(2, env.Key("c"), "T1", 20m), Row(3, env.Key("n"), "T1") });
        await env.Store.SetConflictOverwriteAsync(id, true, null);

        // Someone else changes the existing row after our preview.
        var other = new string('f', 64);
        var po = existing.PayOrderNo!;
        await env.ExecuteAsync($"UPDATE dbo.Transactions SET RowHash = {other} WHERE PayOrderNo = {po} AND TxnRefSeqNo = 'T1'");

        var result = await env.Store.CommitAsync(id);

        Assert.False(result.Success);
        Assert.Equal(CommitFailure.RowsChangedSincePreview, result.FailureReason);
        Assert.Equal(other, (await env.GetTransactionAsync(po, "T1"))!.RowHash);        // not overwritten
        Assert.Null(await env.GetTransactionAsync(env.Key("n"), "T1"));                  // the New row was not written either
        Assert.Equal("Staged", await env.BatchStatusAsync(id));
    }

    [DbFact]
    public async Task Parallel_commits_with_overlapping_identical_keys_both_succeed_without_deadlock()
    {
        await using var env = Create();
        var a = await env.StageAsync(new[] { Row(2, env.Key("k1"), "T1", 1m), Row(3, env.Key("k2"), "T1", 2m) });
        var b = await env.StageAsync(new[] { Row(2, env.Key("k2"), "T1", 2m), Row(3, env.Key("k3"), "T1", 3m) });

        var results = await Task.WhenAll(env.Store.CommitAsync(a), env.Store.CommitAsync(b));

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(3, results.Sum(r => r.NewRows));                  // k1, k2, k3 each written once
        Assert.Equal(1, results.Sum(r => r.SkippedExactDuplicates));   // k2 was written by whoever went first
        Assert.Equal(3, await env.CountTransactionsWithPrefixAsync());
    }

    [DbFact]
    public async Task Parallel_commits_with_overlapping_different_keys_let_exactly_one_win()
    {
        await using var env = Create();
        var a = await env.StageAsync(new[] { Row(2, env.Key("k1"), "T1", 1m), Row(3, env.Key("k2"), "T1", 2m) });
        var b = await env.StageAsync(new[] { Row(2, env.Key("k2"), "T1", 99m), Row(3, env.Key("k3"), "T1", 3m) });

        var results = await Task.WhenAll(env.Store.CommitAsync(a), env.Store.CommitAsync(b));

        Assert.Equal(1, results.Count(r => r.Success));
        var loser = Assert.Single(results, r => !r.Success);
        Assert.Equal(CommitFailure.RowsChangedSincePreview, loser.FailureReason);
        Assert.Equal(2, await env.CountTransactionsWithPrefixAsync());  // only the winner's two rows
    }

    [DbFact]
    public async Task Cleanup_discards_old_Staged_batches_leaves_fresh_ones_and_clears_leftovers_of_Committed_ones()
    {
        await using var env = Create();

        var old = await env.StageAsync(new[] { Row(2, env.Key("old"), "T1") });
        await env.ExecuteAsync($"UPDATE dbo.ImportBatches SET ImportedAt = DATEADD(HOUR, -25, SYSUTCDATETIME()) WHERE Id = {old}");

        var fresh = await env.StageAsync(new[] { Row(2, env.Key("fresh"), "T1") });

        var committed = await env.StageAsync(new[] { Row(2, env.Key("done"), "T1") });
        await env.ExecuteAsync($"UPDATE dbo.ImportBatches SET Status = 'Committed' WHERE Id = {committed}");   // leftovers remain

        var result = await env.Store.CleanupStaleBatchesAsync();

        Assert.True(result.StaleBatchesDiscarded >= 1);
        Assert.True(result.LeftoverBatchesCleaned >= 1);

        Assert.Equal("Discarded", await env.BatchStatusAsync(old));
        Assert.Equal(0, await env.StagingCountAsync(old));

        Assert.Equal("Staged", await env.BatchStatusAsync(fresh));        // recent Staged batch untouched
        Assert.Equal(1, await env.StagingCountAsync(fresh));

        Assert.Equal("Committed", await env.BatchStatusAsync(committed)); // status kept, rows removed
        Assert.Equal(0, await env.StagingCountAsync(committed));
    }

    [LargeDbFact]
    public async Task Commits_a_200k_row_file_then_sees_the_same_file_as_all_exact_duplicates()
    {
        await using var env = Create();
        var path = Path.Combine(Path.GetTempPath(), $"excelsearch-commit-{Guid.NewGuid():N}.xlsx");
        try
        {
            LargeFileTests.WriteSyntheticWorkbook(path, 200_000, env.Prefix + "-"); // TEST_ keys only

            GC.Collect();
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var baseline = proc.WorkingSet64;
            long peak = baseline;
            var progress = new Progress<int>(_ => { proc.Refresh(); peak = Math.Max(peak, proc.WorkingSet64); });

            // First import: stage (load + classify), then commit.
            var (first, load1, classify1) = await StageTimedAsync(env, path, progress);
            var sw = Stopwatch.StartNew();
            var commit = await env.Store.CommitAsync(first);
            var commitTime = sw.Elapsed;
            Assert.True(commit.Success, commit.Message);
            Assert.Equal(200_000, commit.NewRows);
            Assert.Equal(200_000, await env.CountTransactionsWithPrefixAsync());
            Assert.Equal(0, await env.StagingCountAsync(first));

            // Same file again: everything must now be an exact duplicate.
            var (second, load2, classify2) = await StageTimedAsync(env, path, progress);
            var counts = await env.Store.GetCountsAsync(second);
            Assert.Equal(200_000, counts[RowStatus.ExactDuplicate]);
            Assert.Equal(0, counts.Where(c => c.Key != RowStatus.ExactDuplicate).Sum(c => c.Value));

            proc.Refresh();
            peak = Math.Max(peak, proc.WorkingSet64);

            _output.WriteLine($"Stage #1: load {load1.TotalSeconds:F1}s, classify {classify1.TotalSeconds:F1}s");
            _output.WriteLine($"Commit #1: {commitTime.TotalSeconds:F1}s (reported {commit.ElapsedSeconds:F1}s, {commit.NewRows:N0} new)");
            _output.WriteLine($"Stage #2 (same file): load {load2.TotalSeconds:F1}s, classify {classify2.TotalSeconds:F1}s -> all {counts[RowStatus.ExactDuplicate]:N0} ExactDuplicate");
            _output.WriteLine($"Working set: baseline {baseline / 1048576.0:F0} MB, peak {peak / 1048576.0:F0} MB, growth {(peak - baseline) / 1048576.0:F0} MB");
            // env.DisposeAsync then deletes the staging rows, the 200,000 TEST_ Transactions rows (chunks of 50,000) and both batches.
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<(int BatchId, TimeSpan Load, TimeSpan Classify)> StageTimedAsync(
        StagingEnv env, string path, IProgress<int> progress)
    {
        using var file = new ExcelParser().Parse(path, null, CancellationToken.None);
        var id = await env.Store.CreateBatchAsync(file.FileName, file.SheetName, file.FileHash, "TEST\\user");
        env.TrackBatch(id);
        var sw = Stopwatch.StartNew();
        await env.Store.LoadRowsAsync(id, file.Rows, progress);
        var load = sw.Elapsed;
        sw.Restart();
        await env.Store.ClassifyAsync(id);
        return (id, load, sw.Elapsed);
    }
}
