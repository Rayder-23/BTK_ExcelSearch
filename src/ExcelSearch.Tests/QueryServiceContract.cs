using ExcelSearch.Core.Query;
using ExcelSearch.Data;
using ExcelSearch.Data.SqlServer;
using ExcelSearch.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExcelSearch.Tests;

/// <summary>
/// The Query screen's search, written once and run against EVERY provider (SQL Server dev database, temp SQLite file; see
/// ContractTestShells.cs). Each test seeds its own rows (TEST_ keys, its own import batch), searches inside that batch, and compares
/// the answer with the same rows filtered in memory ("the oracle"). One case per filter kind, plus paging, sorting, totals, details and export.
/// </summary>
public abstract class QueryServiceContract
{
    protected abstract StagingEnv NewEnv(bool large = false);

    private static readonly StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    private static TransactionFilter In(QuerySeed.Seeded s) => new() { ImportBatchId = s.BatchId };

    private static async Task<TransactionPage> RunAsync(TransactionQueryServiceBase svc, TransactionFilter f, TransactionSort? sort = null)
        => await svc.SearchAsync(f, sort ?? TransactionSort.Default, 1, 500);

    /// <summary>The filter must return exactly the rows the in-memory predicate selects, and its count must agree.</summary>
    private static async Task AssertMatchesAsync(TransactionQueryServiceBase svc, QuerySeed.Seeded seed, string what,
        TransactionFilter filter, Func<Transaction, bool> expected)
    {
        var want = seed.Rows.Where(expected).Select(r => r.PayOrderNo).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var page = await RunAsync(svc, filter);
        var got = page.Rows.Select(r => r.PayOrderNo).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(want.SequenceEqual(got), $"{what}: expected {want.Length} rows, got {got.Length}.");
        Assert.True(want.Length == page.TotalCount, $"{what}: total count {page.TotalCount} != {want.Length}.");
        Assert.Equal(want.Length, await svc.CountAsync(filter));
    }

    // ---- individual filters ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task Every_reference_field_supports_starts_with_and_exact()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var s = In(seed);

        var fields = new (string Name, Func<TransactionFilter, TextCriterion, TransactionFilter> With, Func<Transaction, string?> Get)[]
        {
            ("Cnic", (f, c) => f with { Cnic = c }, t => t.Cnic),
            ("ApplicationNo", (f, c) => f with { ApplicationNo = c }, t => t.ApplicationNo),
            ("RegNo", (f, c) => f with { RegNo = c }, t => t.RegNo),
            ("PayOrderNo", (f, c) => f with { PayOrderNo = c }, t => t.PayOrderNo),
            ("TxnRefSeqNo", (f, c) => f with { TxnRefSeqNo = c }, t => t.TxnRefSeqNo),
            ("ChallanNo", (f, c) => f with { ChallanNo = c }, t => t.ChallanNo),
            ("ChequeInstNo", (f, c) => f with { ChequeInstNo = c }, t => t.ChequeInstNo),
            ("AccountNo", (f, c) => f with { AccountNo = c }, t => t.AccountNo),
        };

        foreach (var (name, with, get) in fields)
        {
            var full = get(seed.Rows[11])!;      // row i = 12
            var prefix = full[..^1];             // drops the last character: matches several rows

            await AssertMatchesAsync(svc, seed, $"{name} starts with '{prefix}'",
                with(s, TextCriterion.StartsWith(prefix)), t => get(t)!.StartsWith(prefix, Ci));
            await AssertMatchesAsync(svc, seed, $"{name} exact '{full}'",
                with(s, TextCriterion.Exact(full)), t => string.Equals(get(t), full, Ci));
            await AssertMatchesAsync(svc, seed, $"{name} exact on a partial value '{prefix}'",
                with(s, TextCriterion.Exact(prefix)), _ => false);

            // The default mode is "starts with".
            Assert.Equal(TextMatchMode.StartsWith, new TextCriterion("x").Mode);
        }
    }

    [SkippableFact]
    public async Task Text_filters_trim_ignore_case_and_treat_like_wildcards_as_plain_text()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var s = In(seed);

        // Spaces around the value are ignored; the database collation is case-insensitive.
        await AssertMatchesAsync(svc, seed, "trimmed exact", s with { ApplicationNo = TextCriterion.Exact("  app-005  ") },
            t => t.ApplicationNo == "APP-005");

        // '_' and '%' are LIKE wildcards in SQL; typed by a user they must mean themselves.
        await AssertMatchesAsync(svc, seed, "underscore is literal", s with { ApplicationNo = TextCriterion.StartsWith("APP_") }, _ => false);
        await AssertMatchesAsync(svc, seed, "percent is literal", s with { ApplicationNo = TextCriterion.StartsWith("%") }, _ => false);
        await AssertMatchesAsync(svc, seed, "dash prefix still works", s with { ApplicationNo = TextCriterion.StartsWith("APP-0") }, t => t.ApplicationNo!.StartsWith("APP-0"));
        await AssertMatchesAsync(svc, seed, "blank text is no filter", s with { ApplicationNo = TextCriterion.StartsWith("   ") }, _ => true);
    }

    [SkippableFact]
    public async Task Customer_name_matches_anywhere_in_the_name_and_literally()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var s = In(seed);

        await AssertMatchesAsync(svc, seed, "contains 'khan'", s with { CustomerName = "khan" }, t => t.CustomerName!.Contains("khan", Ci));
        await AssertMatchesAsync(svc, seed, "starts with 'ali' (case-insensitive)",
            s with { CustomerName = "ali", CustomerNameMode = TextMatchMode.StartsWith }, t => t.CustomerName!.StartsWith("Ali", Ci));
        await AssertMatchesAsync(svc, seed, "starts with does not match in the middle",
            s with { CustomerName = "khan", CustomerNameMode = TextMatchMode.StartsWith }, t => t.CustomerName!.StartsWith("khan", Ci));
        await AssertMatchesAsync(svc, seed, "contains with padding", s with { CustomerName = "  Ahmed 1" }, t => t.CustomerName!.Contains("Ahmed 1", Ci));
        await AssertMatchesAsync(svc, seed, "percent is literal", s with { CustomerName = "100%" }, t => t.CustomerName!.Contains("100%"));
        await AssertMatchesAsync(svc, seed, "underscore is literal", s with { CustomerName = "r_X" }, t => t.CustomerName!.Contains("r_X"));
        await AssertMatchesAsync(svc, seed, "a lone underscore matches only a real underscore", s with { CustomerName = "_" }, t => t.CustomerName!.Contains('_'));
        await AssertMatchesAsync(svc, seed, "quotes and comma", s with { CustomerName = "Khan, \"Ali\"" }, t => t.CustomerName!.Contains("Khan, \"Ali\""));
    }

    [SkippableFact]
    public async Task Cnic_is_compared_by_digits_only()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var s = In(seed);

        var cnic = seed.Rows[4].Cnic!;                       // "4210000000005"
        var dashed = $"{cnic[..5]}-{cnic[5..12]}-{cnic[12..]}";   // 42100-0000000-5

        await AssertMatchesAsync(svc, seed, "exact, dashes typed", s with { Cnic = TextCriterion.Exact(dashed) }, t => t.Cnic == cnic);
        await AssertMatchesAsync(svc, seed, "exact, spaces typed", s with { Cnic = TextCriterion.Exact(" " + cnic.Insert(5, " ") + " ") }, t => t.Cnic == cnic);
        await AssertMatchesAsync(svc, seed, "starts with, dashes typed", s with { Cnic = TextCriterion.StartsWith("42100-00000") }, t => t.Cnic!.StartsWith("4210000000"));
        await AssertMatchesAsync(svc, seed, "letters only matches nothing", s with { Cnic = TextCriterion.StartsWith("abc") }, _ => false);

        // The filter object itself normalizes (this is what the service relies on).
        Assert.Equal("4210000000005", (new TransactionFilter { Cnic = TextCriterion.Exact("42100-0000000-5") }).Normalized().Cnic.Value);
    }

    [SkippableFact]
    public async Task Value_date_range_is_inclusive_at_both_ends()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);   // 2024-01-01 .. 2024-01-10, three rows per day
        var svc = QuerySeed.Service(env);
        var s = In(seed);
        DateOnly D(int day) => new(2024, 1, day);

        await AssertMatchesAsync(svc, seed, "from only", s with { ValueDateFrom = D(8) }, t => t.ValueDate >= D(8));
        await AssertMatchesAsync(svc, seed, "to only", s with { ValueDateTo = D(3) }, t => t.ValueDate <= D(3));
        await AssertMatchesAsync(svc, seed, "from..to", s with { ValueDateFrom = D(3), ValueDateTo = D(5) }, t => t.ValueDate >= D(3) && t.ValueDate <= D(5));
        await AssertMatchesAsync(svc, seed, "a single day", s with { ValueDateFrom = D(4), ValueDateTo = D(4) }, t => t.ValueDate == D(4));
        await AssertMatchesAsync(svc, seed, "first day", s with { ValueDateFrom = D(1), ValueDateTo = D(1) }, t => t.ValueDate == D(1));
        await AssertMatchesAsync(svc, seed, "last day", s with { ValueDateFrom = D(10), ValueDateTo = D(10) }, t => t.ValueDate == D(10));
        await AssertMatchesAsync(svc, seed, "before everything", s with { ValueDateTo = new DateOnly(2023, 12, 31) }, _ => false);
        await AssertMatchesAsync(svc, seed, "after everything", s with { ValueDateFrom = D(11) }, _ => false);
        await AssertMatchesAsync(svc, seed, "from after to", s with { ValueDateFrom = D(6), ValueDateTo = D(2) }, _ => false);

        Assert.Equal(3, (await RunAsync(svc, s with { ValueDateFrom = D(4), ValueDateTo = D(4) })).TotalCount);
    }

    [SkippableFact]
    public async Task Multi_select_lists_match_any_of_the_chosen_values()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var s = In(seed);
        var p = env.Prefix;

        await AssertMatchesAsync(svc, seed, "one bank", s with { BankNames = new[] { $"{p} HBL" } }, t => t.BankName == $"{p} HBL");
        await AssertMatchesAsync(svc, seed, "two banks", s with { BankNames = new[] { $"{p} HBL", $"{p} UBL" } }, t => t.BankName != $"{p} Meezan");
        await AssertMatchesAsync(svc, seed, "bank is case-insensitive", s with { BankNames = new[] { $"{p} hbl".ToLowerInvariant() } }, t => t.BankName == $"{p} HBL");
        await AssertMatchesAsync(svc, seed, "unknown bank", s with { BankNames = new[] { "Nobody Bank" } }, _ => false);
        await AssertMatchesAsync(svc, seed, "project", s with { Projects = new[] { $"{p} Alpha" } }, t => t.Project == $"{p} Alpha");
        await AssertMatchesAsync(svc, seed, "dealers", s with { DealerNames = new[] { $"{p} D0", $"{p} D3" } }, t => t.DealerName == $"{p} D0" || t.DealerName == $"{p} D3");
        await AssertMatchesAsync(svc, seed, "branch", s with { BranchNames = new[] { $"{p} Br2" } }, t => t.BranchName == $"{p} Br2");
        await AssertMatchesAsync(svc, seed, "data source", s with { DataSources = new[] { $"{p} SrcA" } }, t => t.DataSource == $"{p} SrcA");
        await AssertMatchesAsync(svc, seed, "blank entries are ignored", s with { BankNames = new[] { "", "  " } }, _ => true);
        await AssertMatchesAsync(svc, seed, "two different lists are ANDed", s with { BankNames = new[] { $"{p} HBL" }, Projects = new[] { $"{p} Alpha" } },
            t => t.BankName == $"{p} HBL" && t.Project == $"{p} Alpha");
    }

    [SkippableFact]
    public async Task Amount_ranges_are_inclusive_and_skip_empty_amounts()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var s = In(seed);

        await AssertMatchesAsync(svc, seed, "credit min", s with { CreditMin = 1500m }, t => t.Credit >= 1500m);
        await AssertMatchesAsync(svc, seed, "credit max", s with { CreditMax = 500m }, t => t.Credit <= 500m);
        await AssertMatchesAsync(svc, seed, "credit between (edges included)", s with { CreditMin = 300m, CreditMax = 700m }, t => t.Credit >= 300m && t.Credit <= 700m);
        await AssertMatchesAsync(svc, seed, "credit exactly one value", s with { CreditMin = 700m, CreditMax = 700m }, t => t.Credit == 700m);
        await AssertMatchesAsync(svc, seed, "debit between", s with { DebitMin = 100m, DebitMax = 200m }, t => t.Debit >= 100m && t.Debit <= 200m);
        await AssertMatchesAsync(svc, seed, "debit zero", s with { DebitMin = 0m, DebitMax = 0m }, t => t.Debit == 0m);
        await AssertMatchesAsync(svc, seed, "down payment min (blank down payments excluded)", s with { DownPaymentMin = 1m }, t => t.DownPaymentAmount >= 1m);
        await AssertMatchesAsync(svc, seed, "down payment max", s with { DownPaymentMax = 10000m }, t => t.DownPaymentAmount <= 10000m);
        await AssertMatchesAsync(svc, seed, "min above max", s with { CreditMin = 900m, CreditMax = 100m }, _ => false);
        await AssertMatchesAsync(svc, seed, "credit and debit together", s with { CreditMin = 500m, DebitMax = 150m }, t => t.Credit >= 500m && t.Debit <= 150m);
    }

    [SkippableFact]
    public async Task Import_batch_filter_isolates_one_batch()
    {
        await using var env = NewEnv();
        var a = await QuerySeed.SeedStandardAsync(env);

        var batchB = await env.NewBatchAsync();
        var extra = Enumerable.Range(101, 5).Select(i => QuerySeed.Standard(env.Prefix, batchB, i)).ToList();
        await QuerySeed.InsertAsync(env, extra);
        var svc = QuerySeed.Service(env);

        Assert.Equal(30, (await RunAsync(svc, new TransactionFilter { ImportBatchId = a.BatchId })).TotalCount);
        var b = await RunAsync(svc, new TransactionFilter { ImportBatchId = batchB });
        Assert.Equal(5, b.TotalCount);
        Assert.All(b.Rows, r => Assert.StartsWith(env.Prefix, r.PayOrderNo));
        Assert.Equal(0, (await RunAsync(svc, new TransactionFilter { ImportBatchId = int.MaxValue })).TotalCount);
        Assert.True((await svc.CountAsync(TransactionFilter.Empty)) >= 35);
    }

    [SkippableFact]
    public async Task Filters_combine_with_and()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var p = env.Prefix;

        var f = In(seed) with
        {
            ValueDateFrom = new DateOnly(2024, 1, 2),
            ValueDateTo = new DateOnly(2024, 1, 9),
            CustomerName = "khan",
            BankNames = new[] { $"{p} HBL", $"{p} UBL" },
            Projects = new[] { $"{p} Beta" },
            CreditMin = 300m,
            ApplicationNo = TextCriterion.StartsWith("APP-0"),
            Cnic = TextCriterion.StartsWith("4210"),
        };
        await AssertMatchesAsync(svc, seed, "combined", f, t =>
            t.ValueDate >= new DateOnly(2024, 1, 2) && t.ValueDate <= new DateOnly(2024, 1, 9) &&
            t.CustomerName!.Contains("khan", Ci) && t.BankName != $"{p} Meezan" && t.Project == $"{p} Beta" &&
            t.Credit >= 300m && t.ApplicationNo!.StartsWith("APP-0") && t.Cnic!.StartsWith("4210"));

        // A filter that cannot be satisfied.
        await AssertMatchesAsync(svc, seed, "contradiction", In(seed) with { PayOrderNo = TextCriterion.Exact($"{p}-PO001"), TxnRefSeqNo = TextCriterion.Exact("TX002") }, _ => false);
    }

    // ---- paging and sorting ----------------------------------------------------------------------------

    [SkippableFact]
    public async Task Paging_returns_every_row_once_and_the_total_count()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var f = In(seed);

        var seen = new List<string>();
        var sizes = new List<int>();
        for (var page = 1; page <= 5; page++)
        {
            var result = await svc.SearchAsync(f, TransactionSort.Default, page, 7);
            Assert.Equal(30, result.TotalCount);
            Assert.Equal(5, result.PageCount);
            Assert.Equal(page, result.PageNumber);
            sizes.Add(result.Rows.Count);
            seen.AddRange(result.Rows.Select(r => r.PayOrderNo));
        }
        Assert.Equal(new[] { 7, 7, 7, 7, 2 }, sizes);
        Assert.Equal(30, seen.Distinct().Count());                          // no row twice, none skipped
        Assert.Equal(seed.Rows.Select(r => r.PayOrderNo).OrderBy(x => x), seen.OrderBy(x => x));

        var beyond = await svc.SearchAsync(f, TransactionSort.Default, 6, 7);
        Assert.Empty(beyond.Rows);
        Assert.Equal(30, beyond.TotalCount);

        // Page 0 or negative is treated as page 1.
        var zero = await svc.GetPageAsync(f, TransactionSort.Default, 0, 7);
        var first = await svc.GetPageAsync(f, TransactionSort.Default, 1, 7);
        Assert.Equal(first.Select(r => r.PayOrderNo), zero.Select(r => r.PayOrderNo));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.GetPageAsync(f, TransactionSort.Default, 1, 0));
    }

    [SkippableFact]
    public async Task Money_text_and_date_columns_sort_the_whole_result_and_pages_never_overlap()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var f = In(seed);

        // One key selector per whitelisted column (null values sort first ascending, like SQL Server).
        var keys = new Dictionary<TransactionColumn, Func<Transaction, IComparable?>>
        {
            [TransactionColumn.ValueDate] = t => t.ValueDate,
            [TransactionColumn.PayOrderNo] = t => t.PayOrderNo.ToUpperInvariant(),
            [TransactionColumn.TxnRefSeqNo] = t => t.TxnRefSeqNo.ToUpperInvariant(),
            [TransactionColumn.CustomerName] = t => t.CustomerName?.ToUpperInvariant(),
            [TransactionColumn.Cnic] = t => t.Cnic,
            [TransactionColumn.ApplicationNo] = t => t.ApplicationNo,
            [TransactionColumn.RegNo] = t => t.RegNo,
            [TransactionColumn.ChallanNo] = t => t.ChallanNo,
            [TransactionColumn.ChequeInstNo] = t => t.ChequeInstNo,
            [TransactionColumn.BankName] = t => t.BankName?.ToUpperInvariant(),
            [TransactionColumn.AccountNo] = t => t.AccountNo,
            [TransactionColumn.Project] = t => t.Project?.ToUpperInvariant(),
            [TransactionColumn.DealerName] = t => t.DealerName?.ToUpperInvariant(),
            [TransactionColumn.BranchName] = t => t.BranchName?.ToUpperInvariant(),
            [TransactionColumn.DataSource] = t => t.DataSource?.ToUpperInvariant(),
            [TransactionColumn.Debit] = t => t.Debit,
            [TransactionColumn.Credit] = t => t.Credit,
            [TransactionColumn.DownPaymentAmount] = t => t.DownPaymentAmount,
            [TransactionColumn.RunningBalance] = t => t.RunningBalance,
        };
        var covered = new[] { TransactionColumn.ValueDate, TransactionColumn.CustomerName, TransactionColumn.BankName,
                              TransactionColumn.Debit, TransactionColumn.Credit, TransactionColumn.RunningBalance };

        foreach (var (column, key) in keys.Where(k => covered.Contains(k.Key)))
            foreach (var descending in new[] { false, true })
            {
                var sort = new TransactionSort(column, descending);
                var ordered = descending ? seed.Rows.OrderByDescending(key) : seed.Rows.OrderBy(key);
                // Ties break on the key in the same direction as the sort.
                var expected = (descending
                        ? ordered.ThenByDescending(r => r.PayOrderNo.ToUpperInvariant(), StringComparer.Ordinal).ThenByDescending(r => r.TxnRefSeqNo.ToUpperInvariant(), StringComparer.Ordinal)
                        : ordered.ThenBy(r => r.PayOrderNo.ToUpperInvariant(), StringComparer.Ordinal).ThenBy(r => r.TxnRefSeqNo.ToUpperInvariant(), StringComparer.Ordinal))
                    .Select(r => r.PayOrderNo).ToArray();

                var got = new List<string>();
                for (var page = 1; page <= 8; page++)
                    got.AddRange((await svc.GetPageAsync(f, sort, page, 4)).Select(r => r.PayOrderNo));

                Assert.True(expected.SequenceEqual(got), $"{column} {(descending ? "descending" : "ascending")}: paged order differs from the whole-result order.");
                Assert.Equal(30, got.Distinct().Count());
            }
    }

    [SkippableFact]
    public async Task An_undefined_sort_column_is_rejected()
    {
        await using var env = NewEnv();
        var svc = QuerySeed.Service(env);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            svc.GetPageAsync(TransactionFilter.Empty, new TransactionSort((TransactionColumn)999, false), 1, 10));
    }

    // ---- totals ----------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Totals_cover_the_whole_filtered_set_equal_a_direct_decimal_sum_and_are_zero_when_nothing_matches()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var p = env.Prefix;

        var filter = In(seed) with { BankNames = new[] { $"{p} HBL" }, CreditMin = 200m };
        var expected = seed.Rows.Where(r => r.BankName == $"{p} HBL" && r.Credit >= 200m).ToList();

        var page = await svc.SearchAsync(filter, TransactionSort.Default, 1, 2);   // page holds just 2 rows
        var totals = await svc.GetTotalsAsync(filter);
        Assert.Equal(expected.Count, page.TotalCount);
        Assert.Equal(expected.Sum(r => r.Credit ?? 0), totals.Credit);
        Assert.Equal(expected.Sum(r => r.Debit ?? 0), totals.Debit);
        Assert.True(totals.Credit > page.Rows.Sum(r => r.Credit ?? 0));

        Assert.Equal(expected.Sum(r => r.DownPaymentAmount ?? 0), totals.DownPayment);

        // The whole batch, against a plain decimal sum of the seeded values (the money columns are summed in SQL, not in memory).
        var all = await svc.GetTotalsAsync(In(seed));
        Assert.Equal(seed.Rows.Sum(r => r.Credit ?? 0), all.Credit);
        Assert.Equal(seed.Rows.Sum(r => r.Debit ?? 0), all.Debit);
        Assert.Equal(seed.Rows.Sum(r => r.DownPaymentAmount ?? 0), all.DownPayment);

        var none = await svc.GetTotalsAsync(In(seed) with { BankNames = new[] { "Nobody Bank" } });
        Assert.Equal(new TransactionTotals(0m, 0m, 0m), none);
    }

    // ---- dropdown values -------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Distinct_values_are_sorted_unique_and_skip_blanks()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        // Rows with blank / empty values must not show up as an empty choice.
        var blanks = QuerySeed.Standard(env.Prefix, seed.BatchId, 99);
        blanks.BankName = "";
        blanks.Project = null;
        await QuerySeed.InsertAsync(env, new[] { blanks });
        var svc = QuerySeed.Service(env);
        var p = env.Prefix;

        var banks = await svc.GetDistinctValuesAsync(DistinctField.BankName);
        Assert.Equal(new[] { $"{p} HBL", $"{p} Meezan", $"{p} UBL" }, banks.Where(b => b.StartsWith(p)).ToArray());
        Assert.DoesNotContain("", banks);
        Assert.Equal(banks.Count, banks.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(banks.OrderBy(b => b, StringComparer.Ordinal).Count(), banks.Count);

        Assert.Equal(new[] { $"{p} Alpha", $"{p} Beta" }, (await svc.GetDistinctValuesAsync(DistinctField.Project)).Where(v => v.StartsWith(p)));
        Assert.Equal(4, (await svc.GetDistinctValuesAsync(DistinctField.DealerName)).Count(v => v.StartsWith(p)));
        Assert.Equal(5, (await svc.GetDistinctValuesAsync(DistinctField.BranchName)).Count(v => v.StartsWith(p)));
        Assert.Equal(2, (await svc.GetDistinctValuesAsync(DistinctField.DataSource)).Count(v => v.StartsWith(p)));
    }

    [SkippableFact]
    public async Task Distinct_values_are_cached_until_invalidated_or_expired()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env, 6);
        var clock = new ManualClock();
        var svc = QuerySeed.Create(env, clock: clock, distinctCacheLifetime: TimeSpan.FromMinutes(10));
        var p = env.Prefix;

        var first = await svc.GetDistinctValuesAsync(DistinctField.BankName);
        Assert.Same(first, await svc.GetDistinctValuesAsync(DistinctField.BankName));        // served from the cache

        var late = QuerySeed.Standard(p, seed.BatchId, 77);
        late.BankName = $"{p} Zeta Bank";
        await QuerySeed.InsertAsync(env, new[] { late });

        Assert.DoesNotContain($"{p} Zeta Bank", await svc.GetDistinctValuesAsync(DistinctField.BankName));   // still the old list

        svc.InvalidateDistinctValues();                                                       // an import committed
        Assert.Contains($"{p} Zeta Bank", await svc.GetDistinctValuesAsync(DistinctField.BankName));

        // Expiry: another user's import never invalidates this process, so the cache also times out.
        await QuerySeed.InsertAsync(env, new[] { Moved(QuerySeed.Standard(p, seed.BatchId, 78), $"{p} Omega Bank") });
        Assert.DoesNotContain($"{p} Omega Bank", await svc.GetDistinctValuesAsync(DistinctField.BankName));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Contains($"{p} Omega Bank", await svc.GetDistinctValuesAsync(DistinctField.BankName));

        static Transaction Moved(Transaction t, string bank)
        {
            t.BankName = bank;
            return t;
        }
    }

    [SkippableFact]
    public async Task A_caller_that_gives_up_does_not_poison_the_cached_list()
    {
        await using var env = NewEnv();
        var svc = QuerySeed.Service(env);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // SQL Server loads asynchronously, so the caller sees the cancellation; SQLite may already have the list. Either is fine:
        // what matters is that the shared load is not poisoned for the next caller.
        try { await svc.GetDistinctValuesAsync(DistinctField.BankName, cts.Token); }
        catch (OperationCanceledException) { }
        var again = await svc.GetDistinctValuesAsync(DistinctField.BankName);     // the next caller still gets the list
        Assert.NotNull(again);
    }

    // ---- details ---------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Detail_returns_all_28_fields_and_the_import_info()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env, 12);
        var svc = QuerySeed.Service(env);
        var src = seed.Rows[8];                                                        // i = 9

        var detail = await svc.GetDetailAsync(new TransactionKey(src.PayOrderNo, src.TxnRefSeqNo));
        Assert.NotNull(detail);
        var r = detail!.Record;

        Assert.Equal(src.PayOrderNo, r.PayOrderNo);
        Assert.Equal(src.TxnRefSeqNo, r.TxnRefSeqNo);
        Assert.Equal(src.ValueDate, r.ValueDate);
        Assert.Equal(src.BranchCode, r.BranchCode);
        Assert.Equal(src.BranchName, r.BranchName);
        Assert.Equal(src.RegNo, r.RegNo);
        Assert.Equal(src.ApplicationNo, r.ApplicationNo);
        Assert.Equal(src.PlotNo, r.PlotNo);
        Assert.Equal(src.StreetNo, r.StreetNo);
        Assert.Equal(src.ChallanNo, r.ChallanNo);
        Assert.Equal(src.ChequeInstNo, r.ChequeInstNo);
        Assert.Equal(src.CustomerName, r.CustomerName);
        Assert.Equal(src.Cnic, r.Cnic);
        Assert.Equal(src.Discount, r.Discount);
        Assert.Equal(src.DownPaymentAmount, r.DownPaymentAmount);
        Assert.Equal(src.Debit, r.Debit);
        Assert.Equal(src.Credit, r.Credit);
        Assert.Equal(src.RunningBalance, r.RunningBalance);
        Assert.Equal(src.BankName, r.BankName);
        Assert.Equal(src.AccountNo, r.AccountNo);
        Assert.Equal(src.Project, r.Project);
        Assert.Equal(src.DealerName, r.DealerName);
        Assert.Equal(src.DataSource, r.DataSource);
        Assert.Equal(src.Narration1, r.Narration1);
        Assert.Equal(src.Narration2, r.Narration2);
        Assert.Equal(src.Narration3, r.Narration3);       // i = 9 is divisible by 3, so this one is set
        Assert.Equal(src.Narration4, r.Narration4);
        Assert.Equal(src.Narration5, r.Narration5);
        Assert.NotNull(r.Narration3);

        var import = detail.Import;
        Assert.Equal(seed.BatchId, import.ImportBatchId);
        Assert.Equal(env.Prefix + ".xlsx", import.FileName);
        Assert.Equal("Sheet1", import.SheetName);
        Assert.Equal("TEST\\user", import.ImportedBy);
        Assert.Equal(10, import.SourceRowNumber);
        Assert.True(import.ImportedAt > DateTime.UtcNow.AddDays(-1) && import.ImportedAt < DateTime.UtcNow.AddDays(1));
        Assert.Null(import.UpdatedAt);

        Assert.Null(await svc.GetDetailAsync(new TransactionKey(env.Prefix + "-nope", "none")));
    }

    // ---- safety, cancellation, timeout -----------------------------------------------------------------

    [SkippableFact]
    public async Task Cancelled_token_stops_every_operation()
    {
        await using var env = NewEnv();
        var svc = QuerySeed.Service(env);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var f = TransactionFilter.Empty;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.SearchAsync(f, TransactionSort.Default, 1, 10, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.GetPageAsync(f, TransactionSort.Default, 1, 10, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.CountAsync(f, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.GetTotalsAsync(f, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.GetDetailAsync(new TransactionKey("a", "b"), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.ExportAsync(f, ExportFormat.Csv, Path.Combine(Path.GetTempPath(), "never.csv"), null, cts.Token));
    }

    // ---- filter object ---------------------------------------------------------------------------------

    // ---- export ----------------------------------------------------------------------------------------

    [SkippableFact]
    public async Task Stream_chunks_walks_the_result_in_key_order_without_gaps_or_repeats()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);

        var sizes = new List<int>();
        var keys = new List<string>();
        await foreach (var chunk in svc.StreamChunksAsync(In(seed), 4))
        {
            sizes.Add(chunk.Count);
            keys.AddRange(chunk.Select(r => r.PayOrderNo));
        }
        Assert.Equal(new[] { 4, 4, 4, 4, 4, 4, 4, 2 }, sizes);
        Assert.Equal(seed.Rows.Select(r => r.PayOrderNo).OrderBy(x => x, StringComparer.OrdinalIgnoreCase), keys);

        // A filtered stream, and a stream that ends exactly on a chunk boundary (no empty trailing chunk is yielded).
        var filtered = new List<int>();
        await foreach (var chunk in svc.StreamChunksAsync(In(seed) with { ApplicationNo = TextCriterion.StartsWith("APP-02") }, 5))
            filtered.Add(chunk.Count);
        Assert.Equal(new[] { 5, 5 }, filtered);                // APP-020..APP-029 = 10 rows
    }

    [SkippableFact]
    public async Task Export_xlsx_writes_a_header_and_every_matching_row_with_real_types()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var path = Path.Combine(Path.GetTempPath(), env.Prefix + ".xlsx");
        try
        {
            var filter = In(seed) with { BankNames = new[] { $"{env.Prefix} HBL" } };
            var expected = seed.Rows.Where(r => r.BankName == $"{env.Prefix} HBL").OrderBy(r => r.PayOrderNo, StringComparer.OrdinalIgnoreCase).ToList();

            var reported = new List<long>();
            var result = await svc.ExportAsync(filter, ExportFormat.Xlsx, path, new Progress<long>(reported.Add));

            Assert.True(result.Success);
            Assert.Equal(expected.Count, result.RowsWritten);
            Assert.Equal(path, result.DestinationPath);
            Assert.False(File.Exists(path + ".part"));

            using var wb = new ClosedXML.Excel.XLWorkbook(path);
            var ws = wb.Worksheet(1);
            Assert.Equal("Pay Order No", ws.Cell(1, 1).GetString());
            Assert.Equal("Narration 5", ws.Cell(1, 28).GetString());
            Assert.Equal(expected.Count + 1, ws.LastRowUsed()!.RowNumber());

            var first = expected[0];
            Assert.Equal(first.PayOrderNo, ws.Cell(2, 1).GetString());
            Assert.Equal(first.ValueDate.ToDateTime(TimeOnly.MinValue), ws.Cell(2, 3).GetDateTime());
            Assert.Equal(first.CustomerName, ws.Cell(2, 12).GetString());
            Assert.Equal(first.Cnic, ws.Cell(2, 13).GetString());                      // stays text
            Assert.Equal((double)first.Debit!.Value, ws.Cell(2, 16).GetDouble());      // stays a number
            Assert.Equal(first.Credit is null, ws.Cell(2, 17).IsEmpty());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public async Task Export_csv_quotes_fields_and_keeps_text_exactly()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var path = Path.Combine(Path.GetTempPath(), env.Prefix + ".csv");
        try
        {
            var result = await svc.ExportAsync(In(seed), ExportFormat.Csv, path);
            Assert.True(result.Success);
            Assert.Equal(30, result.RowsWritten);
            Assert.False(File.Exists(path + ".part"));

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);                  // UTF-8 byte-order mark for Excel
            var records = ParseCsv(System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

            Assert.Equal(31, records.Count);
            Assert.All(records, rec => Assert.Equal(28, rec.Count));                    // quoting kept every record at 28 fields
            Assert.Equal("Pay Order No", records[0][0]);

            var awkward = records.Single(r => r[0] == seed.Rows[7].PayOrderNo);          // i = 8: comma, quotes, line break
            Assert.Equal("Khan, \"Ali\"\nJr", awkward[11].Replace("\r\n", "\n"));
            var plain = records.Single(r => r[0] == seed.Rows[0].PayOrderNo);
            Assert.Equal("2024-01-01", plain[2]);
            Assert.Equal("4210000000001", plain[12]);
            Assert.Equal("290.00", plain[15]);                                          // Debit = (30-1)*10
            Assert.Equal("100.00", plain[16]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public async Task Export_to_xlsx_is_refused_above_the_sheet_limit_but_csv_is_not()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Create(env, maxExcelDataRows: 10);   // pretend a sheet holds 10 rows
        var xlsx = Path.Combine(Path.GetTempPath(), env.Prefix + "-big.xlsx");
        var csv = Path.Combine(Path.GetTempPath(), env.Prefix + "-big.csv");
        try
        {
            var refused = await svc.ExportAsync(In(seed), ExportFormat.Xlsx, xlsx);
            Assert.False(refused.Success);
            Assert.Contains("CSV", refused.Message);
            Assert.Contains("10", refused.Message);
            Assert.False(File.Exists(xlsx));
            Assert.False(File.Exists(xlsx + ".part"));

            var ok = await svc.ExportAsync(In(seed), ExportFormat.Csv, csv);
            Assert.True(ok.Success);
            Assert.Equal(30, ok.RowsWritten);

            // Exactly at the limit is fine.
            var atLimit = await svc.ExportAsync(In(seed) with { ApplicationNo = TextCriterion.StartsWith("APP-02") }, ExportFormat.Xlsx, xlsx);
            Assert.True(atLimit.Success);
            Assert.Equal(10, atLimit.RowsWritten);
        }
        finally
        {
            File.Delete(xlsx);
            File.Delete(csv);
        }
    }

    [SkippableFact]
    public async Task A_cancelled_export_leaves_no_file_behind()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var path = Path.Combine(Path.GetTempPath(), env.Prefix + "-cancel.csv");
        using var cts = new CancellationTokenSource();
        try
        {
            // Cancel from inside the stream, after the first chunk has been written.
            var export = TransactionExportWriter.WriteAsync(CancelAfterFirst(svc.StreamChunksAsync(In(seed), 5, cts.Token), cts),
                ExportFormat.Csv, path, null, cts.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".part"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".part");
        }

        static async IAsyncEnumerable<IReadOnlyList<TransactionRecord>> CancelAfterFirst(
            IAsyncEnumerable<IReadOnlyList<TransactionRecord>> source, CancellationTokenSource cts)
        {
            await foreach (var chunk in source)
            {
                yield return chunk;
                cts.Cancel();
            }
        }
    }

    [SkippableFact]
    public async Task Export_with_columns_writes_only_those_for_only_the_matching_rows()
    {
        await using var env = NewEnv();
        var seed = await QuerySeed.SeedStandardAsync(env);
        var svc = QuerySeed.Service(env);
        var path = Path.Combine(Path.GetTempPath(), env.Prefix + "-cols.xlsx");
        try
        {
            var filter = In(seed) with { CustomerName = "khan" };
            var expected = seed.Rows.Where(r => r.CustomerName!.Contains("khan", Ci)).OrderBy(r => r.PayOrderNo, StringComparer.OrdinalIgnoreCase).ToList();

            var result = await svc.ExportAsync(filter, ExportFormat.Xlsx, path, null, default, new[] { "PayOrderNo", "CustomerName", "Credit" });

            Assert.True(result.Success);
            Assert.Equal(expected.Count, result.RowsWritten);
            using var wb = new ClosedXML.Excel.XLWorkbook(path);
            var ws = wb.Worksheet(1);
            Assert.Equal(3, ws.LastColumnUsed()!.ColumnNumber());
            Assert.Equal(expected.Count + 1, ws.LastRowUsed()!.RowNumber());
            Assert.Equal(expected[0].PayOrderNo, ws.Cell(2, 1).GetString());

            await Assert.ThrowsAsync<ArgumentException>(() => svc.ExportAsync(filter, ExportFormat.Xlsx, path, null, default, new string[0]));
            await Assert.ThrowsAsync<ArgumentException>(() => svc.ExportAsync(filter, ExportFormat.Csv, path + ".csv", null, default, new[] { "Nope" }));
            Assert.False(File.Exists(path + ".csv"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Minimal RFC 4180 reader: quoted fields may hold commas, doubled quotes and line breaks.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); records.Add(row); row = new List<string>(); }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); records.Add(row); }
        return records;
    }
}
