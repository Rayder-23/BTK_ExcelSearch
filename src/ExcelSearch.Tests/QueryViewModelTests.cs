using CommunityToolkit.Mvvm.Messaging;
using ExcelSearch.Core.Query;
using ExcelSearch.Services;
using ExcelSearch.ViewModels;

namespace ExcelSearch.Tests;

/// <summary>An in-memory ITransactionQueryService the tests can steer: slice a data list, or hold a call open until released.</summary>
internal sealed class FakeQueryService : ITransactionQueryService
{
    public List<TransactionRecord> Data { get; set; } = Enumerable.Range(1, 250).Select(i => Rec(i)).ToList();

    public Func<TransactionFilter, TransactionSort, int, int, CancellationToken, Task<IReadOnlyList<TransactionRecord>>>? OnPage { get; set; }
    public Func<TransactionFilter, CancellationToken, Task<int>>? OnCount { get; set; }
    public Func<TransactionFilter, CancellationToken, Task<TransactionTotals>>? OnTotals { get; set; }
    public Func<TransactionKey, CancellationToken, Task<TransactionDetail?>>? OnDetail { get; set; }
    public Func<DistinctField, Task<IReadOnlyList<string>>>? OnDistinct { get; set; }
    public Func<TransactionFilter, ExportFormat, string, IProgress<long>?, CancellationToken, Task<QueryExportResult>>? OnExport { get; set; }

    public List<(TransactionFilter Filter, TransactionSort Sort, int Page, int Size)> PageCalls { get; } = new();
    public List<TransactionFilter> CountCalls { get; } = new();
    public List<TransactionFilter> TotalsCalls { get; } = new();
    public List<(TransactionFilter Filter, ExportFormat Format, string Path)> ExportCalls { get; } = new();
    public List<IReadOnlyList<string>?> ExportColumnCalls { get; } = new();
    public int Invalidations { get; private set; }
    public int DistinctCalls { get; private set; }

    public static TransactionRecord Rec(int i, string? bank = "HBL") => new(
        $"PO{i:0000}", $"TX{i:0000}", new DateOnly(2024, 1, 1).AddDays(i % 28), "BC1", "Branch", "REG" + i, "APP" + i, null, null,
        "CH" + i, "CQ" + i, "Customer " + i, "4210000000" + (i % 1000).ToString("000"), null, i % 5 == 0 ? 500m : null, 10m * i, 20m * i, null,
        bank, "ACC" + i, "Project", "Dealer", "Source", "n1", null, null, null, null);

    public Task<TransactionPage> SearchAsync(TransactionFilter filter, TransactionSort sort, int pageNumber, int pageSize, CancellationToken ct = default) =>
        throw new NotSupportedException("The screen never calls SearchAsync: it asks for the page, count and totals separately.");

    public Task<IReadOnlyList<TransactionRecord>> GetPageAsync(TransactionFilter filter, TransactionSort sort, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        lock (PageCalls) PageCalls.Add((filter, sort, pageNumber, pageSize));
        if (OnPage is not null) return OnPage(filter, sort, pageNumber, pageSize, ct);
        IReadOnlyList<TransactionRecord> page = Data.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(page);
    }

    public Task<int> CountAsync(TransactionFilter filter, CancellationToken ct = default)
    {
        lock (CountCalls) CountCalls.Add(filter);
        return OnCount is not null ? OnCount(filter, ct) : Task.FromResult(Data.Count);
    }

    public Task<TransactionTotals> GetTotalsAsync(TransactionFilter filter, CancellationToken ct = default)
    {
        lock (TotalsCalls) TotalsCalls.Add(filter);
        return OnTotals is not null
            ? OnTotals(filter, ct)
            : Task.FromResult(new TransactionTotals(Data.Sum(d => d.Credit ?? 0), Data.Sum(d => d.Debit ?? 0), Data.Sum(d => d.DownPaymentAmount ?? 0)));
    }

    public Task<IReadOnlyList<string>> GetDistinctValuesAsync(DistinctField field, CancellationToken ct = default)
    {
        DistinctCalls++;
        return OnDistinct is not null ? OnDistinct(field) : Task.FromResult<IReadOnlyList<string>>(new[] { field + " B", field + " A" });
    }

    public void InvalidateDistinctValues() => Invalidations++;

    public Task<TransactionDetail?> GetDetailAsync(TransactionKey key, CancellationToken ct = default) =>
        OnDetail is not null
            ? OnDetail(key, ct)
            : Task.FromResult<TransactionDetail?>(new TransactionDetail(Data.First(d => d.Key == key),
                new ImportProvenance(7, "bank.xlsx", "Sheet1", new DateTime(2024, 5, 1, 9, 30, 0), "PC\\user", 12, new DateTime(2024, 5, 1), null)));

    public Task<QueryExportResult> ExportAsync(TransactionFilter filter, ExportFormat format, string destinationPath, IProgress<long>? progress = null, CancellationToken ct = default, IReadOnlyList<string>? columns = null)
    {
        ExportCalls.Add((filter, format, destinationPath));
        ExportColumnCalls.Add(columns);
        return OnExport is not null
            ? OnExport(filter, format, destinationPath, progress, ct)
            : Task.FromResult(new QueryExportResult(true, Data.Count, format, destinationPath, null));
    }
}

public class QueryViewModelTests
{
    static QueryViewModelTests()
    {
        // Messages use the current culture ("1,048,575", "1,000.50"); pin it so the tests read the same on any PC.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = new System.Globalization.CultureInfo("en-US");
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
    }

    private static (QueryViewModel Vm, FakeQueryService Svc, FakeDialogs Dialogs, IMessenger Messenger) Make(Action<FakeQueryService>? setup = null)
    {
        var svc = new FakeQueryService();
        setup?.Invoke(svc);
        var dialogs = new FakeDialogs();
        var messenger = new WeakReferenceMessenger();
        return (new QueryViewModel(svc, dialogs, null, messenger), svc, dialogs, messenger);
    }

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for: " + because);
            await Task.Delay(5);
        }
    }

    // ---- search flow -----------------------------------------------------------------------------------

    [Fact]
    public async Task Before_the_first_search_the_screen_shows_a_prompt_and_nothing_else()
    {
        var (vm, svc, _, _) = Make();
        Assert.True(vm.ShowPrompt);
        Assert.False(vm.HasSearched);
        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.TotalText);
        Assert.Empty(svc.PageCalls);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.NextPageCommand.CanExecute(null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Search_shows_page_one_then_the_count_and_the_totals()
    {
        var (vm, svc, _, _) = Make();

        await vm.SearchAsync();

        Assert.True(vm.HasSearched);
        Assert.False(vm.ShowPrompt);
        Assert.False(vm.IsBusy);
        Assert.Equal(100, vm.Rows.Count);
        Assert.Equal(250, vm.TotalCount);
        Assert.Equal("250 rows", vm.TotalText);
        Assert.Equal("Page 1 of 3", vm.PageText);
        Assert.Equal(3, vm.PageCount);
        Assert.Equal(svc.Data.Sum(d => d.Credit), decimal.Parse(vm.CreditTotalText, System.Globalization.NumberStyles.Number));
        Assert.False(vm.IsTotalsLoading);
        Assert.False(vm.IsEmptyResult);
        Assert.Single(svc.PageCalls);
        Assert.Equal((1, 100), (svc.PageCalls[0].Page, svc.PageCalls[0].Size));
        Assert.Equal(TransactionSort.Default, svc.PageCalls[0].Sort);
    }

    [Fact]
    public async Task Rows_appear_before_the_count_and_totals_arrive()
    {
        var count = new TaskCompletionSource<int>();
        var totals = new TaskCompletionSource<TransactionTotals>();
        var (vm, _, _, _) = Make(s => { s.OnCount = (_, _) => count.Task; s.OnTotals = (_, _) => totals.Task; });

        var search = vm.SearchAsync();
        await WaitUntil(() => vm.Rows.Count == 100, "the first page to be shown");

        Assert.False(vm.IsBusy);                       // the busy indicator is gone: the grid is usable
        Assert.Null(vm.TotalCount);
        Assert.True(vm.IsTotalsLoading);
        Assert.Equal("counting…", vm.TotalText);
        Assert.Equal("…", vm.CreditTotalText);
        Assert.False(vm.ExportCommand.CanExecute(null));   // not until the count is known
        Assert.True(vm.NextPageCommand.CanExecute(null));  // a full page means there is probably more

        count.SetResult(250);
        totals.SetResult(new TransactionTotals(1234.5m, 10m, 0m));
        await search;

        Assert.Equal(250, vm.TotalCount);
        Assert.False(vm.IsTotalsLoading);
        Assert.Equal(1234.5m.ToString("N2"), vm.CreditTotalText);
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Starting_a_new_search_cancels_the_running_one_and_ignores_its_answer()
    {
        var first = new TaskCompletionSource<IReadOnlyList<TransactionRecord>>();
        CancellationToken firstToken = default;
        var calls = 0;
        var (vm, _, _, _) = Make(s => s.OnPage = (_, _, _, _, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstToken = ct;
                return first.Task;                           // the slow search
            }
            return Task.FromResult<IReadOnlyList<TransactionRecord>>(new[] { FakeQueryService.Rec(99) });   // the new, quick one
        });

        vm.CustomerName = "slow";
        var slow = vm.SearchAsync();
        await WaitUntil(() => calls == 1, "the first search to start");

        vm.CustomerName = "quick";
        await vm.SearchAsync();

        Assert.True(firstToken.IsCancellationRequested);     // the old search was told to stop
        Assert.Equal("PO0099", vm.Rows.Single().PayOrderNo);

        // Even if the old one still answers (it ignored the token), its rows must not replace the new ones.
        first.SetResult(new[] { FakeQueryService.Rec(1), FakeQueryService.Rec(2) });
        await slow;
        Assert.Equal("PO0099", vm.Rows.Single().PayOrderNo);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Cancel_stops_the_search_and_says_so()
    {
        var page = new TaskCompletionSource<IReadOnlyList<TransactionRecord>>();
        CancellationToken token = default;
        var (vm, _, _, _) = Make(s => s.OnPage = (_, _, _, _, ct) => { token = ct; return page.Task; });

        var search = vm.SearchAsync();
        await WaitUntil(() => vm.IsBusy, "busy");
        vm.CancelSearchCommand.Execute(null);

        Assert.True(token.IsCancellationRequested);
        Assert.False(vm.IsBusy);
        Assert.Equal("Search cancelled.", vm.Message);
        Assert.False(vm.HasSearched);                  // nothing was ever shown: back to the prompt

        page.SetResult(new[] { FakeQueryService.Rec(1) });   // a late answer is ignored
        await search;
        Assert.Empty(vm.Rows);
    }

    // ---- filters ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_customer_name_toggle_switches_between_contains_and_starts_with_and_shows_in_the_chip()
    {
        var (vm, svc, _, _) = Make();
        await vm.OnShownAsync();
        vm.CustomerName = "khan";

        await vm.SearchAsync();                                  // default: contains
        Assert.Equal(TextMatchMode.Contains, svc.PageCalls.Last().Filter.CustomerNameMode);
        Assert.Contains(vm.ActiveFilters, c => c.Text == "Customer contains: khan");

        vm.CustomerNameStartsWith = true;
        await vm.SearchAsync();
        Assert.Equal(TextMatchMode.StartsWith, svc.PageCalls.Last().Filter.CustomerNameMode);
        Assert.Contains(vm.ActiveFilters, c => c.Text == "Customer starts with: khan");

        vm.ClearCommand.Execute(null);                           // clearing puts the toggle back to the default
        Assert.False(vm.CustomerNameStartsWith);
    }

    [Fact]
    public async Task The_typed_filters_are_turned_into_a_normalized_filter()
    {
        var (vm, svc, _, _) = Make();
        await vm.OnShownAsync();

        vm.ValueDateFrom = new DateTime(2024, 3, 1, 14, 30, 0);
        vm.ValueDateTo = new DateTime(2024, 3, 31);
        vm.CustomerName = "  khan ";
        vm.Cnic.Text = "35202-1234567-1";
        vm.Cnic.Exact = true;
        vm.ApplicationNo.Text = " APP-1 ";
        vm.PayOrderNo.Text = "PO";
        vm.TxnRefSeqNo.Text = "TX";
        vm.RegNo.Text = "R";
        vm.ChallanNo.Text = "CH";
        vm.ChequeInstNo.Text = "CQ";
        vm.AccountNo.Text = "AC";
        vm.Banks.TickSecond();
        vm.CreditMin = "1,000.50";
        vm.CreditMax = "9000";
        vm.DebitMin = "1";
        vm.DownPaymentMax = "75000";
        vm.ImportBatchText = " 42 ";

        await vm.SearchAsync();

        var f = svc.PageCalls.Single().Filter;
        Assert.Equal(new DateOnly(2024, 3, 1), f.ValueDateFrom);          // the time of day is dropped
        Assert.Equal(new DateOnly(2024, 3, 31), f.ValueDateTo);
        Assert.Equal("khan", f.CustomerName);
        Assert.Equal(new TextCriterion("3520212345671", TextMatchMode.Exact), f.Cnic);
        Assert.Equal(new TextCriterion("APP-1", TextMatchMode.StartsWith), f.ApplicationNo);   // starts-with is the default
        Assert.Equal("PO", f.PayOrderNo.Value);
        Assert.Equal("TX", f.TxnRefSeqNo.Value);
        Assert.Equal("R", f.RegNo.Value);
        Assert.Equal("CH", f.ChallanNo.Value);
        Assert.Equal("CQ", f.ChequeInstNo.Value);
        Assert.Equal("AC", f.AccountNo.Value);
        Assert.Equal(new[] { "BankName B" }, f.BankNames);
        Assert.Equal(1000.50m, f.CreditMin);
        Assert.Equal(9000m, f.CreditMax);
        Assert.Equal(1m, f.DebitMin);
        Assert.Null(f.DebitMax);
        Assert.Equal(75000m, f.DownPaymentMax);
        Assert.Equal(42, f.ImportBatchId);
        // The same filter goes to the count and the totals.
        Assert.Equal(f, svc.CountCalls.Single());
        Assert.Equal(f, svc.TotalsCalls.Single());
    }

    [Theory]
    [InlineData("from-after-to", "The \"from\" date is after the \"to\" date.")]
    [InlineData("credit-text", "Credit minimum is not a valid amount.")]
    [InlineData("debit-range", "Debit: the minimum is larger than the maximum.")]
    [InlineData("batch-text", "Import batch must be a whole number")]
    [InlineData("batch-zero", "Import batch must be a whole number")]
    public async Task Invalid_filters_are_explained_and_do_not_search(string which, string expected)
    {
        var (vm, svc, _, _) = Make();
        switch (which)
        {
            case "from-after-to": vm.ValueDateFrom = new DateTime(2024, 5, 2); vm.ValueDateTo = new DateTime(2024, 5, 1); break;
            case "credit-text": vm.CreditMin = "abc"; break;
            case "debit-range": vm.DebitMin = "10"; vm.DebitMax = "5"; break;
            case "batch-text": vm.ImportBatchText = "x1"; break;
            case "batch-zero": vm.ImportBatchText = "0"; break;
        }

        await vm.SearchAsync();

        Assert.True(vm.HasFilterError);
        Assert.StartsWith(expected, vm.FilterError);
        Assert.Empty(svc.PageCalls);
        Assert.False(vm.HasSearched);

        // Fixing the problem lets the next search through and clears the message.
        vm.ValueDateFrom = null; vm.ValueDateTo = null; vm.CreditMin = ""; vm.DebitMin = ""; vm.DebitMax = ""; vm.ImportBatchText = "";
        await vm.SearchAsync();
        Assert.False(vm.HasFilterError);
        Assert.Single(svc.PageCalls);
    }

    [Fact]
    public async Task Clear_resets_the_filters_and_the_results_but_keeps_sort_and_page_size()
    {
        var (vm, svc, _, _) = Make();
        await vm.OnShownAsync();
        vm.CustomerName = "x";
        vm.Cnic.Text = "123";
        vm.Cnic.Exact = true;
        vm.Banks.TickSecond();
        vm.CreditMin = "5";
        vm.ImportBatchText = "3";
        vm.ValueDateFrom = new DateTime(2024, 1, 1);
        vm.SelectedPageSize = 200;
        await vm.SearchAsync();
        vm.SortBy(nameof(TransactionColumn.Credit));
        await vm.LastLoad;

        vm.ClearCommand.Execute(null);

        Assert.Equal("", vm.CustomerName);
        Assert.Equal("", vm.Cnic.Text);
        Assert.False(vm.Cnic.Exact);
        Assert.Empty(vm.Banks.SelectedValues);
        Assert.Equal("", vm.CreditMin);
        Assert.Equal("", vm.ImportBatchText);
        Assert.Null(vm.ValueDateFrom);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasSearched);
        Assert.True(vm.ShowPrompt);
        Assert.Null(vm.TotalCount);
        Assert.Null(vm.Totals);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(200, vm.SelectedPageSize);
        Assert.Equal(TransactionColumn.Credit, vm.Sort.Column);
    }

    [Fact]
    public async Task Clear_cancels_a_search_that_is_still_running()
    {
        var page = new TaskCompletionSource<IReadOnlyList<TransactionRecord>>();
        CancellationToken token = default;
        var (vm, _, _, _) = Make(s => s.OnPage = (_, _, _, _, ct) => { token = ct; return page.Task; });

        var search = vm.SearchAsync();
        await WaitUntil(() => vm.IsBusy, "busy");
        vm.ClearCommand.Execute(null);
        page.SetResult(new[] { FakeQueryService.Rec(1) });
        await search;

        Assert.True(token.IsCancellationRequested);
        Assert.Empty(vm.Rows);
        Assert.False(vm.IsBusy);
    }

    // ---- paging, sorting, page size --------------------------------------------------------------------

    [Fact]
    public async Task Next_and_previous_move_between_pages_without_recounting()
    {
        var (vm, svc, _, _) = Make();
        await vm.SearchAsync();
        Assert.False(vm.PreviousPageCommand.CanExecute(null));

        vm.NextPageCommand.Execute(null);
        await vm.LastLoad;
        Assert.Equal(2, vm.PageNumber);
        Assert.Equal("Page 2 of 3", vm.PageText);
        Assert.Equal("PO0101", vm.Rows[0].PayOrderNo);

        vm.NextPageCommand.Execute(null);
        await vm.LastLoad;
        Assert.Equal(50, vm.Rows.Count);                    // the last page is short
        Assert.False(vm.NextPageCommand.CanExecute(null));

        vm.PreviousPageCommand.Execute(null);
        await vm.LastLoad;
        Assert.Equal(2, vm.PageNumber);

        Assert.Equal(4, svc.PageCalls.Count);
        Assert.Single(svc.CountCalls);                      // the count and totals were asked for once, not per page
        Assert.Single(svc.TotalsCalls);
    }

    [Fact]
    public async Task Changing_the_page_size_goes_back_to_page_one()
    {
        var (vm, svc, _, _) = Make();
        await vm.SearchAsync();
        vm.NextPageCommand.Execute(null);
        await vm.LastLoad;

        vm.SelectedPageSize = 50;
        await vm.LastLoad;

        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(50, vm.Rows.Count);
        Assert.Equal("Page 1 of 5", vm.PageText);
        Assert.Equal(50, svc.PageCalls[^1].Size);
        Assert.Single(svc.CountCalls);
        Assert.Equal(new[] { 50, 100, 200, 500 }, QueryViewModel.PageSizes);
        Assert.Equal(100, new QueryViewModel(svc, new FakeDialogs(), null, new WeakReferenceMessenger()).SelectedPageSize);   // default
    }

    [Fact]
    public async Task Clicking_a_header_sorts_on_the_server_and_a_second_click_flips_the_direction()
    {
        var (vm, svc, _, _) = Make();
        await vm.SearchAsync();
        vm.NextPageCommand.Execute(null);
        await vm.LastLoad;

        vm.SortBy(nameof(TransactionColumn.CustomerName));
        await vm.LastLoad;
        Assert.Equal(new TransactionSort(TransactionColumn.CustomerName, false), vm.Sort);
        Assert.Equal(new TransactionSort(TransactionColumn.CustomerName, false), svc.PageCalls[^1].Sort);
        Assert.Equal(1, svc.PageCalls[^1].Page);             // a new order starts at the first page
        Assert.Equal(1, vm.PageNumber);

        vm.SortBy(nameof(TransactionColumn.CustomerName));
        await vm.LastLoad;
        Assert.Equal(new TransactionSort(TransactionColumn.CustomerName, true), svc.PageCalls[^1].Sort);

        vm.SortBy(nameof(TransactionColumn.Debit));
        await vm.LastLoad;
        Assert.Equal(new TransactionSort(TransactionColumn.Debit, false), svc.PageCalls[^1].Sort);   // a different column starts ascending

        Assert.Single(svc.CountCalls);                       // sorting does not change the count
    }

    [Theory]
    [InlineData("Nonsense")]
    [InlineData("1; DROP TABLE x")]
    [InlineData("999")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Only_whitelisted_sort_names_are_accepted(string? name)
    {
        var (vm, svc, _, _) = Make();
        await vm.SearchAsync();
        var before = svc.PageCalls.Count;

        vm.SortBy(name);

        Assert.Equal(TransactionSort.Default, vm.Sort);
        Assert.Equal(before, svc.PageCalls.Count);
    }

    [Fact]
    public void Every_grid_column_sorts_by_a_whitelisted_column_and_binds_to_a_real_property()
    {
        var (vm, _, _, _) = Make();
        Assert.Equal(28, vm.Columns.Count);                       // every one of the 28 fields can be shown and exported
        foreach (var c in vm.Columns)
        {
            if (c.Sort is { } sort) Assert.True(Enum.IsDefined(sort), c.Header);
            Assert.NotNull(typeof(TransactionRecord).GetProperty(c.Path));
        }
        Assert.Equal(28, vm.Columns.Select(c => c.Path).Distinct().Count());
        var sortable = vm.Columns.Where(c => c.Sort is not null).Select(c => c.Sort).ToArray();
        Assert.Equal(sortable.Length, sortable.Distinct().Count());
        Assert.Equal(19, sortable.Length);                        // exactly the TransactionColumn whitelist
        Assert.Equal(new[] { "Discount", "Branch code", "Plot no", "Street no", "Narration 1", "Narration 2", "Narration 3", "Narration 4", "Narration 5" },
            vm.Columns.Where(c => c.Sort is null).Select(c => c.Header));
    }

    // ---- empty and error states ------------------------------------------------------------------------

    [Fact]
    public async Task No_results_shows_the_empty_state_and_disables_export()
    {
        var (vm, _, _, _) = Make(s => s.Data = new List<TransactionRecord>());

        await vm.SearchAsync();

        Assert.True(vm.IsEmptyResult);
        Assert.Empty(vm.Rows);
        Assert.Equal(0, vm.TotalCount);
        Assert.Equal("0 rows", vm.TotalText);
        Assert.Contains("No transactions match", vm.EmptyText);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.HasMessage);                          // an empty result is not an error
    }

    [Fact]
    public async Task A_timed_out_search_gets_a_friendly_message_and_an_empty_grid()
    {
        var (vm, _, _, _) = Make(s => s.OnPage = (_, _, _, _, _) =>
            Task.FromException<IReadOnlyList<TransactionRecord>>(new QueryTimeoutException(new Exception("sql"))));

        await vm.SearchAsync();

        Assert.True(vm.HasMessage);
        Assert.Contains("longer than 60 seconds", vm.Message);
        Assert.Contains("Narrow it down", vm.Message);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsTotalsLoading);
        Assert.Empty(vm.Rows);
        Assert.False(vm.IsEmptyResult);                       // an error, not "no results"
    }

    [Fact]
    public async Task A_database_error_gets_a_friendly_message()
    {
        var (vm, _, _, _) = Make(s => s.OnPage = (_, _, _, _, _) =>
            Task.FromException<IReadOnlyList<TransactionRecord>>(new InvalidOperationException("boom", new FakeDbException("login failed"))));

        await vm.SearchAsync();

        Assert.Contains("database could not be reached", vm.Message);
        Assert.Contains("login failed", vm.Message);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_failing_count_keeps_the_rows_and_explains_the_missing_total()
    {
        var (vm, _, _, _) = Make(s => s.OnCount = (_, _) => Task.FromException<int>(new QueryTimeoutException(new Exception())));

        await vm.SearchAsync();

        Assert.Equal(100, vm.Rows.Count);                     // the rows are still shown
        Assert.Null(vm.TotalCount);
        Assert.True(vm.CountFailed);
        Assert.Equal("count unavailable", vm.TotalText);
        Assert.Contains("total is unavailable", vm.Message);
        Assert.True(vm.NextPageCommand.CanExecute(null));      // paging still works without a count
        Assert.False(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_page_that_has_become_empty_falls_back_and_refreshes_the_count()
    {
        // Three pages were counted, but by the time page 3 is asked for (another user deleted rows) only 120 remain.
        var (vm, svc, _, _) = Make();
        await vm.SearchAsync();
        vm.NextPageCommand.Execute(null);
        await vm.LastLoad;
        Assert.Equal(250, vm.TotalCount);

        svc.Data = svc.Data.Take(120).ToList();
        vm.NextPageCommand.Execute(null);                      // page 3: empty now
        await vm.LastLoad;
        await WaitUntil(() => vm.TotalCount == 120 && !vm.IsBusy, "the fallback to a real page");

        Assert.Equal(2, vm.PageNumber);
        Assert.Equal(20, vm.Rows.Count);
        Assert.Equal("Page 2 of 2", vm.PageText);
        Assert.False(vm.IsEmptyResult);
    }

    // ---- details panel ---------------------------------------------------------------------------------

    [Fact]
    public async Task Selecting_a_row_shows_all_fields_at_once_and_then_the_import_info()
    {
        var detail = new TaskCompletionSource<TransactionDetail?>();
        var (vm, svc, _, _) = Make(s => s.OnDetail = (_, _) => detail.Task);
        await vm.SearchAsync();

        vm.SelectedRow = vm.Rows[2];

        Assert.True(vm.HasSelection);
        var sections = vm.DetailSections;
        Assert.Equal(new[] { "Transaction", "Customer", "Amounts", "Bank", "Narrations", "Imported from" }, sections.Select(s => s.Title));
        Assert.Equal("loading…", sections[^1].Fields.Single().Value);
        Assert.Equal("PO0003", sections[0].Fields[0].Value);                          // the row's own data, immediately
        var allLabels = sections.SelectMany(s => s.Fields).Select(f => f.Label).ToList();
        Assert.Contains("Narration 5", allLabels);
        Assert.Contains("CNIC", allLabels);

        detail.SetResult(new TransactionDetail(svc.Data[2],
            new ImportProvenance(7, "bank.xlsx", "Sheet1", new DateTime(2024, 5, 1, 9, 30, 0), "PC\\user", 12, new DateTime(2024, 5, 1), null)));
        await vm.DetailLoad;

        var import = vm.DetailSections[^1].Fields.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("bank.xlsx", import["File"]);
        Assert.Equal("12", import["Row in file"]);
        Assert.Equal("PC\\user", import["Imported by"]);
        Assert.Equal("01/05/2024 09:30", import["Imported"]);
        Assert.Equal("7", import["Import batch"]);
        Assert.NotNull(vm.Detail);
    }

    [Fact]
    public async Task Selecting_another_row_cancels_the_first_details_load()
    {
        var first = new TaskCompletionSource<TransactionDetail?>();
        CancellationToken firstToken = default;
        var (vm, svc, _, _) = Make(s => s.OnDetail = (key, ct) =>
        {
            if (key.PayOrderNo == "PO0001") { firstToken = ct; return first.Task; }
            return Task.FromResult<TransactionDetail?>(new TransactionDetail(s.Data.First(d => d.Key == key),
                new ImportProvenance(9, "second.xlsx", null, DateTime.UtcNow, null, null, DateTime.UtcNow, null)));
        });
        await vm.SearchAsync();

        vm.SelectedRow = vm.Rows[0];
        vm.SelectedRow = vm.Rows[1];
        await vm.DetailLoad;
        Assert.True(firstToken.IsCancellationRequested);

        first.SetResult(new TransactionDetail(svc.Data[0], new ImportProvenance(1, "first.xlsx", null, DateTime.UtcNow, null, null, DateTime.UtcNow, null)));
        await Task.Delay(50);
        Assert.Equal("second.xlsx", vm.DetailSections[^1].Fields.First(f => f.Label == "File").Value);   // the late first answer was ignored
    }

    [Fact]
    public async Task A_failed_details_load_still_shows_the_row_data()
    {
        var (vm, _, _, _) = Make(s => s.OnDetail = (_, _) => Task.FromException<TransactionDetail?>(new InvalidOperationException("x")));
        await vm.SearchAsync();

        vm.SelectedRow = vm.Rows[0];
        await vm.DetailLoad;

        Assert.Equal("could not be loaded", vm.DetailSections[^1].Fields.Single().Value);
        Assert.Equal("PO0001", vm.DetailSections[0].Fields[0].Value);
    }

    // ---- export ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_writes_the_whole_result_in_the_chosen_format_and_offers_to_open_the_folder()
    {
        var (vm, svc, dialogs, _) = Make();
        await vm.SearchAsync();
        dialogs.ExportTargetToPick = new ExportTarget(@"C:\out\result.csv", ExportFormat.Csv);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Single(dialogs.ExportPicks);
        Assert.True(dialogs.ExportPicks[0].AllowExcel);
        Assert.StartsWith("Transactions_", dialogs.ExportPicks[0].Name);
        var call = svc.ExportCalls.Single();
        Assert.Equal(ExportFormat.Csv, call.Format);
        Assert.Equal(@"C:\out\result.csv", call.Path);
        Assert.True(call.Filter.IsEmpty);
        Assert.Contains("Exported 250 rows", vm.Message);
        Assert.Single(dialogs.ConfirmMessages);
        Assert.Equal(new[] { @"C:\out\result.csv" }, dialogs.OpenedFolders);       // FakeDialogs answers Yes
        Assert.False(vm.IsExporting);
    }

    [Fact]
    public async Task Export_uses_the_filter_of_the_results_on_screen_not_the_typed_one()
    {
        var (vm, svc, dialogs, _) = Make();
        vm.CustomerName = "searched";
        await vm.SearchAsync();
        vm.CustomerName = "typed later";
        dialogs.ExportTargetToPick = new ExportTarget(@"C:\o\a.xlsx", ExportFormat.Xlsx);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("searched", svc.ExportCalls.Single().Filter.CustomerName);
        Assert.Equal(ExportFormat.Xlsx, svc.ExportCalls.Single().Format);
    }

    [Fact]
    public async Task A_result_too_big_for_excel_offers_csv_only_and_says_why()
    {
        var (vm, svc, dialogs, _) = Make(s => s.OnCount = (_, _) => Task.FromResult(QueryLimits.MaxExcelDataRows + 1));
        await vm.SearchAsync();

        Assert.NotNull(vm.ExportHint);
        Assert.Contains("1,048,575", vm.ExportHint);
        Assert.Contains("CSV only", vm.ExportHint);

        dialogs.ExportTargetToPick = new ExportTarget(@"C:\o\big.csv", ExportFormat.Csv);
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.False(dialogs.ExportPicks.Single().AllowExcel);                       // the save dialog only lists CSV
        Assert.Contains(dialogs.Infos, i => i.Contains("1,048,575") && i.Contains("CSV"));   // and the user is told why
        Assert.Equal(ExportFormat.Csv, svc.ExportCalls.Single().Format);
    }

    [Fact]
    public async Task Excel_is_never_requested_for_a_too_big_result_even_if_the_dialog_returns_it()
    {
        var (vm, svc, dialogs, _) = Make(s => s.OnCount = (_, _) => Task.FromResult(QueryLimits.MaxExcelDataRows + 5));
        await vm.SearchAsync();
        dialogs.ExportTargetToPick = new ExportTarget(@"C:\o\sneaky.xlsx", ExportFormat.Xlsx);

        await vm.ExportCommand.ExecuteAsync(null);

        var call = svc.ExportCalls.Single();
        Assert.Equal(ExportFormat.Csv, call.Format);
        Assert.Equal(@"C:\o\sneaky.csv", call.Path);
    }

    [Fact]
    public async Task Exactly_the_sheet_limit_still_allows_excel()
    {
        var (vm, _, dialogs, _) = Make(s => s.OnCount = (_, _) => Task.FromResult(QueryLimits.MaxExcelDataRows));
        await vm.SearchAsync();
        Assert.Null(vm.ExportHint);
        dialogs.ExportTargetToPick = null;
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.True(dialogs.ExportPicks.Single().AllowExcel);
    }

    [Fact]
    public async Task Export_can_be_cancelled_while_it_runs()
    {
        var started = new TaskCompletionSource();
        var (vm, svc, dialogs, _) = Make(s => s.OnExport = async (_, f, path, progress, ct) =>
        {
            progress?.Report(10);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new QueryExportResult(true, 0, f, path, null);
        });
        await vm.SearchAsync();
        dialogs.ExportTargetToPick = new ExportTarget(@"C:\o\x.csv", ExportFormat.Csv);

        var export = vm.ExportCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsExporting);
        Assert.False(vm.ExportCommand.CanExecute(null));       // cannot start a second export meanwhile

        vm.ExportCancelCommand.Execute(null);
        await export;

        Assert.False(vm.IsExporting);
        Assert.Equal("Export cancelled. No file was written.", vm.Message);
        Assert.Empty(dialogs.OpenedFolders);
    }

    [Fact]
    public async Task A_refused_or_failed_export_shows_a_message_and_recovers()
    {
        var mode = "refuse";
        var (vm, _, dialogs, _) = Make(s => s.OnExport = (_, f, p, _, _) => mode == "refuse"
            ? Task.FromResult(new QueryExportResult(false, 0, f, null, "Too many rows."))
            : Task.FromException<QueryExportResult>(new System.IO.IOException("disk full")));
        await vm.SearchAsync();
        dialogs.ExportTargetToPick = new ExportTarget(@"C:\o\x.csv", ExportFormat.Csv);

        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal("Too many rows.", vm.Message);
        Assert.False(vm.IsExporting);

        mode = "fail";
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Contains("disk full", vm.Message);
        Assert.False(vm.IsExporting);
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    // ---- layout state: filter drawer, chips, columns ---------------------------------------------------

    [Fact]
    public async Task The_filter_drawer_starts_open_hides_after_a_search_and_reopens_on_clear()
    {
        var (vm, _, _, _) = Make();
        Assert.True(vm.IsFilterPanelOpen);
        Assert.True(vm.AutoHideFilters);

        await vm.SearchAsync();
        Assert.False(vm.IsFilterPanelOpen);                  // the results get the room

        vm.ToggleFilterPanelCommand.Execute(null);
        Assert.True(vm.IsFilterPanelOpen);
        vm.ToggleFilterPanelCommand.Execute(null);
        Assert.False(vm.IsFilterPanelOpen);

        vm.ClearCommand.Execute(null);
        Assert.True(vm.IsFilterPanelOpen);                   // ready for new filters
    }

    [Fact]
    public async Task Each_applied_filter_becomes_a_chip_and_the_button_counts_them()
    {
        var (vm, _, _, _) = Make();
        await vm.OnShownAsync();
        Assert.Equal("Filters", vm.FiltersButtonText);

        vm.ValueDateFrom = new DateTime(2024, 1, 1);
        vm.ValueDateTo = new DateTime(2024, 3, 31);
        vm.CustomerName = "khan";
        vm.Cnic.Text = "42101-1234567-1";
        vm.Cnic.Exact = true;
        vm.ApplicationNo.Text = "APP";
        vm.Banks.TickSecond();
        vm.CreditMin = "10000";
        vm.CreditMax = "20000";
        vm.DebitMin = "5";
        vm.ImportBatchText = "42";
        await vm.SearchAsync();

        var text = vm.ActiveFilters.Select(c => c.Text).ToArray();
        Assert.Equal(new[]
        {
            "Date: 01/01/2024 – 31/03/2024", "Customer contains: khan", "CNIC: 4210112345671 (exact)", "Application no: APP",
            "Bank: BankName B", "Credit: 10,000 – 20,000", "Debit ≥ 5", "Import batch: 42",
        }, text);
        Assert.True(vm.HasActiveFilters);
        Assert.Equal("Filters (8)", vm.FiltersButtonText);

        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.ActiveFilters);
        Assert.Equal("Filters", vm.FiltersButtonText);
    }

    [Fact]
    public async Task No_filters_means_no_chips_and_a_half_open_date_range_reads_naturally()
    {
        var (vm, _, _, _) = Make();
        await vm.SearchAsync();
        Assert.Empty(vm.ActiveFilters);

        vm.ValueDateTo = new DateTime(2024, 3, 31);
        await vm.SearchAsync();
        Assert.Equal("Date until 31/03/2024", vm.ActiveFilters.Single().Text);
    }

    [Fact]
    public async Task Removing_a_chip_clears_that_filter_only_and_searches_again()
    {
        var (vm, svc, _, _) = Make();
        vm.CustomerName = "khan";
        vm.Cnic.Text = "4210";
        await vm.SearchAsync();
        var customerChip = vm.ActiveFilters.Single(c => c.Text.StartsWith("Customer"));

        await vm.RemoveFilterCommand.ExecuteAsync(customerChip);

        Assert.Equal("", vm.CustomerName);
        Assert.Equal("4210", vm.Cnic.Text);                  // the other filter is untouched
        Assert.Equal(2, svc.PageCalls.Count);
        Assert.Null(svc.PageCalls[1].Filter.CustomerName);
        Assert.Equal("4210", svc.PageCalls[1].Filter.Cnic.Value);
        Assert.Equal(new[] { "CNIC: 4210" }, vm.ActiveFilters.Select(c => c.Text));
    }

    [Fact]
    public void Columns_start_with_the_everyday_ones_and_the_picker_lists_all_of_them()
    {
        var (vm, _, _, _) = Make();
        Assert.Equal(vm.Columns.Count, vm.ColumnChoices.Count);

        var shown = vm.ColumnChoices.Where(c => c.IsVisible).Select(c => c.Header).ToArray();
        Assert.Equal(new[]
        {
            "Value date", "Pay order no", "Txn ref / seq", "Customer", "CNIC", "Application no", "Bank", "Project", "Dealer",
            "Debit", "Credit", "Down payment",
        }, shown);
        Assert.Equal(16, vm.ColumnChoices.Count(c => !c.IsVisible));  // reg no, account, branch, challan, cheque, data source + the 10 rarely needed fields

        vm.ColumnChoices.Single(c => c.Header == "Account no").IsVisible = true;
        Assert.Contains("Account no", vm.ColumnChoices.Where(c => c.IsVisible).Select(c => c.Header));
    }

    // ---- export: selected columns (default) or all columns -----------------------------------------------

    [Fact]
    public async Task Export_defaults_to_the_selected_columns_in_grid_order()
    {
        var (vm, svc, dialogs, _) = Make();
        await vm.SearchAsync();
        dialogs.ExportTargetToPick = new ExportTarget(@"C:\o\a.xlsx", ExportFormat.Xlsx);
        Assert.Equal(QueryViewModel.SelectedColumnsScope, vm.ExportScope);

        vm.ColumnChoices.Single(c => c.Header == "Dealer").IsVisible = false;        // unticked in the picker
        vm.ColumnChoices.Single(c => c.Header == "Narration 1").IsVisible = true;    // a normally hidden field, ticked

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(new[]
        {
            "ValueDate", "PayOrderNo", "TxnRefSeqNo", "CustomerName", "Cnic", "ApplicationNo", "BankName", "Project",
            "Debit", "Credit", "DownPaymentAmount", "Narration1",
        }, svc.ExportColumnCalls.Single());
    }

    [Fact]
    public async Task Choosing_all_columns_exports_every_field_whatever_is_ticked()
    {
        var (vm, svc, dialogs, _) = Make();
        await vm.SearchAsync();
        dialogs.ExportTargetToPick = new ExportTarget(@"C:\o\a.csv", ExportFormat.Csv);
        foreach (var c in vm.ColumnChoices) c.IsVisible = false;                       // nothing ticked ...
        vm.ExportScope = QueryViewModel.AllColumnsScope;                                // ... but "All columns" is chosen

        Assert.True(vm.ExportCommand.CanExecute(null));
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Null(svc.ExportColumnCalls.Single());                                    // null = all 28 fields
        Assert.Equal(new[] { QueryViewModel.SelectedColumnsScope, QueryViewModel.AllColumnsScope }, QueryViewModel.ExportScopes);
    }

    [Fact]
    public async Task Export_of_selected_columns_needs_at_least_one_column()
    {
        var (vm, svc, _, _) = Make();
        await vm.SearchAsync();
        Assert.True(vm.ExportCommand.CanExecute(null));

        foreach (var c in vm.ColumnChoices) c.IsVisible = false;

        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.Equal("Tick at least one column to export.", vm.ExportHint);

        vm.ExportScope = QueryViewModel.AllColumnsScope;                                // switching to All makes it possible again
        Assert.True(vm.ExportCommand.CanExecute(null));
        Assert.Null(vm.ExportHint);

        vm.ExportScope = QueryViewModel.SelectedColumnsScope;
        vm.ColumnChoices[0].IsVisible = true;
        Assert.True(vm.ExportCommand.CanExecute(null));
        Assert.Empty(svc.ExportCalls);
    }

    // ---- dropdown lists --------------------------------------------------------------------------------

    [Fact]
    public async Task The_dropdown_lists_load_once_and_again_after_an_import_commits()
    {
        var (vm, svc, _, messenger) = Make();

        await vm.OnShownAsync();
        Assert.Equal(5, svc.DistinctCalls);
        Assert.Equal(new[] { "BankName A", "BankName B" }, vm.Banks.VisibleOptions.Select(o => o.Value));   // sorted
        Assert.True(vm.Banks.IsLoaded);

        await vm.OnShownAsync();                                // shown again: no new queries
        Assert.Equal(5, svc.DistinctCalls);

        messenger.Send(new ImportCommittedMessage(42));         // an import finished
        Assert.Equal(1, svc.Invalidations);                     // the service cache was dropped
        await vm.OnShownAsync();
        Assert.Equal(10, svc.DistinctCalls);
    }

    [Fact]
    public async Task A_failed_dropdown_load_does_not_block_searching_and_is_retried_next_time()
    {
        var fail = true;
        var (vm, svc, _, _) = Make(s => s.OnDistinct = f => fail
            ? Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("no list"))
            : Task.FromResult<IReadOnlyList<string>>(new[] { "A" }));

        await vm.OnShownAsync();
        Assert.Contains("could not be loaded", vm.Message);
        Assert.False(vm.Banks.IsLoaded);

        await vm.SearchAsync();                                  // searching still works
        Assert.Equal(100, vm.Rows.Count);

        fail = false;
        await vm.OnShownAsync();
        Assert.True(vm.Banks.IsLoaded);
    }

    [Fact]
    public async Task Ticked_values_survive_a_refresh_of_the_list()
    {
        var (vm, _, _, messenger) = Make();
        await vm.OnShownAsync();
        vm.Banks.TickSecond();   // ticks "BankName B"

        messenger.Send(new ImportCommittedMessage(1));
        await vm.OnShownAsync();

        Assert.Equal(new[] { "BankName B" }, vm.Banks.SelectedValues);
        Assert.Equal("BankName B", vm.Banks.Summary);
    }

    [Fact]
    public void The_save_dialog_follows_the_file_name_and_never_returns_excel_when_it_is_not_allowed()
    {
        var csv = WpfDialogServiceAccess.Resolve(@"C:\a\b.CSV", 1, true);
        Assert.Equal(ExportFormat.Csv, csv.Format);

        var xlsx = WpfDialogServiceAccess.Resolve(@"C:\a\b.xlsx", 2, true);        // typed .xlsx while the CSV filter was showing
        Assert.Equal(ExportFormat.Xlsx, xlsx.Format);

        var forced = WpfDialogServiceAccess.Resolve(@"C:\a\b.xlsx", 1, false);
        Assert.Equal(ExportFormat.Csv, forced.Format);
        Assert.Equal(@"C:\a\b.xlsx.csv", forced.Path);

        var noExt = WpfDialogServiceAccess.Resolve(@"C:\a\b", 1, true);
        Assert.Equal((@"C:\a\b.xlsx", ExportFormat.Xlsx), (noExt.Path, noExt.Format));
        var noExtCsv = WpfDialogServiceAccess.Resolve(@"C:\a\b", 2, true);
        Assert.Equal((@"C:\a\b.csv", ExportFormat.Csv), (noExtCsv.Path, noExtCsv.Format));
    }
}

internal sealed class FakeDbException : System.Data.Common.DbException
{
    public FakeDbException(string message) : base(message) { }
}

internal static class WpfDialogServiceAccess
{
    public static ExportTarget Resolve(string name, int filterIndex, bool allowExcel) =>
        ExcelSearch.Services.WpfDialogService.ResolveTarget(name, filterIndex, allowExcel);
}

internal static class QueryVmTestExtensions
{
    /// <summary>Ticks the second option of a loaded multi-select (the fake service lists "X A" and "X B").</summary>
    public static void TickSecond(this MultiSelectFilter box) => box.VisibleOptions[1].IsSelected = true;
}
