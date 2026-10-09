using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using Xunit.Abstractions;
using static ExcelSearch.Tests.StagingEnv;

namespace ExcelSearch.Tests;

/// <summary>
/// Integration tests against the development database (see DbFactAttribute). Each test uses unique TEST_ keys,
/// remembers its batches and removes only what it created.
/// </summary>
public class StagingStoreTests
{
    private readonly ITestOutputHelper _output;

    public StagingStoreTests(ITestOutputHelper output) => _output = output;

    [DbFact]
    public async Task Load_leaves_valid_rows_Pending_and_invalid_rows_Invalid_with_no_hash()
    {
        await using var env = Create();
        var id = await env.NewBatchAsync();

        var load = await env.Store.LoadRowsAsync(id, new[]
        {
            Row(2, env.Key("a"), "T1"),
            Row(3, env.Key("b"), "T1", invalid: true),
        }, null);

        Assert.Equal(2, load.RowsLoaded);
        var counts = await env.Store.GetCountsAsync(id);
        Assert.Equal(1, counts[RowStatus.Pending]);
        Assert.Equal(1, counts[RowStatus.Invalid]);

        var invalid = Assert.Single((await env.Store.GetPageAsync(id, RowStatus.Invalid, 1, 10)).Rows);
        Assert.Null(invalid.RowHash);
        Assert.Equal("Debit is not a valid amount", invalid.ErrorText);
        var pending = Assert.Single((await env.Store.GetPageAsync(id, RowStatus.Pending, 1, 10)).Rows);
        Assert.Equal(64, pending.RowHash!.Length);
    }

    [DbFact]
    public async Task Classify_assigns_all_five_statuses()
    {
        await using var env = Create();
        var seedBatch = await env.NewBatchAsync();

        var exact = Row(4, env.Key("exact"), "T1", 10m);
        var conflictIncoming = Row(5, env.Key("conflict"), "T1", 20m);
        var conflictExisting = Row(5, env.Key("conflict"), "T1", 99m);
        await env.SeedTransactionAsync(seedBatch, exact.PayOrderNo!, exact.TxnRefSeqNo!, exact.RowHash);
        await env.SeedTransactionAsync(seedBatch, conflictExisting.PayOrderNo!, conflictExisting.TxnRefSeqNo!, conflictExisting.RowHash);

        var id = await env.StageAsync(new[]
        {
            Row(2, env.Key("bad"), "T1", invalid: true),    // Invalid
            Row(3, env.Key("new"), "T1", 5m),                // New
            exact,                                           // ExactDuplicate
            conflictIncoming,                                // Conflict
            Row(6, env.Key("repeat"), "T1", 7m),             // New (first of an identical pair)
            Row(7, env.Key("repeat"), "T1", 7m),             // FileDuplicate
        });

        var counts = await env.Store.GetCountsAsync(id);
        Assert.Equal(0, counts[RowStatus.Pending]);
        Assert.Equal(1, counts[RowStatus.Invalid]);
        Assert.Equal(2, counts[RowStatus.New]);
        Assert.Equal(1, counts[RowStatus.ExactDuplicate]);
        Assert.Equal(1, counts[RowStatus.FileDuplicate]);
        Assert.Equal(1, counts[RowStatus.Conflict]);

        Assert.Equal(new[] { 3, 6 }, (await env.Store.GetPageAsync(id, RowStatus.New, 1, 10)).Rows.Select(r => r.ExcelRowNumber));
        Assert.Equal(7, Assert.Single((await env.Store.GetPageAsync(id, RowStatus.FileDuplicate, 1, 10)).Rows).ExcelRowNumber);
    }

    [DbFact]
    public async Task Conflict_row_stores_the_existing_row_hash_and_other_rows_keep_it_null()
    {
        await using var env = Create();
        var seedBatch = await env.NewBatchAsync();
        var existing = Row(1, env.Key("k"), "T1", 99m);
        await env.SeedTransactionAsync(seedBatch, existing.PayOrderNo!, existing.TxnRefSeqNo!, existing.RowHash);

        var id = await env.StageAsync(new[] { Row(2, env.Key("k"), "T1", 1m), Row(3, env.Key("other"), "T1", 1m) });

        var conflict = Assert.Single((await env.Store.GetPageAsync(id, RowStatus.Conflict, 1, 10)).Rows);
        Assert.Equal(existing.RowHash, conflict.ExistingRowHash);
        Assert.NotEqual(conflict.RowHash, conflict.ExistingRowHash);
        var added = Assert.Single((await env.Store.GetPageAsync(id, RowStatus.New, 1, 10)).Rows);
        Assert.Null(added.ExistingRowHash);
    }

    [DbFact]
    public async Task Identical_rows_with_the_same_key_leave_the_first_and_mark_later_ones_FileDuplicate()
    {
        await using var env = Create();
        var id = await env.StageAsync(new[]
        {
            Row(2, env.Key("k"), "T1", 5m),
            Row(3, env.Key("k"), "T1", 5m),
            Row(4, env.Key("k"), "T1", 5m),
        });

        Assert.Equal(2, Assert.Single((await env.Store.GetPageAsync(id, RowStatus.New, 1, 10)).Rows).ExcelRowNumber);
        Assert.Equal(new[] { 3, 4 }, (await env.Store.GetPageAsync(id, RowStatus.FileDuplicate, 1, 10)).Rows.Select(r => r.ExcelRowNumber));
    }

    [DbFact]
    public async Task Differing_rows_with_the_same_key_are_all_Invalid_and_name_the_other_rows()
    {
        await using var env = Create();
        var id = await env.StageAsync(new[]
        {
            Row(2, env.Key("k"), "T1", 5m),
            Row(3, env.Key("k"), "T1", 6m),
            Row(4, env.Key("k"), "T1", 5m),   // same as row 2, but the group as a whole differs
            Row(5, env.Key("solo"), "T1", 1m),
        });

        var invalid = (await env.Store.GetPageAsync(id, RowStatus.Invalid, 1, 10)).Rows;
        Assert.Equal(new[] { 2, 3, 4 }, invalid.Select(r => r.ExcelRowNumber));
        Assert.Contains("3, 4", invalid[0].ErrorText);
        Assert.Contains("2, 4", invalid[1].ErrorText);
        Assert.Contains("2, 3", invalid[2].ErrorText);
        Assert.Equal(5, Assert.Single((await env.Store.GetPageAsync(id, RowStatus.New, 1, 10)).Rows).ExcelRowNumber);
    }

    [DbFact]
    public async Task Keys_are_compared_case_insensitively_like_the_database()
    {
        await using var env = Create();
        var id = await env.StageAsync(new[]
        {
            Row(2, env.Key("abc"), "T1", 5m),
            Row(3, env.Key("ABC"), "T1", 5m + 1m),   // same key apart from case, different values
        });

        Assert.Equal(2, (await env.Store.GetPageAsync(id, RowStatus.Invalid, 1, 10)).Total);
    }

    [DbFact]
    public async Task GetPage_pages_by_row_number_and_reports_the_total()
    {
        await using var env = Create();
        var id = await env.StageAsync(Enumerable.Range(2, 25).Select(n => Row(n, env.Key("p" + n), "T1", n)));

        var page1 = await env.Store.GetPageAsync(id, RowStatus.New, 1, 10);
        var page2 = await env.Store.GetPageAsync(id, RowStatus.New, 2, 10);
        var page3 = await env.Store.GetPageAsync(id, RowStatus.New, 3, 10);
        var page4 = await env.Store.GetPageAsync(id, RowStatus.New, 4, 10);

        Assert.All(new[] { page1, page2, page3, page4 }, p => Assert.Equal(25, p.Total));
        Assert.Equal(Enumerable.Range(2, 10), page1.Rows.Select(r => r.ExcelRowNumber));
        Assert.Equal(Enumerable.Range(12, 10), page2.Rows.Select(r => r.ExcelRowNumber));
        Assert.Equal(Enumerable.Range(22, 5), page3.Rows.Select(r => r.ExcelRowNumber));
        Assert.Empty(page4.Rows);
        Assert.Equal(env.Key("p2"), page1.Rows[0].PayOrderNo);
        Assert.Equal(new DateTime(2024, 4, 3), page1.Rows[0].ValueDate);
    }

    [DbFact]
    public async Task Discard_removes_only_its_own_batch_and_leaves_Transactions_alone()
    {
        await using var env = Create();
        var seedBatch = await env.NewBatchAsync();
        await env.SeedTransactionAsync(seedBatch, env.Key("existing"), "T1", new string('b', 64));

        var mine = await env.StageAsync(Enumerable.Range(2, 30).Select(n => Row(n, env.Key("mine" + n), "T1")));
        var theirs = await env.StageAsync(Enumerable.Range(2, 30).Select(n => Row(n, env.Key("mine" + n), "T1")));

        await env.Store.DiscardAsync(mine);

        Assert.All((await env.Store.GetCountsAsync(mine)).Values, c => Assert.Equal(0, c));
        Assert.Equal(30, (await env.Store.GetCountsAsync(theirs))[RowStatus.New]);
        Assert.Equal("Discarded", await env.BatchStatusAsync(mine));
        Assert.Equal("Staged", await env.BatchStatusAsync(theirs));
        Assert.Equal(1, await env.CountTransactionsAsync(seedBatch));
    }

    [DbFact]
    public async Task Two_batches_staged_in_parallel_with_overlapping_keys_are_classified_independently()
    {
        await using var env = Create();
        var seedBatch = await env.NewBatchAsync();

        // Keys shared by both batches. "existing" is already in Transactions with hash H.
        var existing = Row(1, env.Key("existing"), "T1", 100m);
        await env.SeedTransactionAsync(seedBatch, existing.PayOrderNo!, existing.TxnRefSeqNo!, existing.RowHash);

        const int fillers = 1500;
        List<ImportRow> BatchA() => new[]
            {
                Row(2, env.Key("shared-new"), "T1", 1m),        // New
                Row(3, env.Key("existing"), "T1", 100m),        // ExactDuplicate (same as stored)
                Row(4, env.Key("a-pair"), "T1", 1m),            // New
                Row(5, env.Key("a-pair"), "T1", 1m),            // FileDuplicate
            }
            .Concat(Enumerable.Range(0, fillers).Select(n => Row(10 + n, env.Key("a-fill" + n), "T1", n)))
            .ToList();

        List<ImportRow> BatchB() => new[]
            {
                Row(2, env.Key("shared-new"), "T1", 2m),        // New in B (A's row is invisible to B)
                Row(3, env.Key("existing"), "T1", 555m),        // Conflict (differs from stored)
                Row(4, env.Key("b-pair"), "T1", 1m),            // Invalid (differing pair)
                Row(5, env.Key("b-pair"), "T1", 2m),            // Invalid
            }
            .Concat(Enumerable.Range(0, fillers).Select(n => Row(10 + n, env.Key("b-fill" + n), "T1", n)))
            .ToList();

        var ids = await Task.WhenAll(env.StageAsync(BatchA()), env.StageAsync(BatchB()));
        var (a, b) = (ids[0], ids[1]);
        Assert.NotEqual(a, b);

        var ca = await env.Store.GetCountsAsync(a);
        Assert.Equal(1 + 1 + fillers, ca[RowStatus.New]);
        Assert.Equal(1, ca[RowStatus.ExactDuplicate]);
        Assert.Equal(1, ca[RowStatus.FileDuplicate]);
        Assert.Equal(0, ca[RowStatus.Conflict] + ca[RowStatus.Invalid] + ca[RowStatus.Pending]);

        var cb = await env.Store.GetCountsAsync(b);
        Assert.Equal(1 + fillers, cb[RowStatus.New]);
        Assert.Equal(1, cb[RowStatus.Conflict]);
        Assert.Equal(2, cb[RowStatus.Invalid]);
        Assert.Equal(0, cb[RowStatus.ExactDuplicate] + cb[RowStatus.FileDuplicate] + cb[RowStatus.Pending]);

        // The invalid pair in B names rows in B only.
        var bad = (await env.Store.GetPageAsync(b, RowStatus.Invalid, 1, 10)).Rows;
        Assert.Contains("5", bad[0].ErrorText);
        Assert.Contains("4", bad[1].ErrorText);
    }

    [LargeDbFact]
    public async Task Stages_a_200k_row_file_and_reports_time_load_rate_and_memory()
    {
        await using var env = Create();
        var path = Path.Combine(Path.GetTempPath(), $"excelsearch-stage-{Guid.NewGuid():N}.xlsx");
        try
        {
            LargeFileTests.WriteSyntheticWorkbook(path, 200_000, env.Prefix + "-"); // TEST_ keys, never real ones

            GC.Collect();
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var baseline = proc.WorkingSet64;
            long peak = baseline;

            var service = new ImportPreviewService(new ExcelParser(), env.Store);
            var progress = new Progress<int>(_ =>
            {
                proc.Refresh();
                peak = Math.Max(peak, proc.WorkingSet64);
            });

            var result = await service.StageFileAsync(path, "TEST\\user", progress);
            Assert.NotNull(result.BatchId);
            env.TrackBatch(result.BatchId!.Value); // removed by env.DisposeAsync: staging rows, then the batch

            proc.Refresh();
            peak = Math.Max(peak, proc.WorkingSet64);

            _output.WriteLine($"Rows loaded: {result.RowsLoaded:N0}");
            _output.WriteLine($"Counts: {string.Join(", ", result.Counts.Select(c => $"{c.Key}={c.Value:N0}"))}");
            _output.WriteLine($"Total time (parse + load + classify + counts): {result.TotalElapsed.TotalSeconds:F1}s");
            _output.WriteLine($"Load rate: {result.LoadRowsPerSecond:N0} rows/s");
            _output.WriteLine($"Working set: baseline {baseline / 1048576.0:F0} MB, peak {peak / 1048576.0:F0} MB, growth {(peak - baseline) / 1048576.0:F0} MB");

            Assert.Equal(200_000, result.RowsLoaded);
            Assert.Equal(200_000, result.Counts[RowStatus.New]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ImportPreviewServiceTests
{
    [DbFact]
    public async Task Rejected_file_creates_no_batch()
    {
        await using var env = Create();
        var dir = Directory.CreateTempSubdirectory("excelsearch-svc-").FullName;
        var fileName = $"{env.Prefix}-bad.xlsx"; // unique, so parallel tests cannot affect the check
        var path = Path.Combine(dir, fileName);
        try
        {
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                wb.AddWorksheet("S").Cell(1, 1).Value = "Not the right headers";
                wb.SaveAs(path);
            }

            await using var db = await env.Factory.CreateDbContextAsync();
            var result = await new ImportPreviewService(new ExcelParser(), env.Store).StageFileAsync(path, "TEST\\user", null);

            Assert.Null(result.BatchId);
            Assert.NotEmpty(result.FileErrors);
            Assert.False(db.ImportBatches.Any(b => b.FileName == fileName));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
