using System.Diagnostics;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using static ExcelSearch.Tests.StagingEnv;

namespace ExcelSearch.Tests;

/// <summary>
/// Commit, discard races and cleanup, written once and run against every provider. Same rules as StagingStoreContract: unique
/// TEST_ keys, every batch tracked, only the test's own rows deleted in a finally (staging, then Transactions, then batches).
/// </summary>
public abstract class CommitContract
{
    private readonly ITestOutputHelper _output;

    protected CommitContract(ITestOutputHelper output) => _output = output;

    protected abstract StagingEnv NewEnv(bool large = false);

    [SkippableFact]
    public async Task Commit_inserts_new_rows_with_correct_values_and_updates_the_batch()
    {
        await using var env = NewEnv();

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

    [SkippableFact]
    public async Task Exact_duplicates_are_skipped_and_the_existing_row_is_untouched()
    {
        await using var env = NewEnv();
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

    [SkippableFact]
    public async Task Conflict_is_skipped_by_default()
    {
        await using var env = NewEnv();
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

    [SkippableFact]
    public async Task Conflict_is_updated_only_when_Overwrite_is_set()
    {
        await using var env = NewEnv();
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

    [SkippableFact]
    public async Task SetConflictOverwrite_with_null_rows_touches_all_conflicts_of_that_batch_only()
    {
        await using var env = NewEnv();
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

    [SkippableFact]
    public async Task Committing_the_same_batch_twice_fails_with_BatchNotStaged()
    {
        await using var env = NewEnv();
        var id = await env.StageAsync(new[] { Row(2, env.Key("a"), "T1") });

        Assert.True((await env.Store.CommitAsync(id)).Success);
        var second = await env.Store.CommitAsync(id);

        Assert.False(second.Success);
        Assert.Equal(CommitFailure.BatchNotStaged, second.FailureReason);
        Assert.Equal(1, await env.CountTransactionsWithPrefixAsync());
    }

    [SkippableFact]
    public async Task Commit_of_an_unknown_batch_fails_with_BatchNotStaged()
    {
        await using var env = NewEnv();
        var result = await env.Store.CommitAsync(int.MaxValue);
        Assert.Equal(CommitFailure.BatchNotStaged, result.FailureReason);
    }

    [SkippableFact]
    public async Task TotalRows_not_matching_the_staging_count_is_IncompleteLoad()
    {
        await using var env = NewEnv();
        var id = await env.StageAsync(new[] { Row(2, env.Key("a"), "T1"), Row(3, env.Key("b"), "T1") });
        await env.SetTotalRowsAsync(id, 3);

        var result = await env.Store.CommitAsync(id);

        Assert.False(result.Success);
        Assert.Equal(CommitFailure.IncompleteLoad, result.FailureReason);
        Assert.Equal(0, await env.CountTransactionsWithPrefixAsync());
        Assert.Equal("Staged", await env.BatchStatusAsync(id));
    }

    [SkippableFact]
    public async Task A_batch_with_a_Pending_row_is_IncompleteLoad()
    {
        await using var env = NewEnv();
        var id = await env.NewBatchAsync();
        await env.Store.LoadRowsAsync(id, new[] { Row(2, env.Key("a"), "T1") }, null);   // loaded, never classified

        var result = await env.Store.CommitAsync(id);

        Assert.Equal(CommitFailure.IncompleteLoad, result.FailureReason);
        Assert.Equal(0, await env.CountTransactionsWithPrefixAsync());
    }

    [SkippableFact]
    public async Task New_key_committed_by_someone_else_with_different_values_aborts_with_RowsChangedSincePreview()
    {
        await using var env = NewEnv();
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

    [SkippableFact]
    public async Task New_key_committed_by_someone_else_with_identical_values_counts_as_exact_duplicate()
    {
        await using var env = NewEnv();
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

    [SkippableFact]
    public async Task Overwrite_whose_existing_row_changed_since_the_preview_aborts()
    {
        await using var env = NewEnv();
        var seed = await env.NewBatchAsync();
        var existing = Row(2, env.Key("c"), "T1", 99m);
        await env.SeedTransactionAsync(seed, existing.PayOrderNo!, "T1", existing.RowHash);

        var id = await env.StageAsync(new[] { Row(2, env.Key("c"), "T1", 20m), Row(3, env.Key("n"), "T1") });
        await env.Store.SetConflictOverwriteAsync(id, true, null);

        // Someone else changes the existing row after our preview.
        var other = new string('f', 64);
        var po = existing.PayOrderNo!;
        await env.SetTransactionHashAsync(other, po);

        var result = await env.Store.CommitAsync(id);

        Assert.False(result.Success);
        Assert.Equal(CommitFailure.RowsChangedSincePreview, result.FailureReason);
        Assert.Equal(other, (await env.GetTransactionAsync(po, "T1"))!.RowHash);        // not overwritten
        Assert.Null(await env.GetTransactionAsync(env.Key("n"), "T1"));                  // the New row was not written either
        Assert.Equal("Staged", await env.BatchStatusAsync(id));
    }

    [SkippableFact]
    public async Task Parallel_commits_with_overlapping_identical_keys_both_succeed_without_deadlock()
    {
        await using var env = NewEnv();
        var a = await env.StageAsync(new[] { Row(2, env.Key("k1"), "T1", 1m), Row(3, env.Key("k2"), "T1", 2m) });
        var b = await env.StageAsync(new[] { Row(2, env.Key("k2"), "T1", 2m), Row(3, env.Key("k3"), "T1", 3m) });

        var results = await Task.WhenAll(env.Store.CommitAsync(a), env.Store.CommitAsync(b));

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(3, results.Sum(r => r.NewRows));                  // k1, k2, k3 each written once
        Assert.Equal(1, results.Sum(r => r.SkippedExactDuplicates));   // k2 was written by whoever went first
        Assert.Equal(3, await env.CountTransactionsWithPrefixAsync());
    }

    [SkippableFact]
    public async Task Parallel_commits_with_overlapping_different_keys_let_exactly_one_win()
    {
        await using var env = NewEnv();
        var a = await env.StageAsync(new[] { Row(2, env.Key("k1"), "T1", 1m), Row(3, env.Key("k2"), "T1", 2m) });
        var b = await env.StageAsync(new[] { Row(2, env.Key("k2"), "T1", 99m), Row(3, env.Key("k3"), "T1", 3m) });

        var results = await Task.WhenAll(env.Store.CommitAsync(a), env.Store.CommitAsync(b));

        Assert.Equal(1, results.Count(r => r.Success));
        var loser = Assert.Single(results, r => !r.Success);
        Assert.Equal(CommitFailure.RowsChangedSincePreview, loser.FailureReason);
        Assert.Equal(2, await env.CountTransactionsWithPrefixAsync());  // only the winner's two rows
    }

    [SkippableFact]
    public async Task Cleanup_discards_old_Staged_batches_leaves_fresh_ones_and_clears_leftovers_of_Committed_ones()
    {
        await using var env = NewEnv();

        var old = await env.StageAsync(new[] { Row(2, env.Key("old"), "T1") });
        await env.BackdateBatchAsync(old, 25);

        var fresh = await env.StageAsync(new[] { Row(2, env.Key("fresh"), "T1") });

        var committed = await env.StageAsync(new[] { Row(2, env.Key("done"), "T1") });
        await env.SetBatchStatusAsync(committed, "Committed");   // leftovers remain

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

    [SkippableFact]
    public async Task Discard_refuses_a_Committed_batch_and_deletes_nothing()
    {
        await using var env = NewEnv();

        // (1) Status flipped by hand so the staging rows are still there to prove nothing gets deleted.
        var flagged = await env.StageAsync(new[] { Row(2, env.Key("a"), "T1"), Row(3, env.Key("b"), "T1") });
        await env.SetBatchStatusAsync(flagged, "Committed");

        var refused = await env.Store.DiscardAsync(flagged);

        Assert.False(refused.Discarded);
        Assert.Contains("already committed", refused.Reason);
        Assert.Equal("Committed", await env.BatchStatusAsync(flagged));
        Assert.Equal(2, await env.StagingCountAsync(flagged));

        // (2) A really committed batch: its Transactions rows survive the refused discard.
        var real = await env.StageAsync(new[] { Row(2, env.Key("c"), "T1") });
        Assert.True((await env.Store.CommitAsync(real)).Success);

        var refusedAgain = await env.Store.DiscardAsync(real);

        Assert.False(refusedAgain.Discarded);
        Assert.Equal("Committed", await env.BatchStatusAsync(real));
        Assert.Equal(1, await env.CountTransactionsAsync(real));
    }

    [SkippableFact]
    public async Task Discard_works_on_a_Staged_batch_and_refuses_a_second_time()
    {
        await using var env = NewEnv();
        var id = await env.StageAsync(new[] { Row(2, env.Key("a"), "T1"), Row(3, env.Key("b"), "T1") });

        var first = await env.Store.DiscardAsync(id);

        Assert.True(first.Discarded);
        Assert.Null(first.Reason);
        Assert.Equal("Discarded", await env.BatchStatusAsync(id));
        Assert.Equal(0, await env.StagingCountAsync(id));

        var second = await env.Store.DiscardAsync(id);
        Assert.False(second.Discarded);
        Assert.Contains("already discarded", second.Reason);
    }

    [SkippableFact]
    public async Task Commit_on_a_Discarded_batch_returns_BatchNotStaged_and_writes_nothing()
    {
        await using var env = NewEnv();
        var id = await env.StageAsync(new[] { Row(2, env.Key("a"), "T1") });
        Assert.True((await env.Store.DiscardAsync(id)).Discarded);

        var result = await env.Store.CommitAsync(id);

        Assert.False(result.Success);
        Assert.Equal(CommitFailure.BatchNotStaged, result.FailureReason);
        Assert.Equal(0, await env.CountTransactionsWithPrefixAsync());
    }

    [SkippableFact]
    public async Task Commit_and_discard_started_together_always_end_consistent()
    {
        const int rows = 10_000;
        await using var env = NewEnv();

        for (var round = 1; round <= 3; round++)
        {
            var r = round;
            var id = await env.StageAsync(Enumerable.Range(0, rows)
                .Select(i => Row(i + 2, env.Key($"r{r}-{i}"), "T1", i)));

            // Release both calls at the same moment.
            var go = new TaskCompletionSource();
            var commitTask = Task.Run(async () => { await go.Task; return await env.Store.CommitAsync(id); });
            var discardTask = Task.Run(async () => { await go.Task; return await env.Store.DiscardAsync(id); });
            go.SetResult();
            await Task.WhenAll(commitTask, discardTask);
            var commit = commitTask.Result;
            var discard = discardTask.Result;

            var inTransactions = await env.CountTransactionsAsync(id);
            var status = await env.BatchStatusAsync(id);
            _output.WriteLine($"Round {round}: {(commit.Success ? "commit won" : "discard won")}, " +
                              $"{inTransactions:N0} rows in Transactions, batch {status}");

            if (commit.Success)
            {
                Assert.False(discard.Discarded, "discard must be refused once the commit won");
                Assert.Equal(rows, inTransactions);
                Assert.Equal("Committed", status);
            }
            else
            {
                Assert.Equal(CommitFailure.BatchNotStaged, commit.FailureReason);
                Assert.True(discard.Discarded, "discard must have won if the commit did not");
                Assert.Equal(0, inTransactions);
                Assert.Equal("Discarded", status);
            }
            Assert.Equal(0, await env.StagingCountAsync(id));   // the winner leaves no staging rows either way
        }
    }

    [SkippableFact]
    public async Task Discard_started_while_a_commit_is_running_waits_for_it_and_is_refused()
    {
        // The simultaneous test above is always won by the discard (a commit needs several round trips before it
        // holds the batch row). Here the commit gets a head start, so this is the "commit wins" half of the race.
        const int rows = 20_000;
        await using var env = NewEnv();
        var id = await env.StageAsync(Enumerable.Range(0, rows).Select(i => Row(i + 2, env.Key($"w{i}"), "T1", i)));

        var commitTask = Task.Run(() => env.Store.CommitAsync(id));
        await Task.Delay(100);
        var sw = Stopwatch.StartNew();
        var discard = await env.Store.DiscardAsync(id);
        var waited = sw.Elapsed;
        var commit = await commitTask;
        _output.WriteLine($"Discard returned after {waited.TotalSeconds:F1}s; commit {(commit.Success ? "succeeded" : "failed: " + commit.Message)}");

        Assert.True(commit.Success, commit.Message);
        Assert.False(discard.Discarded);
        Assert.Contains("already committed", discard.Reason);
        Assert.Equal(rows, await env.CountTransactionsAsync(id));
        Assert.Equal("Committed", await env.BatchStatusAsync(id));
    }

    [SkippableFact, Trait("Category", "Large")]
    public async Task Commits_a_200k_row_file_then_sees_the_same_file_as_all_exact_duplicates()
    {
        await using var env = NewEnv(large: true);
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
            var commitRun = Task.Run(() => env.Store.CommitAsync(first));

            // While the commit runs, count Transactions from a different connection (a fresh context each time).
            // With READ_COMMITTED_SNAPSHOT on (SQL Server) or WAL (SQLite), readers see the last committed rows and must not wait for the writer.
            var (isolationOn, isolationText) = await env.ReaderIsolationAsync();
            var samples = new List<(double Seconds, int Count, bool CommitStillRunning)>();
            await Task.Delay(3000);   // let the commit get into its insert
            while (!commitRun.IsCompleted && samples.Count < 5)
            {
                var t0 = Stopwatch.StartNew();
                var count = await env.CountAllTransactionsAsync();
                samples.Add((t0.Elapsed.TotalSeconds, count, !commitRun.IsCompleted));
                await Task.Delay(2000);
            }

            var commit = await commitRun;
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

            _output.WriteLine($"[{env.ProviderName}] Stage #1: load {load1.TotalSeconds:F1}s ({200_000 / load1.TotalSeconds:N0} rows/s), classify {classify1.TotalSeconds:F1}s");
            _output.WriteLine(isolationText);
            foreach (var (secs, count, running) in samples)
                _output.WriteLine($"SELECT COUNT(*) FROM Transactions during commit: {secs:F3}s, value {count:N0}, commit still running afterwards: {running}");
            Assert.True(isolationOn, "Readers must not wait for a commit: READ_COMMITTED_SNAPSHOT (Scripts/db/004_enable_rcsi.sql) or WAL must be ON");
            Assert.NotEmpty(samples);
            Assert.All(samples, x => Assert.True(x.Seconds < 5, $"a reader waited {x.Seconds:F1}s for the commit"));
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

    [SkippableFact]
    public async Task Conflict_page_shows_the_existing_row_and_exactly_which_columns_differ()
    {
        await using var env = NewEnv();
        var key = env.Key("k");

        var old = Row(2, key, "T1", 99m);
        old.CustomerName = "Old Name";
        old.RowHash = RowHasher.Compute(old);
        Assert.True((await env.Store.CommitAsync(await env.StageAsync(new[] { old }))).Success);

        var changed = Row(2, key, "T1", 150m);
        changed.CustomerName = "New Name";
        changed.RowHash = RowHasher.Compute(changed);
        var id = await env.StageAsync(new[] { changed, Row(3, env.Key("fresh"), "T1", 1m) });

        var page = await env.Store.GetConflictPageAsync(id, 1, 100);

        Assert.Equal(1, page.Total);
        var conflict = Assert.Single(page.Rows);
        Assert.Equal(2, conflict.Incoming.ExcelRowNumber);
        Assert.False(conflict.ExistingRowMissing);
        Assert.Equal(new[] { "Customer name", "Debit" }, conflict.Changes.Select(c => c.Label).OrderBy(x => x));
        var debit = conflict.Changes.Single(c => c.Label == "Debit");
        Assert.Equal("99.00", debit.Existing);
        Assert.Equal("150.00", debit.Incoming);
        Assert.Equal("Old Name", conflict.Changes.Single(c => c.Label == "Customer name").Existing);
        Assert.Contains("Debit: 99.00 → 150.00", conflict.Summary);
    }

    [SkippableFact]
    public async Task Conflict_page_flags_a_row_that_disappeared_from_Transactions()
    {
        await using var env = NewEnv();
        var key = env.Key("gone");
        var seedBatch = await env.NewBatchAsync();
        var existing = Row(1, key, "T1", 99m);
        await env.SeedTransactionAsync(seedBatch, key, "T1", existing.RowHash);
        var id = await env.StageAsync(new[] { Row(2, key, "T1", 5m) });

        await env.DeleteTransactionAsync(key);
        var conflict = Assert.Single((await env.Store.GetConflictPageAsync(id, 1, 100)).Rows);

        Assert.True(conflict.ExistingRowMissing);
        Assert.Empty(conflict.Changes);
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
