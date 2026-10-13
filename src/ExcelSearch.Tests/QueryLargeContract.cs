using System.Diagnostics;
using System.Text;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Query;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data;
using ExcelSearch.Data.SqlServer;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace ExcelSearch.Tests;

/// <summary>
/// Written once, run on both providers (shells in ContractTestShells.cs). Skipped unless EXCELSEARCH_RUN_LARGE=1. Seeds 500,000 TEST_ rows through the normal stage -> classify -> commit path,
/// times the Query screen's searches against them, then times RefreshAgainstDatabaseAsync and GetConflictPageAsync on a
/// 200,000-row batch of mostly conflicting rows. Everything it created is deleted afterwards (StagingEnv does the chunked
/// deletes by TEST_ prefix). The report is printed and also written to %TEMP%\ExcelSearch_query_large_report.txt.
/// </summary>
public abstract class QueryLargeContract
{
    protected abstract StagingEnv NewEnv(bool large = false);

    private const int Batches = 5;
    private const int RowsPerBatch = 100_000;
    private const int ConflictBatchRows = 200_000;

    private static readonly string[] First =
    {
        "Ali", "Ahmed", "Bilal", "Hassan", "Usman", "Omar", "Zain", "Fahad", "Imran", "Kamran", "Saad", "Tariq", "Waqar", "Yasir",
        "Ayesha", "Fatima", "Sara", "Hina", "Maryam", "Nida", "Sana", "Zara", "Amna", "Bushra", "Iqra", "Khadija", "Mehwish", "Rabia",
        "Muhammad", "Abdul", "Shahid", "Nadeem", "Asif", "Rizwan", "Faisal", "Junaid", "Naveed", "Sajid", "Tahir", "Zubair",
    };

    private static readonly string[] Last =
    {
        "Khan", "Malik", "Sheikh", "Butt", "Chaudhry", "Raja", "Mirza", "Qureshi", "Siddiqui", "Ansari", "Baig", "Bhatti", "Cheema",
        "Dar", "Gill", "Hashmi", "Iqbal", "Javed", "Kayani", "Lodhi", "Mughal", "Niazi", "Orakzai", "Pasha", "Rana", "Syed", "Tarar",
        "Umar", "Virk", "Wani", "Yousafzai", "Zaidi", "Abbasi", "Awan", "Bajwa", "Durrani", "Farooqi", "Gondal", "Hussain", "Jutt",
        "Kiani", "Lashari", "Memon", "Nawaz", "Osmani", "Pirzada", "Rehman", "Satti", "Toor", "Warraich", "Aziz", "Bukhari", "Cheena",
        "Dogar", "Ejaz", "Farid", "Ghani", "Haider", "Ilyas", "Jaffri",
    };

    private readonly ITestOutputHelper _output;
    private readonly StringBuilder _report = new();
    private string ReportName => GetType().Name.EndsWith("Sqlite") ? "sqlite" : "sqlserver";

    protected QueryLargeContract(ITestOutputHelper output) => _output = output;

    private void Say(string line)
    {
        _output.WriteLine(line);
        _report.AppendLine(line);
    }

    /// <summary>Row i of the synthetic data set: realistic spread of dates, banks, projects, names and amounts.</summary>
    private static ImportRow Gen(string prefix, int i, int excelRow, bool changed = false)
    {
        var row = new ImportRow
        {
            ExcelRowNumber = excelRow,
            PayOrderNo = $"{prefix}-{i:D7}",
            TxnRefSeqNo = "1",
            ValueDate = new DateTime(2020, 1, 1).AddDays(i % 2000),
            BranchCode = $"BC{i % 80}",
            BranchName = $"Branch {i % 80}",
            RegNo = $"REG{i % 100000:D6}",
            ApplicationNo = $"APP-{i:D7}",
            PlotNo = $"{i % 900}",
            StreetNo = $"{i % 40}",
            ChallanNo = $"CH{i:D8}",
            ChequeInstNo = $"CQ{i:D8}",
            CustomerName = $"{First[(i * 31) % First.Length]} {Last[(i * 17) % Last.Length]}",
            Cnic = $"{35200 + i % 97:D5}{i:D8}",
            Discount = i % 11 == 0 ? 500m : null,
            DownPaymentAmount = i % 3 == 0 ? (i % 400 + 1) * 1000m : null,
            Debit = (i * 104_729L % 700) * 5m + (changed ? 1m : 0m),    // "changed" is what turns a re-sent row into a conflict
            Credit = ((i * 7_919L) % 50_000 + 1) * 10m,
            RunningBalance = i * 3.5m,
            BankName = $"Bank {i % 12}",
            AccountNo = $"{i % 3000:D8}",
            Project = $"Project {(i * 3) % 25}",
            DealerName = $"Dealer {(i * 7) % 150}",
            DataSource = $"Source {i % 5}",
            Narration1 = $"Online transfer reference {i}",
            Narration2 = "Received against installment",
        };
        row.RowHash = RowHasher.Compute(row);
        return row;
    }

    private static IEnumerable<ImportRow> Rows(string prefix, int from, int count)
    {
        for (var n = 0; n < count; n++) yield return Gen(prefix, from + n, n + 2);
    }

    // ---- measuring -------------------------------------------------------------------------------------

    private sealed record Timing(double Seconds, bool TimedOut, int Rows, long Peak);

    /// <summary>Runs <paramref name="work"/> while a sampler records the peak working set of this process.</summary>
    private static async Task<(double Seconds, long PeakGrowthBytes)> MeasureAsync(Func<Task> work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var baseline = proc.WorkingSet64;
        var peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                proc.Refresh();
                peak = Math.Max(peak, proc.WorkingSet64);
                try { await Task.Delay(50, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        var sw = Stopwatch.StartNew();
        try { await work(); }
        finally
        {
            sw.Stop();
            stop.Cancel();
            await sampler;
        }
        proc.Refresh();
        peak = Math.Max(peak, proc.WorkingSet64);
        return (sw.Elapsed.TotalSeconds, Math.Max(0, peak - baseline));
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0} MB";

    private async Task<(double Page, double Count, double Totals, double Parallel, int Rows, int Total, string Note)> TimeSearchAsync(
        TransactionQueryServiceBase svc, TransactionFilter filter, TransactionSort sort)
    {
        double page = 0, count = 0, totals = 0, parallel = 0;
        var rows = 0;
        var total = 0;
        var note = "";

        async Task<double> T<TR>(Func<Task<TR>> f, Action<TR>? take = null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var r = await f();
                take?.Invoke(r);
            }
            catch (QueryTimeoutException)
            {
                note = "TIMEOUT (60 s)";
            }
            return sw.Elapsed.TotalSeconds;
        }

        page = await T(() => svc.GetPageAsync(filter, sort, 1, 100), r => rows = r.Count);
        count = await T(() => svc.CountAsync(filter), r => total = r);
        totals = await T(() => svc.GetTotalsAsync(filter));

        // What the screen does: all three at once, the page usually first.
        var sw = Stopwatch.StartNew();
        try
        {
            await Task.WhenAll(svc.GetPageAsync(filter, sort, 1, 100), svc.CountAsync(filter), svc.GetTotalsAsync(filter));
        }
        catch (QueryTimeoutException)
        {
            note = "TIMEOUT (60 s)";
        }
        parallel = sw.Elapsed.TotalSeconds;
        return (page, count, totals, parallel, rows, total, note);
    }

    // ---- the test --------------------------------------------------------------------------------------

    [SkippableFact, Trait("Category", "Large")]
    public async Task Query_screen_timings_at_500000_rows_and_refresh_and_conflict_paging_at_200000()
    {
        await using var env = NewEnv(large: true);
        var svc = QuerySeed.Service(env);
        var p = env.Prefix;
        var total = Stopwatch.StartNew();

        try
        {
            // ===== 1. Seed 500,000 rows through the normal stage -> classify -> commit path =====
            Say($"# Query screen, large run on {env.ProviderName} ({DateTime.Now:yyyy-MM-dd HH:mm})");
            Say("");
            Say($"## 1. Seeding {Batches * RowsPerBatch:N0} rows ({Batches} batches of {RowsPerBatch:N0}) through stage + commit");
            var seedWatch = Stopwatch.StartNew();
            for (var b = 0; b < Batches; b++)
            {
                var from = b * RowsPerBatch;
                var batchId = await env.NewBatchAsync();
                var sw = Stopwatch.StartNew();
                var load = await env.Store.LoadRowsAsync(batchId, Rows(p, from, RowsPerBatch), null);
                var loadSec = sw.Elapsed.TotalSeconds;
                sw.Restart();
                await env.Store.ClassifyAsync(batchId);
                var classifySec = sw.Elapsed.TotalSeconds;
                sw.Restart();
                var commit = await env.Store.CommitAsync(batchId);
                Assert.True(commit.Success, commit.Message);
                Say($"- batch {b + 1}: load {loadSec:0.0} s, classify {classifySec:0.0} s, commit {sw.Elapsed.TotalSeconds:0.0} s ({load.RowsLoaded:N0} rows)");
            }
            Assert.Equal(Batches * RowsPerBatch, await env.CountTransactionsWithPrefixAsync());
            Say($"- seeded and committed in {seedWatch.Elapsed.TotalSeconds:0} s; {await env.CountAllTransactionsAsync():N0} rows now in Transactions");
            Say("");

            // ===== 2. Query timings =====
            var banks = new[] { "Bank 3" };
            var scenarios = new (string Name, TransactionFilter Filter, TransactionSort Sort)[]
            {
                ("Date range (one month)", new() { ValueDateFrom = new DateOnly(2023, 3, 1), ValueDateTo = new DateOnly(2023, 3, 31) }, TransactionSort.Default),
                ("Exact CNIC", new() { Cnic = TextCriterion.Exact(Gen(p, 250_001, 0).Cnic!) }, TransactionSort.Default),
                ("Application no starts with 'APP-012'", new() { ApplicationNo = TextCriterion.StartsWith("APP-012") }, TransactionSort.Default),
                ("Customer name contains 'Khan'", new() { CustomerName = "Khan" }, TransactionSort.Default),
                ("Bank + project", new() { BankNames = banks, Projects = new[] { "Project 9" } }, TransactionSort.Default),
                ("Credit between 100,000 and 120,000", new() { CreditMin = 100_000m, CreditMax = 120_000m }, TransactionSort.Default),
                ("Date range + bank + customer contains 'Ahmed'", new()
                {
                    ValueDateFrom = new DateOnly(2022, 1, 1), ValueDateTo = new DateOnly(2022, 6, 30),
                    BankNames = new[] { "Bank 5" }, CustomerName = "Ahmed",
                }, TransactionSort.Default),
                ("Unfiltered, sorted by ValueDate (default)", TransactionFilter.Empty, TransactionSort.Default),
                ("Unfiltered, sorted by CustomerName", TransactionFilter.Empty, new TransactionSort(TransactionColumn.CustomerName, false)),
                ("(extra) Unfiltered, sorted by Credit", TransactionFilter.Empty, new TransactionSort(TransactionColumn.Credit, true)),
                ("(extra) Pay order no starts with the test prefix (all 500,000)", new() { PayOrderNo = TextCriterion.StartsWith(p) }, TransactionSort.Default),
            };

            Say("## 2. Search timings (first page of 100 + count + totals), seconds");
            Say("");
            Say("| Search | Matching rows | Page | Count | Totals | All three in parallel | Repeat (warm), parallel | Note |");
            Say("|---|---:|---:|---:|---:|---:|---:|---|");

            var queryMemory = await MeasureAsync(async () =>
            {
                foreach (var (name, filter, sort) in scenarios)
                {
                    var first = await TimeSearchAsync(svc, filter, sort);
                    var again = await TimeSearchAsync(svc, filter, sort);
                    Say($"| {name} | {first.Total:N0} | {first.Page:0.00} | {first.Count:0.00} | {first.Totals:0.00} | {first.Parallel:0.00} | {again.Parallel:0.00} | {first.Note} |");
                }
            });
            Say("");
            Say($"Memory while running all searches: working set grew by at most {Mb(queryMemory.PeakGrowthBytes)} (the screen holds one page of at most 500 rows).");
            Say("");

            // ===== 3. Dropdown values =====
            Say("## 3. Dropdown values (distinct), seconds");
            Say("");
            foreach (var field in Enum.GetValues<DistinctField>())
            {
                svc.InvalidateDistinctValues();
                var sw = Stopwatch.StartNew();
                var values = await svc.GetDistinctValuesAsync(field);
                Say($"- {field}: {sw.Elapsed.TotalSeconds:0.00} s ({values.Count:N0} values)");
            }
            Say("");

            // ===== 4. Export memory =====
            Say("## 4. Export of all 500,000 rows (keyset chunks of 10,000)");
            Say("");
            var all = new TransactionFilter { PayOrderNo = TextCriterion.StartsWith(p) };
            foreach (var format in new[] { ExportFormat.Csv, ExportFormat.Xlsx })
            {
                var path = Path.Combine(Path.GetTempPath(), $"{p}-export.{(format == ExportFormat.Csv ? "csv" : "xlsx")}");
                try
                {
                    QueryExportResult? result = null;
                    var m = await MeasureAsync(async () => result = await svc.ExportAsync(all, format, path));
                    Assert.True(result!.Success, result.Message);
                    Assert.Equal(Batches * RowsPerBatch, result.RowsWritten);
                    Say($"- {format}: {m.Seconds:0.0} s ({result.RowsWritten / m.Seconds:N0} rows/s), file {new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB, working set grew by at most {Mb(m.PeakGrowthBytes)}");
                }
                finally
                {
                    File.Delete(path);
                    File.Delete(path + ".part");
                }
            }
            Say("");

            // ===== 5. Cancelling a slow search in flight =====
            Say("## 5. Cancelling a running search");
            Say("");
            using (var cts = new CancellationTokenSource())
            {
                var sw = Stopwatch.StartNew();
                var slow = svc.GetPageAsync(new TransactionFilter { CustomerName = "zzz-nothing" }, new TransactionSort(TransactionColumn.Credit, true), 5000, 100, cts.Token);
                cts.CancelAfter(150);
                try { await slow; Say($"- the search finished before it could be cancelled ({sw.Elapsed.TotalSeconds:0.0} s)"); }
                catch (OperationCanceledException) { Say($"- cancelled {sw.Elapsed.TotalSeconds:0.00} s after it started (token fired at 0.15 s)"); }
            }
            Say("");

            // ===== 6. Refresh + conflict paging on a 200,000-row mostly-conflicting batch =====
            Say($"## 6. A {ConflictBatchRows:N0}-row batch of mostly conflicting rows against the {Batches * RowsPerBatch:N0} existing rows");
            Say("");
            // Rows 0..199,999 again with the same keys; 9 of 10 carry a changed Debit (conflict), every 10th is identical (already there).
            IEnumerable<ImportRow> Resent()
            {
                for (var i = 0; i < ConflictBatchRows; i++) yield return Gen(p, i, i + 2, changed: i % 10 != 0);
            }

            var conflictBatch = await env.NewBatchAsync();
            var staged = await MeasureAsync(async () =>
            {
                await env.Store.LoadRowsAsync(conflictBatch, Resent(), null);
                await env.Store.ClassifyAsync(conflictBatch);
            });
            var counts = await env.Store.GetCountsAsync(conflictBatch);
            Say($"- stage + classify: {staged.Seconds:0.0} s, working set grew by at most {Mb(staged.PeakGrowthBytes)}");
            Say($"- classified as: Conflict {counts.GetValueOrDefault(RowStatus.Conflict):N0}, Already in database {counts.GetValueOrDefault(RowStatus.ExactDuplicate):N0}, New {counts.GetValueOrDefault(RowStatus.New):N0}");
            Assert.True(counts.GetValueOrDefault(RowStatus.Conflict) >= ConflictBatchRows * 8 / 10);

            RefreshResult? unchanged = null;
            var r1 = await MeasureAsync(async () => unchanged = await env.Store.RefreshAgainstDatabaseAsync(conflictBatch));
            Say($"- RefreshAgainstDatabaseAsync, nothing changed: {r1.Seconds:0.0} s, working set grew by at most {Mb(r1.PeakGrowthBytes)} ({unchanged!.Describe()})");

            // Another user changes 20,000 of the existing rows (the ones that were exact duplicates), in chunks of 4,000.
            var dupKeys = Enumerable.Range(0, ConflictBatchRows).Where(i => i % 10 == 0).Select(i => $"{p}-{i:D7}").ToArray();
            await using (var db = await env.Factory.CreateDbContextAsync())
            {
                db.Database.SetCommandTimeout(600);
                foreach (var chunk in dupKeys.Chunk(4_000))
                {
                    var keys = chunk;
                    await db.Transactions.Where(t => keys.Contains(t.PayOrderNo)).ExecuteUpdateAsync(s => s.SetProperty(t => t.RowHash, new string('b', 64)));
                }
            }

            RefreshResult? changed = null;
            var r2 = await MeasureAsync(async () => changed = await env.Store.RefreshAgainstDatabaseAsync(conflictBatch));
            Say($"- RefreshAgainstDatabaseAsync, {dupKeys.Length:N0} rows changed in the database: {r2.Seconds:0.0} s, working set grew by at most {Mb(r2.PeakGrowthBytes)} ({changed!.Describe()})");
            Assert.True(changed.RowsChanged > 0);

            foreach (var page in new[] { 1, 500, 1800 })
            {
                PagedConflicts? result = null;
                var m = await MeasureAsync(async () => result = await env.Store.GetConflictPageAsync(conflictBatch, page, 100));
                Say($"- GetConflictPageAsync page {page:N0} of 100 rows: {m.Seconds:0.00} s ({result!.Rows.Count} rows, {result.Rows.Count(r => r.ChangeCount > 0)} with differences), working set grew by at most {Mb(m.PeakGrowthBytes)}");
            }
            Say("");
            Say($"Whole test: {total.Elapsed.TotalMinutes:0.0} min. Cleaning up the seeded rows (chunked deletes by TEST_ prefix)...");
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"ExcelSearch_query_large_report_{ReportName}.txt"), _report.ToString());
        }
    }
}
