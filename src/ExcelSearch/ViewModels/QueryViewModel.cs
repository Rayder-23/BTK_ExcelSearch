using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ExcelSearch.Core.Query;
using ExcelSearch.Services;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.ViewModels;

/// <summary>One grid column on the Query screen. <see cref="Sort"/> is the whitelisted column a header click sorts by.</summary>
public sealed record QueryColumn(string Header, string Path, TransactionColumn? Sort, ColumnKind Kind = ColumnKind.Text,
    string? Format = null, double Width = 0, bool DefaultVisible = true);

/// <summary>One removable summary of an active filter ("Bank: HBL"). <see cref="Clear"/> empties that filter's input boxes.</summary>
public sealed record FilterChip(string Text, Action Clear);

/// <summary>One label/value pair in the details panel.</summary>
public sealed record DetailField(string Label, string Value, bool IsMono = false);

/// <summary>A titled group of details (Key, People, Amounts...).</summary>
public sealed record DetailSection(string Title, IReadOnlyList<DetailField> Fields);

/// <summary>
/// The Query screen's logic: filters, searching, server-side paging and sorting, totals, the details panel and export.
/// Like the import screen it only talks to an interface (ITransactionQueryService) and IDialogService, so it is tested
/// with fakes. Three rules shape it:
///  1. The grid shows the rows as soon as the page query returns; the count and totals arrive later on their own.
///  2. Starting a search cancels the one still running, and answers from a cancelled search are ignored (a "generation"
///     number is checked before any result is shown).
///  3. Paging and sorting use the filter as it was when Search was pressed, not whatever is typed in the boxes now.
/// </summary>
public sealed partial class QueryViewModel : ObservableObject
{
    private const string AppTitle = "ExcelSearch";

    private readonly ITransactionQueryService _service;
    private readonly IDialogService _dialogs;
    private readonly ILogger<QueryViewModel>? _logger;

    private CancellationTokenSource? _operation;     // the running search/page load
    private CancellationTokenSource? _detailLoad;     // the running details load
    private int _generation;                          // bumped whenever a new operation starts
    private TransactionFilter _active = TransactionFilter.Empty;   // the filter of the results on screen
    private bool _optionsStale = true;

    public QueryViewModel(ITransactionQueryService service, IDialogService dialogs, ILogger<QueryViewModel>? logger = null,
        IMessenger? messenger = null)
    {
        _service = service;
        _dialogs = dialogs;
        _logger = logger;

        ColumnChoices = Columns.Select(c => new ColumnChoice(c, c.DefaultVisible)).ToArray();
        foreach (var choice in ColumnChoices)
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ColumnChoice.IsVisible)) return;
                ExportCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ExportHint));
            };

        Cnic = new TextFilterInput("CNIC");
        ApplicationNo = new TextFilterInput("Application no");
        PayOrderNo = new TextFilterInput("Pay order no");
        TxnRefSeqNo = new TextFilterInput("Txn ref / seq no");
        SlipNo = new TextFilterInput("Slip no");
        RegNo = new TextFilterInput("Reg no");
        ChallanNo = new TextFilterInput("Challan no");
        ChequeInstNo = new TextFilterInput("Cheque / inst no");
        AccountNo = new TextFilterInput("Account no");

        Banks = new MultiSelectFilter("Bank", "Any bank");
        Projects = new MultiSelectFilter("Project", "Any project");
        Dealers = new MultiSelectFilter("Dealer", "Any dealer");
        Branches = new MultiSelectFilter("Branch", "Any branch");
        DataSources = new MultiSelectFilter("Data source", "Any source");

        // After any import commits, the lists of banks/projects/... may have new values.
        (messenger ?? WeakReferenceMessenger.Default).Register<QueryViewModel, ImportCommittedMessage>(this,
            static (self, _) => self.OnImportCommitted());
    }

    // ---- filter inputs -----------------------------------------------------------------------------------

    [ObservableProperty] private DateTime? _valueDateFrom;
    [ObservableProperty] private DateTime? _valueDateTo;
    [ObservableProperty] private string _customerName = "";

    /// <summary>Off (default) = the name contains what was typed; on = the name starts with it (can use the name index on SQL Server).</summary>
    [ObservableProperty] private bool _customerNameStartsWith;

    public TextFilterInput Cnic { get; }
    public TextFilterInput ApplicationNo { get; }
    public TextFilterInput PayOrderNo { get; }
    public TextFilterInput TxnRefSeqNo { get; }

    /// <summary>The slip number found in Narration 1 when the file was imported.</summary>
    public TextFilterInput SlipNo { get; }
    public TextFilterInput RegNo { get; }
    public TextFilterInput ChallanNo { get; }
    public TextFilterInput ChequeInstNo { get; }
    public TextFilterInput AccountNo { get; }

    public MultiSelectFilter Banks { get; }
    public MultiSelectFilter Projects { get; }
    public MultiSelectFilter Dealers { get; }
    public MultiSelectFilter Branches { get; }
    public MultiSelectFilter DataSources { get; }

    // Amount ranges and the batch are typed text; they are parsed (and checked) when Search is pressed.
    [ObservableProperty] private string _creditMin = "";
    [ObservableProperty] private string _creditMax = "";
    [ObservableProperty] private string _debitMin = "";
    [ObservableProperty] private string _debitMax = "";
    [ObservableProperty] private string _downPaymentMin = "";
    [ObservableProperty] private string _downPaymentMax = "";
    [ObservableProperty] private string _importBatchText = "";

    /// <summary>Why the filters cannot be searched yet (shown under the filter panel).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterError))]
    private string? _filterError;

    public bool HasFilterError => !string.IsNullOrEmpty(FilterError);

    // ---- layout state: filter drawer and active-filter chips ---------------------------------------------

    /// <summary>The filter drawer on the left. It starts open, and (by default) hides itself after a search so the results get the room.</summary>
    [ObservableProperty] private bool _isFilterPanelOpen = true;

    [ObservableProperty] private bool _autoHideFilters = true;

    /// <summary>One chip per filter that was applied to the results on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters), nameof(FiltersButtonText))]
    private IReadOnlyList<FilterChip> _activeFilters = Array.Empty<FilterChip>();

    public bool HasActiveFilters => ActiveFilters.Count > 0;

    public string FiltersButtonText => ActiveFilters.Count > 0 ? $"Filters ({ActiveFilters.Count})" : "Filters";

    [RelayCommand]
    private void ToggleFilterPanel() => IsFilterPanelOpen = !IsFilterPanelOpen;

    /// <summary>The ✕ on a chip: clears that filter's inputs and searches again.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RemoveFilterAsync(FilterChip? chip)
    {
        if (chip is null) return;
        chip.Clear();
        await SearchAsync().ConfigureAwait(true);
    }

    // ---- results state -----------------------------------------------------------------------------------

    public static IReadOnlyList<int> PageSizes { get; } = QueryLimits.PageSizes;

    public IReadOnlyList<QueryColumn> Columns { get; } = new QueryColumn[]
    {
        new("Value date", nameof(TransactionRecord.ValueDate), TransactionColumn.ValueDate, ColumnKind.Mono, "dd/MM/yyyy", 108),
        new("Slip no", nameof(TransactionRecord.SlipNo), TransactionColumn.SlipNo, ColumnKind.Mono, null, 100),
        // Bank statements have no pay order no / txn ref on most rows, so these two are hidden until ticked in the Columns picker.
        new("Pay order no", nameof(TransactionRecord.PayOrderNo), TransactionColumn.PayOrderNo, ColumnKind.Mono, null, 150, false),
        new("Txn ref / seq", nameof(TransactionRecord.TxnRefSeqNo), TransactionColumn.TxnRefSeqNo, ColumnKind.Mono, null, 120, false),
        new("Customer", nameof(TransactionRecord.CustomerName), TransactionColumn.CustomerName, ColumnKind.Text, null, 220),
        new("CNIC", nameof(TransactionRecord.Cnic), TransactionColumn.Cnic, ColumnKind.Mono, null, 130),
        new("Application no", nameof(TransactionRecord.ApplicationNo), TransactionColumn.ApplicationNo, ColumnKind.Mono, null, 130, false),
        new("Reg no", nameof(TransactionRecord.RegNo), TransactionColumn.RegNo, ColumnKind.Mono, null, 100, false),
        new("Bank", nameof(TransactionRecord.BankName), TransactionColumn.BankName, ColumnKind.Text, null, 150),
        new("Account no", nameof(TransactionRecord.AccountNo), TransactionColumn.AccountNo, ColumnKind.Mono, null, 140),
        new("Project", nameof(TransactionRecord.Project), TransactionColumn.Project, ColumnKind.Text, null, 140),
        new("Dealer", nameof(TransactionRecord.DealerName), TransactionColumn.DealerName, ColumnKind.Text, null, 150, false),
        new("Branch", nameof(TransactionRecord.BranchName), TransactionColumn.BranchName, ColumnKind.Text, null, 140, false),
        new("Debit", nameof(TransactionRecord.Debit), TransactionColumn.Debit, ColumnKind.Number, "N2", 118),
        new("Credit", nameof(TransactionRecord.Credit), TransactionColumn.Credit, ColumnKind.Number, "N2", 118),
        new("Down payment", nameof(TransactionRecord.DownPaymentAmount), TransactionColumn.DownPaymentAmount, ColumnKind.Number, "N2", 128, false),
        new("Challan no", nameof(TransactionRecord.ChallanNo), TransactionColumn.ChallanNo, ColumnKind.Mono, null, 110, false),
        new("Cheque / inst no", nameof(TransactionRecord.ChequeInstNo), TransactionColumn.ChequeInstNo, ColumnKind.Mono, null, 130, false),
        new("Data source", nameof(TransactionRecord.DataSource), TransactionColumn.DataSource, ColumnKind.Text, null, 130, false),

        // Hidden until ticked in the Columns picker (except the first two narrations), exportable like the rest. All sort on the server;
        // sorting by a narration has no index and reads the whole result, so on hundreds of thousands of rows it takes a moment.
        new("Running balance", nameof(TransactionRecord.RunningBalance), TransactionColumn.RunningBalance, ColumnKind.Number, "N2", 130, false),
        new("Discount", nameof(TransactionRecord.Discount), TransactionColumn.Discount, ColumnKind.Number, "N2", 100, false),
        new("Branch code", nameof(TransactionRecord.BranchCode), TransactionColumn.BranchCode, ColumnKind.Mono, null, 100, false),
        new("Plot no", nameof(TransactionRecord.PlotNo), TransactionColumn.PlotNo, ColumnKind.Mono, null, 90, false),
        new("Street no", nameof(TransactionRecord.StreetNo), TransactionColumn.StreetNo, ColumnKind.Mono, null, 90, false),
        new("Narration 1", nameof(TransactionRecord.Narration1), TransactionColumn.Narration1, ColumnKind.Text, null, 240),
        new("Narration 2", nameof(TransactionRecord.Narration2), TransactionColumn.Narration2, ColumnKind.Text, null, 240),
        new("Narration 3", nameof(TransactionRecord.Narration3), TransactionColumn.Narration3, ColumnKind.Text, null, 240, false),
        new("Narration 4", nameof(TransactionRecord.Narration4), TransactionColumn.Narration4, ColumnKind.Text, null, 240, false),
        new("Narration 5", nameof(TransactionRecord.Narration5), TransactionColumn.Narration5, ColumnKind.Text, null, 240, false),
        new("CNIC in narration", nameof(TransactionRecord.NarrationCnic), TransactionColumn.NarrationCnic, ColumnKind.Mono, null, 140, false),
    };

    /// <summary>Which columns are shown (the "Columns" picker). The defaults leave out the rarely needed ones.</summary>
    public IReadOnlyList<ColumnChoice> ColumnChoices { get; }

    /// <summary>Rows of the current page. Replaced as a whole (one change notification), not filled row by row.</summary>
    [ObservableProperty] private IReadOnlyList<TransactionRecord> _rows = Array.Empty<TransactionRecord>();

    /// <summary>True once Search has been pressed (until Clear).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPrompt))]
    private bool _hasSearched;

    /// <summary>The page query is running (the busy indicator over the grid).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPrompt))]
    private bool _isBusy;

    [ObservableProperty] private bool _isEmptyResult;

    public bool ShowPrompt => !HasSearched && !IsBusy;

    [ObservableProperty] private int _pageNumber = 1;

    [ObservableProperty] private int _selectedPageSize = QueryLimits.DefaultPageSize;

    /// <summary>Matching rows in all; null while the count is still running (or failed).</summary>
    [ObservableProperty] private int? _totalCount;

    [ObservableProperty] private bool _countFailed;

    [ObservableProperty] private TransactionTotals? _totals;
    [ObservableProperty] private bool _isTotalsLoading;
    [ObservableProperty] private bool _totalsFailed;

    [ObservableProperty] private TransactionSort _sort = TransactionSort.Default;

    public int PageCount => TotalCount is { } n ? Math.Max(1, (int)Math.Ceiling(n / (double)SelectedPageSize)) : 0;

    public string PageText => TotalCount is null
        ? $"Page {PageNumber:N0}"
        : $"Page {PageNumber:N0} of {PageCount:N0}";

    public string TotalText => !HasSearched ? "" :
        TotalCount is { } n ? $"{n:N0} {(n == 1 ? "row" : "rows")}" :
        CountFailed ? "count unavailable" : "counting…";

    public string CreditTotalText => TotalsText(Totals?.Credit);
    public string DebitTotalText => TotalsText(Totals?.Debit);
    public string DownPaymentTotalText => TotalsText(Totals?.DownPayment);

    private string TotalsText(decimal? value) =>
        value is { } v ? v.ToString("N2", CultureInfo.CurrentCulture) : TotalsFailed ? "n/a" : "…";

    // ---- messages ----------------------------------------------------------------------------------------

    /// <summary>Banner text (timeout, database error, export result...). Stays until dismissed; it never times out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    [RelayCommand]
    private void DismissMessage() => Message = null;

    /// <summary>The friendly text shown in the middle of the grid when a search finds nothing.</summary>
    public string EmptyText => "No transactions match these filters. Try removing one, or check the spelling.";

    // ---- searching ---------------------------------------------------------------------------------------

    /// <summary>
    /// Runs a new search from page 1 using what is typed in the filter boxes. Concurrent executions are allowed on
    /// purpose: pressing Search again cancels the running search instead of being ignored.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task SearchAsync()
    {
        if (!TryBuildFilter(out var filter)) return;
        FilterError = null;
        _active = filter;
        ActiveFilters = BuildChips(filter);
        if (AutoHideFilters) IsFilterPanelOpen = false;
        HasSearched = true;
        PageNumber = 1;
        await LoadAsync(requeryAggregates: true).ConfigureAwait(true);
    }

    /// <summary>Resets every filter and the results. The sort order and page size are kept.</summary>
    [RelayCommand]
    private void Clear()
    {
        BeginOperation();   // cancels whatever is running and invalidates its answers
        _active = TransactionFilter.Empty;

        ValueDateFrom = null;
        ValueDateTo = null;
        CustomerName = "";
        CustomerNameStartsWith = false;
        foreach (var t in new[] { Cnic, ApplicationNo, PayOrderNo, TxnRefSeqNo, SlipNo, RegNo, ChallanNo, ChequeInstNo, AccountNo }) t.Clear();
        foreach (var m in new[] { Banks, Projects, Dealers, Branches, DataSources }) m.Clear();
        CreditMin = CreditMax = DebitMin = DebitMax = DownPaymentMin = DownPaymentMax = ImportBatchText = "";
        FilterError = null;
        ActiveFilters = Array.Empty<FilterChip>();
        IsFilterPanelOpen = true;

        Rows = Array.Empty<TransactionRecord>();
        SelectedRow = null;
        HasSearched = false;
        IsBusy = false;
        IsEmptyResult = false;
        PageNumber = 1;
        TotalCount = null;
        CountFailed = false;
        Totals = null;
        IsTotalsLoading = false;
        TotalsFailed = false;
        Message = null;
        RaiseDerived();
    }

    /// <summary>Stops the running search (the Cancel link next to the busy indicator).</summary>
    [RelayCommand]
    private void CancelSearch()
    {
        if (!IsBusy) return;
        BeginOperation();
        IsBusy = false;
        IsTotalsLoading = false;
        if (Rows.Count == 0) HasSearched = false;
        Message = "Search cancelled.";
        RaiseDerived();
    }

    /// <summary>Turns the typed filters into a TransactionFilter, or says what is wrong with them.</summary>
    private bool TryBuildFilter(out TransactionFilter filter)
    {
        filter = TransactionFilter.Empty;

        if (ValueDateFrom is { } f && ValueDateTo is { } t && f.Date > t.Date)
            return Reject("The \"from\" date is after the \"to\" date.");

        if (!TryAmount(CreditMin, "Credit minimum", out var creditMin, out var error) ||
            !TryAmount(CreditMax, "Credit maximum", out var creditMax, out error) ||
            !TryAmount(DebitMin, "Debit minimum", out var debitMin, out error) ||
            !TryAmount(DebitMax, "Debit maximum", out var debitMax, out error) ||
            !TryAmount(DownPaymentMin, "Down payment minimum", out var downMin, out error) ||
            !TryAmount(DownPaymentMax, "Down payment maximum", out var downMax, out error))
            return Reject(error!);

        if (creditMin > creditMax) return Reject("Credit: the minimum is larger than the maximum.");
        if (debitMin > debitMax) return Reject("Debit: the minimum is larger than the maximum.");
        if (downMin > downMax) return Reject("Down payment: the minimum is larger than the maximum.");

        int? batch = null;
        if (!string.IsNullOrWhiteSpace(ImportBatchText))
        {
            if (!int.TryParse(ImportBatchText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var b) || b < 1)
                return Reject("Import batch must be a whole number (the batch number shown in the details panel).");
            batch = b;
        }

        filter = new TransactionFilter
        {
            ValueDateFrom = ValueDateFrom is { } df ? DateOnly.FromDateTime(df) : null,
            ValueDateTo = ValueDateTo is { } dt ? DateOnly.FromDateTime(dt) : null,
            Cnic = Cnic.Criterion,
            ApplicationNo = ApplicationNo.Criterion,
            RegNo = RegNo.Criterion,
            PayOrderNo = PayOrderNo.Criterion,
            TxnRefSeqNo = TxnRefSeqNo.Criterion,
            SlipNo = SlipNo.Criterion,
            ChallanNo = ChallanNo.Criterion,
            ChequeInstNo = ChequeInstNo.Criterion,
            AccountNo = AccountNo.Criterion,
            CustomerName = CustomerName,
            CustomerNameMode = CustomerNameStartsWith ? TextMatchMode.StartsWith : TextMatchMode.Contains,
            BankNames = Banks.SelectedValues,
            Projects = Projects.SelectedValues,
            DealerNames = Dealers.SelectedValues,
            BranchNames = Branches.SelectedValues,
            DataSources = DataSources.SelectedValues,
            CreditMin = creditMin,
            CreditMax = creditMax,
            DebitMin = debitMin,
            DebitMax = debitMax,
            DownPaymentMin = downMin,
            DownPaymentMax = downMax,
            ImportBatchId = batch,
        }.Normalized();
        return true;

        bool Reject(string why)
        {
            FilterError = why;
            return false;
        }
    }

    private static bool TryAmount(string text, string what, out decimal? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        // "1,250.50" is fine (thousands separators allowed); "abc" is not.
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var d))
        {
            value = d;
            return true;
        }
        error = $"{what} is not a valid amount.";
        return false;
    }

    // ---- chips -----------------------------------------------------------------------------------------

    private IReadOnlyList<FilterChip> BuildChips(TransactionFilter f)
    {
        var chips = new List<FilterChip>();
        static string Amount(decimal v) => v.ToString("#,0.##", CultureInfo.CurrentCulture);
        static string Day(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        if (f.ValueDateFrom is not null || f.ValueDateTo is not null)
            chips.Add(new FilterChip(
                f.ValueDateFrom is { } from && f.ValueDateTo is { } to ? $"Date: {Day(from)} – {Day(to)}"
                : f.ValueDateFrom is { } only ? $"Date from {Day(only)}" : $"Date until {Day(f.ValueDateTo!.Value)}",
                () => { ValueDateFrom = null; ValueDateTo = null; }));

        if (f.CustomerName is { } name)
            chips.Add(new FilterChip($"Customer {(f.CustomerNameMode == TextMatchMode.StartsWith ? "starts with" : "contains")}: {name}",
                () => { CustomerName = ""; CustomerNameStartsWith = false; }));

        void Text(TextFilterInput input, TextCriterion c)
        {
            if (c.IsEmpty) return;
            chips.Add(new FilterChip($"{input.Label}: {c.Value}{(c.Mode == TextMatchMode.Exact ? " (exact)" : "")}", input.Clear));
        }
        Text(Cnic, f.Cnic);
        Text(ApplicationNo, f.ApplicationNo);
        Text(PayOrderNo, f.PayOrderNo);
        Text(TxnRefSeqNo, f.TxnRefSeqNo);
        Text(SlipNo, f.SlipNo);
        Text(RegNo, f.RegNo);
        Text(ChallanNo, f.ChallanNo);
        Text(ChequeInstNo, f.ChequeInstNo);
        Text(AccountNo, f.AccountNo);

        void Pick(MultiSelectFilter box, IReadOnlyList<string> values)
        {
            if (values.Count == 0) return;
            chips.Add(new FilterChip($"{box.Label}: {(values.Count <= 2 ? string.Join(", ", values) : values.Count + " selected")}", box.Clear));
        }
        Pick(Banks, f.BankNames);
        Pick(Projects, f.Projects);
        Pick(Dealers, f.DealerNames);
        Pick(Branches, f.BranchNames);
        Pick(DataSources, f.DataSources);

        void Range(string label, decimal? min, decimal? max, Action clear)
        {
            if (min is null && max is null) return;
            chips.Add(new FilterChip(
                min is { } lo && max is { } hi ? $"{label}: {Amount(lo)} – {Amount(hi)}" : min is { } m ? $"{label} ≥ {Amount(m)}" : $"{label} ≤ {Amount(max!.Value)}",
                clear));
        }
        Range("Credit", f.CreditMin, f.CreditMax, () => { CreditMin = ""; CreditMax = ""; });
        Range("Debit", f.DebitMin, f.DebitMax, () => { DebitMin = ""; DebitMax = ""; });
        Range("Down payment", f.DownPaymentMin, f.DownPaymentMax, () => { DownPaymentMin = ""; DownPaymentMax = ""; });

        if (f.ImportBatchId is { } batch)
            chips.Add(new FilterChip($"Import batch: {batch}", () => ImportBatchText = ""));
        return chips;
    }

    // ---- loading a page (and the count/totals) -----------------------------------------------------------

    /// <summary>Cancels the running operation, starts a new generation and returns its token.</summary>
    private CancellationToken BeginOperation()
    {
        var old = _operation;
        _operation = new CancellationTokenSource();
        Interlocked.Increment(ref _generation);
        if (old is not null)
        {
            old.Cancel();
            old.Dispose();
        }
        return _operation.Token;
    }

    /// <summary>
    /// Fetches the current page with the active filter and sort. A new search (<paramref name="requeryAggregates"/>)
    /// also asks for the count and the totals, each on its own so the rows are not held up waiting for them.
    /// </summary>
    private Task LoadAsync(bool requeryAggregates) => LastLoad = LoadCoreAsync(requeryAggregates);

    /// <summary>The most recent page load (search, paging, sort or page-size change). Tests await it; the screen does not need to.</summary>
    public Task LastLoad { get; private set; } = Task.CompletedTask;

    private async Task LoadCoreAsync(bool requeryAggregates)
    {
        var ct = BeginOperation();
        var gen = _generation;
        var filter = _active;
        var sort = Sort;
        var page = PageNumber;
        var size = SelectedPageSize;

        IsBusy = true;
        IsEmptyResult = false;
        Message = null;
        if (requeryAggregates)
        {
            TotalCount = null;
            CountFailed = false;
            Totals = null;
            TotalsFailed = false;
            IsTotalsLoading = true;
        }
        RaiseDerived();

        Task<int>? countTask = null;
        Task<TransactionTotals>? totalsTask = null;
        try
        {
            var pageTask = _service.GetPageAsync(filter, sort, page, size, ct);
            if (requeryAggregates)
            {
                countTask = _service.CountAsync(filter, ct);
                totalsTask = _service.GetTotalsAsync(filter, ct);
            }

            var rows = await pageTask.ConfigureAwait(true);
            if (gen != _generation) return;

            // The page is past the end (another user removed rows since the count): step back a page and recount.
            if (rows.Count == 0 && page > 1)
            {
                Swallow(countTask);
                Swallow(totalsTask);
                PageNumber = page - 1;
                await LoadAsync(requeryAggregates: true).ConfigureAwait(true);
                return;
            }

            Rows = rows;
            SelectedRow = null;
            IsEmptyResult = rows.Count == 0 && page == 1;
            IsBusy = false;
            RaiseDerived();
        }
        catch (OperationCanceledException)
        {
            Swallow(countTask);
            Swallow(totalsTask);
            if (gen == _generation) IsBusy = false;   // cancelled by something other than a newer operation
            return;
        }
        catch (Exception ex)
        {
            Swallow(countTask);
            Swallow(totalsTask);
            if (gen != _generation) return;
            Fail(ex, "The search");
            return;
        }

        if (countTask is null || totalsTask is null) return;
        await Task.WhenAll(ApplyCountAsync(countTask, gen), ApplyTotalsAsync(totalsTask, gen)).ConfigureAwait(true);
    }

    private async Task ApplyCountAsync(Task<int> task, int gen)
    {
        try
        {
            var n = await task.ConfigureAwait(true);
            if (gen != _generation) return;
            TotalCount = n;
            IsEmptyResult = n == 0;
            RaiseDerived();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            _logger?.LogError(ex, "Counting the matching transactions failed.");
            CountFailed = true;
            Message = ex is QueryTimeoutException
                ? "Counting all the matching rows took too long, so the total is unavailable. The rows shown are still correct. Add a filter to narrow the search."
                : ErrorDescriber.Describe(ex);
            RaiseDerived();
        }
    }

    private async Task ApplyTotalsAsync(Task<TransactionTotals> task, int gen)
    {
        try
        {
            var totals = await task.ConfigureAwait(true);
            if (gen != _generation) return;
            Totals = totals;
            IsTotalsLoading = false;
            RaiseDerived();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            _logger?.LogError(ex, "Summing the matching transactions failed.");
            TotalsFailed = true;
            IsTotalsLoading = false;
            if (!HasMessage)
                Message = ex is QueryTimeoutException
                    ? "Adding up the totals took too long, so they are unavailable. The rows shown are still correct. Add a filter to narrow the search."
                    : ErrorDescriber.Describe(ex);
            RaiseDerived();
        }
    }

    /// <summary>Shows a failed page/search as a friendly message and leaves an empty grid.</summary>
    private void Fail(Exception ex, string what)
    {
        _logger?.LogError(ex, "{What} failed.", what);
        IsBusy = false;
        IsTotalsLoading = false;
        Rows = Array.Empty<TransactionRecord>();
        IsEmptyResult = false;
        Message = ex is QueryTimeoutException
            ? $"{what} took longer than {QueryLimits.CommandTimeoutSeconds} seconds and was stopped. " +
              "Narrow it down (a date range, a more exact reference number or a bank) and try again."
            : ErrorDescriber.Describe(ex);
        RaiseDerived();
    }

    /// <summary>Retrieving the exception of a task nobody awaits, so it is not reported as unobserved.</summary>
    private static void Swallow(Task? task) =>
        task?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    // ---- paging ------------------------------------------------------------------------------------------

    private bool CanGoPrevious() => HasSearched && PageNumber > 1 && !IsBusy;

    private bool CanGoNext() => HasSearched && !IsBusy &&
        (TotalCount is not null ? PageNumber < PageCount : Rows.Count >= SelectedPageSize);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousPageAsync()
    {
        PageNumber--;
        await LoadAsync(requeryAggregates: false).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextPageAsync()
    {
        PageNumber++;
        await LoadAsync(requeryAggregates: false).ConfigureAwait(true);
    }

    partial void OnSelectedPageSizeChanged(int value)
    {
        if (!HasSearched) return;
        // A different page size changes which rows are on page 1, but not the filter: no new count or totals needed.
        PageNumber = 1;
        _ = LoadAsync(requeryAggregates: false);
    }

    // ---- sorting -----------------------------------------------------------------------------------------

    /// <summary>
    /// A header was clicked. Only names from the <see cref="TransactionColumn"/> list are accepted. The same column twice
    /// flips the direction. The whole result is re-sorted on the server, then page 1 is shown.
    /// </summary>
    public void SortBy(string? columnName)
    {
        if (!Enum.TryParse<TransactionColumn>(columnName, out var column) || !Enum.IsDefined(column)) return;

        Sort = Sort.Column == column
            ? new TransactionSort(column, !Sort.Descending)
            : new TransactionSort(column, Descending: false);
        if (!HasSearched) return;
        PageNumber = 1;
        _ = LoadAsync(requeryAggregates: false);
    }

    // ---- details panel -----------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private TransactionRecord? _selectedRow;

    public bool HasSelection => SelectedRow is not null;

    /// <summary>All fields of the selected transaction, grouped. Shown at once from the row, then the import info is added.</summary>
    [ObservableProperty] private IReadOnlyList<DetailSection> _detailSections = Array.Empty<DetailSection>();

    /// <summary>The service's detail answer (all fields + import provenance); null until it arrives.</summary>
    [ObservableProperty] private TransactionDetail? _detail;

    /// <summary>Completes when the details of the current selection have been loaded (used by tests).</summary>
    public Task DetailLoad { get; private set; } = Task.CompletedTask;

    partial void OnSelectedRowChanged(TransactionRecord? value)
    {
        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
        _detailLoad = null;
        Detail = null;

        if (value is null)
        {
            DetailSections = Array.Empty<DetailSection>();
            DetailLoad = Task.CompletedTask;
            return;
        }

        DetailSections = DetailSectionBuilder.Build(value, null, importLoading: true);
        _detailLoad = new CancellationTokenSource();
        DetailLoad = LoadDetailAsync(value, _detailLoad.Token);
    }

    private async Task LoadDetailAsync(TransactionRecord row, CancellationToken ct)
    {
        try
        {
            var detail = await _service.GetDetailAsync(row.Key, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || SelectedRow != row) return;
            Detail = detail;
            DetailSections = DetailSectionBuilder.Build(detail?.Record ?? row, detail?.Import, importLoading: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || SelectedRow != row) return;
            _logger?.LogError(ex, "Loading the transaction details failed.");
            DetailSections = DetailSectionBuilder.Build(row, null, importLoading: false, importFailed: true);
        }
    }

    [RelayCommand]
    private void CloseDetails() => SelectedRow = null;

    // ---- export ------------------------------------------------------------------------------------------

    public const string SelectedColumnsScope = "Selected columns";
    public const string AllColumnsScope = "All columns";

    /// <summary>What "Export results" writes: the columns ticked in the picker (default) or all 28 fields.</summary>
    public static IReadOnlyList<string> ExportScopes { get; } = new[] { SelectedColumnsScope, AllColumnsScope };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _exportScope = SelectedColumnsScope;

    partial void OnExportScopeChanged(string value) => OnPropertyChanged(nameof(ExportHint));

    /// <summary>The property names an export writes, in grid order; null means all 28 fields.</summary>
    private IReadOnlyList<string>? ExportColumns() =>
        ExportScope == AllColumnsScope ? null : ColumnChoices.Where(c => c.IsVisible).Select(c => c.Column.Path).ToArray();

    /// <summary>The "All" link in the Columns picker.</summary>
    [RelayCommand]
    private void ShowAllColumns()
    {
        foreach (var c in ColumnChoices) c.IsVisible = true;
    }

    /// <summary>The "Default" link in the Columns picker: back to the everyday columns.</summary>
    [RelayCommand]
    private void ResetColumns()
    {
        foreach (var c in ColumnChoices) c.IsVisible = c.Column.DefaultVisible;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportProgressText))]
    private long _exportedRows;

    [ObservableProperty] private bool _isExporting;

    public string ExportProgressText => $"Exporting… {ExportedRows:N0} rows written";

    /// <summary>Set when the result is too large for an Excel sheet: tells the user why only CSV is offered.</summary>
    public string? ExportHint =>
        TotalCount is > QueryLimits.MaxExcelDataRows
            ? $"{TotalCount:N0} rows is more than an Excel sheet can hold ({QueryLimits.MaxExcelDataRows:N0}), so this export is CSV only."
            : ExportColumns() is { Count: 0 } ? "Tick at least one column to export." : null;

    private bool CanExport() => HasSearched && !IsBusy && !IsExporting && TotalCount is > 0 && ExportColumns() is not { Count: 0 };

    /// <summary>
    /// Saves every row of the current result (not just the page on screen). IncludeCancelCommand adds ExportCancelCommand
    /// for the Cancel button shown while it runs.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport), IncludeCancelCommand = true)]
    private async Task ExportAsync(CancellationToken ct)
    {
        if (TotalCount is not { } total || total == 0) return;

        var excelAllowed = total <= QueryLimits.MaxExcelDataRows;
        if (!excelAllowed)
            _dialogs.ShowInfo(AppTitle, ExportHint!);

        var target = _dialogs.PickExportPath($"Transactions_{DateTime.Now:yyyyMMdd_HHmm}", excelAllowed);
        if (target is null) return;
        // A dialog fake or a hand-typed name could still ask for Excel on a too-large result: refuse it here as well.
        if (!excelAllowed && target.Format == ExportFormat.Xlsx)
            target = target with { Format = ExportFormat.Csv, Path = Path.ChangeExtension(target.Path, ".csv") };

        Message = null;
        ExportedRows = 0;
        IsExporting = true;
        RaiseDerived();
        try
        {
            var progress = new Progress<long>(n => ExportedRows = n);
            var result = await _service.ExportAsync(_active, target.Format, target.Path, progress, ct, ExportColumns()).ConfigureAwait(true);

            if (!result.Success)
            {
                Message = result.Message ?? "The export could not be completed.";
                return;
            }

            Message = $"Exported {result.RowsWritten:N0} rows to {result.DestinationPath}." +
                      (result.Format == ExportFormat.Csv ? " " + ExportNotes.CsvLeadingZeros : "");
            if (_dialogs.Confirm(AppTitle, $"Exported {result.RowsWritten:N0} rows.\n\n{result.DestinationPath}\n\nOpen the folder?"))
                _dialogs.OpenFolder(result.DestinationPath!);
        }
        catch (OperationCanceledException)
        {
            Message = "Export cancelled. No file was written.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exporting the query results failed.");
            Message = ex is QueryTimeoutException
                ? "The export stopped because the database took too long to answer. Try again, or narrow the filters."
                : ErrorDescriber.Describe(ex);
        }
        finally
        {
            IsExporting = false;
            RaiseDerived();
        }
    }

    // ---- dropdown lists ----------------------------------------------------------------------------------

    private void OnImportCommitted()
    {
        _service.InvalidateDistinctValues();
        _optionsStale = true;
    }

    /// <summary>Called each time the Query screen is shown: loads the dropdown lists the first time and after an import.</summary>
    public async Task OnShownAsync()
    {
        if (!_optionsStale) return;
        _optionsStale = false;
        try
        {
            await Task.WhenAll(
                Fill(Banks, DistinctField.BankName),
                Fill(Projects, DistinctField.Project),
                Fill(Dealers, DistinctField.DealerName),
                Fill(Branches, DistinctField.BranchName),
                Fill(DataSources, DistinctField.DataSource)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _optionsStale = true;   // try again next time the screen is shown
            _logger?.LogError(ex, "Loading the filter lists failed.");
            Message = "The lists for the bank, project, dealer, branch and data-source filters could not be loaded. " +
                      "You can still search with the other filters.\n\n" + ErrorDescriber.Describe(ex);
        }
    }

    private async Task Fill(MultiSelectFilter filter, DistinctField field) =>
        filter.SetOptions(await _service.GetDistinctValuesAsync(field).ConfigureAwait(true));

    // ---- keeping the buttons in step ---------------------------------------------------------------------

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(CreditTotalText));
        OnPropertyChanged(nameof(DebitTotalText));
        OnPropertyChanged(nameof(DownPaymentTotalText));
        OnPropertyChanged(nameof(ExportHint));
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>Lays a transaction out as titled groups of label/value pairs for the details panel.</summary>
internal static class DetailSectionBuilder
{
    public static IReadOnlyList<DetailSection> Build(TransactionRecord r, ImportProvenance? import, bool importLoading,
        bool importFailed = false)
    {
        static string Text(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
        static string Money(decimal? d) => d is { } v ? v.ToString("N2", CultureInfo.CurrentCulture) : "—";

        var sections = new List<DetailSection>
        {
            new("Transaction", new DetailField[]
            {
                new("Pay order no", Text(r.PayOrderNo), true),
                new("Txn ref / seq no", Text(r.TxnRefSeqNo), true),
                new("Slip no", Text(r.SlipNo), true),
                new("Value date", r.ValueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), true),
                new("Data source", Text(r.DataSource)),
            }),
            new("Customer", new DetailField[]
            {
                new("Name", Text(r.CustomerName)),
                new("CNIC", Text(r.Cnic), true),
                new("CNIC in narration", Text(r.NarrationCnic), true),
                new("Application no", Text(r.ApplicationNo), true),
                new("Reg no", Text(r.RegNo), true),
                new("Plot no", Text(r.PlotNo), true),
                new("Street no", Text(r.StreetNo), true),
                new("Dealer", Text(r.DealerName)),
                new("Project", Text(r.Project)),
            }),
            new("Amounts", new DetailField[]
            {
                new("Debit", Money(r.Debit), true),
                new("Credit", Money(r.Credit), true),
                new("Down payment", Money(r.DownPaymentAmount), true),
                new("Discount", Money(r.Discount), true),
                new("Running balance", Money(r.RunningBalance), true),
            }),
            new("Bank", new DetailField[]
            {
                new("Bank", Text(r.BankName)),
                new("Account no", Text(r.AccountNo), true),
                new("Branch", Text(r.BranchName)),
                new("Branch code", Text(r.BranchCode), true),
                new("Challan no", Text(r.ChallanNo), true),
                new("Cheque / inst no", Text(r.ChequeInstNo), true),
            }),
            new("Narrations", new DetailField[]
            {
                new("Narration 1", Text(r.Narration1)),
                new("Narration 2", Text(r.Narration2)),
                new("Narration 3", Text(r.Narration3)),
                new("Narration 4", Text(r.Narration4)),
                new("Narration 5", Text(r.Narration5)),
            }),
        };

        if (import is not null)
        {
            sections.Add(new DetailSection("Imported from", new DetailField[]
            {
                new("File", import.FileName),
                new("Sheet", Text(import.SheetName)),
                new("Row in file", import.SourceRowNumber?.ToString(CultureInfo.InvariantCulture) ?? "—", true),
                new("Imported", import.ImportedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)),
                new("Imported by", Text(import.ImportedBy)),
                new("Import batch", import.ImportBatchId.ToString(CultureInfo.InvariantCulture), true),
                new("Last updated", import.UpdatedAt?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? "never"),
            }));
        }
        else if (importLoading || importFailed)
        {
            sections.Add(new DetailSection("Imported from", new DetailField[]
            {
                new("File", importLoading ? "loading…" : "could not be loaded"),
            }));
        }
        return sections;
    }
}
