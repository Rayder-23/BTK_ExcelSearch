using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExcelSearch.Core.Staging;

namespace ExcelSearch.ViewModels;

/// <summary>How a grid cell is drawn: text, monospaced (keys), monospaced right-aligned (amounts), an error message, or the Overwrite checkbox.</summary>
public enum ColumnKind { Text, Mono, Number, Error, Overwrite }

/// <summary>
/// One grid column: header text, the PreviewRow property path it shows, an optional format, and a width. <paramref name="Sort"/> is the
/// whitelisted server-side sort column (null: the column cannot be sorted, e.g. what the comparison with the database produces).
/// <paramref name="Optional"/> columns are listed in the Columns picker and shown when <paramref name="DefaultVisible"/> says so;
/// the others (row number, error, overwrite, ...) are always shown.
/// </summary>
public sealed record GridColumn(string Header, string Path, ColumnKind Kind = ColumnKind.Text, string? Format = null, double Width = 0,
    StagedColumn? Sort = null, bool Optional = false, bool DefaultVisible = true);

/// <summary>One entry of the preview's "Columns" picker: a grid column and whether it is currently shown.</summary>
public sealed partial class GridColumnChoice : ObservableObject
{
    public GridColumnChoice(GridColumn column)
    {
        Column = column;
        _isVisible = column.DefaultVisible;
    }

    public GridColumn Column { get; }
    public string Header => Column.Header;

    [ObservableProperty] private bool _isVisible;
}

/// <summary>
/// One grid row. Every tab shows the staged row; the Conflict tab also carries what differs from the database and the
/// editable Overwrite choice. Toggling <see cref="IsOverwrite"/> raises <see cref="OverwriteToggled"/> (the tab saves it).
/// </summary>
public sealed partial class PreviewRow : ObservableObject
{
    private bool _quiet;

    public PreviewRow(StagedRow row, ConflictRow? conflict = null)
    {
        Row = row;
        Conflict = conflict;
        _isOverwrite = row.Overwrite;
    }

    public StagedRow Row { get; }
    public ConflictRow? Conflict { get; }
    public bool IsConflict => Conflict is not null;

    /// <summary>Why a Possible-duplicate row was flagged (empty for other rows).</summary>
    public string MatchReasonText => Row.MatchReason switch
    {
        1 => "Same slip no., date, account and amounts",
        2 => "Same date, amount and branch (a hint)",
        3 => "Same Txn ref, other values differ",
        _ => "",
    };

    /// <summary>Bound two-way to the Overwrite checkbox.</summary>
    [ObservableProperty] private bool _isOverwrite;

    public event Action<PreviewRow, bool>? OverwriteToggled;

    partial void OnIsOverwriteChanged(bool value)
    {
        if (!_quiet) OverwriteToggled?.Invoke(this, value);
    }

    /// <summary>Puts the checkbox back (after a failed save) without triggering another save.</summary>
    public void SetWithoutNotify(bool value)
    {
        _quiet = true;
        IsOverwrite = value;
        _quiet = false;
    }
}

/// <summary>
/// One tab of the preview (one RowStatus): its rows for the current page, the paging state and Previous/Next.
/// Pages are fetched from the server on demand (100 rows), never the whole status at once. The Conflict tab also
/// saves each row's Overwrite choice immediately (so it survives paging) and has Overwrite all / Skip all.
/// </summary>
public partial class StatusTabViewModel : ObservableObject
{
    public const int PageSize = 100;

    private readonly IImportPreviewService _service;
    private readonly int _batchId;
    private readonly Action<Exception> _onError;
    private readonly Action<int>? _onOverwriteCountChanged;
    private int _loadVersion;   // lets a slow, older page load be ignored if a newer one was started

    public StatusTabViewModel(IImportPreviewService service, int batchId, RowStatus status, string title, int count,
        Action<Exception> onError, Action<int>? onOverwriteCountChanged = null)
    {
        _service = service;
        _batchId = batchId;
        _onError = onError;
        _onOverwriteCountChanged = onOverwriteCountChanged;
        Status = status;
        Title = title;
        Count = count;
        Columns = BuildColumns(status);
        ColumnChoices = Columns.Where(c => c.Optional).Select(c => new GridColumnChoice(c)).ToArray();
        Hint = HintFor(status);
        ConflictTotal = status == RowStatus.Conflict ? count : 0;
    }

    public RowStatus Status { get; }
    public string Title { get; }
    public int Count { get; }
    public IReadOnlyList<GridColumn> Columns { get; }

    /// <summary>The columns of the "Columns" picker (every optional column), in grid order.</summary>
    public IReadOnlyList<GridColumnChoice> ColumnChoices { get; }

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

    /// <summary>The order of the rows on screen (default: file order). Applies to the whole status, not just the page.</summary>
    [ObservableProperty] private StagedSort _sort;

    /// <summary>
    /// A header was clicked. Only names from the <see cref="StagedColumn"/> list that this tab shows as sortable are accepted.
    /// The same column twice flips the direction. The whole status is re-sorted on the server, then page 1 is shown.
    /// </summary>
    public void SortBy(string? columnName)
    {
        if (!Enum.TryParse<StagedColumn>(columnName, out var column) || !Enum.IsDefined(column)) return;
        if (!Columns.Any(c => c.Sort == column)) return;

        Sort = Sort.Column == column ? new StagedSort(column, !Sort.Descending) : new StagedSort(column, Descending: false);
        if (IsLoaded) _ = LoadPageAsync(1);
    }

    /// <summary>One sentence under the tab explaining what these rows are and what happens to them.</summary>
    public string Hint { get; }

    /// <summary>The Conflict tab shows a details panel (existing value vs file value) for the selected row.</summary>
    public bool ShowsDetails => Status == RowStatus.Conflict;

    /// <summary>The rows on screen. Replaced wholesale per page, so the DataGrid just re-binds.</summary>
    public ObservableCollection<PreviewRow> Rows { get; } = new();

    [ObservableProperty] private PreviewRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    private int _pageNumber = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalPages), nameof(PageText), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    private int _totalRows;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand), nameof(OverwriteAllCommand), nameof(SkipAllCommand))]
    private bool _isLoading;

    public bool IsLoaded { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalRows / (double)PageSize));
    public bool IsEmpty => IsLoaded && TotalRows == 0;
    public string PageText => $"Page {PageNumber:N0} of {TotalPages:N0}  ({TotalRows:N0} rows)";

    // ---- conflict overwrite choices ---------------------------------------------------------------------

    /// <summary>How many conflicts (of the whole batch) are set to overwrite, as last read from the database.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverwriteSummary))]
    private int _overwriteCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverwriteSummary))]
    private int _conflictTotal;

    public string OverwriteSummary => $"{OverwriteCount:N0} of {ConflictTotal:N0} conflicts will overwrite";

    /// <summary>The save started by the latest toggle or Overwrite all / Skip all (tests await it).</summary>
    public Task LastUpdate { get; private set; } = Task.CompletedTask;

    private bool CanBulkChange() => ShowsDetails && Count > 0 && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanBulkChange))]
    private Task OverwriteAllAsync() => LastUpdate = SetAllAsync(true);

    [RelayCommand(CanExecute = nameof(CanBulkChange))]
    private Task SkipAllAsync() => LastUpdate = SetAllAsync(false);

    private async Task SetAllAsync(bool overwrite)
    {
        try
        {
            // null row numbers = every Conflict row of the batch, not just this page.
            await _service.SetConflictOverwriteAsync(_batchId, overwrite, null);
            await LoadPageAsync(PageNumber);   // show the saved flags (and refreshes the count)
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }

    private void OnRowToggled(PreviewRow row, bool value) => LastUpdate = SaveToggleAsync(row, value);

    private async Task SaveToggleAsync(PreviewRow row, bool value)
    {
        try
        {
            await _service.SetConflictOverwriteAsync(_batchId, value, new[] { row.Row.ExcelRowNumber });
            await RefreshOverwriteCountAsync();
        }
        catch (Exception ex)
        {
            row.SetWithoutNotify(!value);   // the save failed: show the truth again
            _onError(ex);
        }
    }

    private async Task RefreshOverwriteCountAsync()
    {
        var counts = await _service.GetConflictCountsAsync(_batchId);
        OverwriteCount = counts.Overwrite;
        ConflictTotal = counts.Total;
        _onOverwriteCountChanged?.Invoke(counts.Overwrite);
    }

    // ---- paging -----------------------------------------------------------------------------------------

    /// <summary>Loads page 1 the first time the tab is shown (cheap to call repeatedly).</summary>
    public Task EnsureLoadedAsync() => IsLoaded || IsLoading ? Task.CompletedTask : LoadPageAsync(1);

    private bool CanGoPrevious() => !IsLoading && PageNumber > 1;
    private bool CanGoNext() => !IsLoading && PageNumber < TotalPages;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousPageAsync() => LoadPageAsync(PageNumber - 1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPageAsync() => LoadPageAsync(PageNumber + 1);

    private async Task LoadPageAsync(int page)
    {
        // A status with no rows never needs a round trip.
        if (Count == 0)
        {
            IsLoaded = true;
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            IReadOnlyList<PreviewRow> rows;
            int total;
            if (Status == RowStatus.Conflict)
            {
                // Conflicts come with the existing database row compared column by column, and each row's Overwrite flag.
                var result = await _service.GetConflictPageAsync(_batchId, page, PageSize, Sort);
                rows = result.Rows.Select(c => new PreviewRow(c.Incoming, c)).ToList();
                total = result.Total;
            }
            else
            {
                var result = await _service.GetPageAsync(_batchId, Status, page, PageSize, Sort);
                rows = result.Rows.Select(r => new PreviewRow(r)).ToList();
                total = result.Total;
            }
            if (version != _loadVersion) return;

            foreach (var old in Rows) old.OverwriteToggled -= OnRowToggled;
            Rows.Clear();
            foreach (var row in rows)
            {
                if (row.IsConflict) row.OverwriteToggled += OnRowToggled;
                Rows.Add(row);
            }
            IsLoaded = true;
            TotalRows = total;
            PageNumber = page;
            // Open the first conflict straight away so the user sees what a conflict looks like.
            SelectedRow = ShowsDetails ? Rows.FirstOrDefault() : null;
            OnPropertyChanged(nameof(IsEmpty));

            if (Status == RowStatus.Conflict) await RefreshOverwriteCountAsync();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    private static string HintFor(RowStatus status) => status switch
    {
        RowStatus.New => "These rows are not in the database yet. They will be imported.",
        RowStatus.Conflict => "These rows are already in the database (same Pay order no + Txn ref) but with different values. " +
                              "Select a row to see exactly what differs. Tick Overwrite to replace the database row with the file's values; " +
                              "conflicts you leave unticked are skipped.",
        RowStatus.Invalid => "These rows have problems and will not be imported. The Error column says what to fix; " +
                             "use Export invalid rows to fix them in Excel and import that file again.",
        RowStatus.ExactDuplicate => "These rows are already in the database with identical values. Rows that have a Pay order no + Txn ref are skipped. " +
                                    "Rows without them are flagged and imported anyway, unless you tick Skip flagged duplicates.",
        RowStatus.FileDuplicate => "These rows repeat an identical earlier row of this file. Rows that have a Pay order no + Txn ref are skipped. " +
                                   "Rows without them are flagged and imported anyway (the first copy is under New), unless you tick Skip flagged duplicates.",
        RowStatus.LikelyDuplicate => "These rows look like a row already in the database: the same Txn ref, the same slip number, or (cash rows) the same date, amount and branch, " +
                                     "while other values differ. The Why flagged column says which. It may be the same transaction or a different one, so they are flagged " +
                                     "and imported unless you tick Skip flagged duplicates.",
        _ => "",
    };

    // ExcelRowNumber first everywhere; ErrorText right after it on the Invalid tab. Slip numbers, keys and amounts are monospaced
    // so digits line up. Every field of the staged row can be shown through the Columns picker; the defaults are what identifies a
    // statement row: date, slip no, txn ref, customer, amounts, bank, branch code, narration 1 (most rows have no pay order no).
    // The Conflict tab defaults to the pay order no + txn ref, then the comparison with the database. Every column sorts on the
    // server except the two that come out of that comparison (Changes, What differs).
    private static IReadOnlyList<GridColumn> BuildColumns(RowStatus status)
    {
        var conflict = status == RowStatus.Conflict;
        var cols = new List<GridColumn>
        {
            new("Row", "Row.ExcelRowNumber", ColumnKind.Number, Width: 64, Sort: StagedColumn.ExcelRowNumber),
        };

        if (status == RowStatus.Invalid)
            cols.Add(new("Error", "Row.ErrorText", ColumnKind.Error, Width: 420, Sort: StagedColumn.ErrorText));

        if (conflict)
            cols.Add(new("Overwrite", nameof(PreviewRow.IsOverwrite), ColumnKind.Overwrite, Width: 112, Sort: StagedColumn.Overwrite));

        if (status == RowStatus.LikelyDuplicate)
            cols.Add(new("Why flagged", nameof(PreviewRow.MatchReasonText), ColumnKind.Text, Width: 250, Sort: StagedColumn.MatchReason));

        // A picker column; shown by default on every tab but the Conflict tab, which shows its own short list.
        GridColumn Pick(string header, string property, StagedColumn sort, ColumnKind kind, double width, bool everyday,
            bool conflictDefault = false, string? format = null) =>
            new(header, "Row." + property, kind, format, width, sort, Optional: true, DefaultVisible: conflict ? conflictDefault : everyday);

        cols.AddRange(new[]
        {
            Pick("Value date", nameof(StagedRow.ValueDate), StagedColumn.ValueDate, ColumnKind.Mono, 110, true, format: "dd-MMM-yyyy"),
            Pick("Slip no", nameof(StagedRow.SlipNo), StagedColumn.SlipNo, ColumnKind.Mono, 100, true),
            Pick("Pay order no", nameof(StagedRow.PayOrderNo), StagedColumn.PayOrderNo, ColumnKind.Mono, 150, false, true),
            Pick("Txn ref", nameof(StagedRow.TxnRefSeqNo), StagedColumn.TxnRefSeqNo, ColumnKind.Mono, 110, true, true),
            Pick("Customer", nameof(StagedRow.CustomerName), StagedColumn.CustomerName, ColumnKind.Text, 190, true),
            Pick("Debit", nameof(StagedRow.Debit), StagedColumn.Debit, ColumnKind.Number, 120, true, format: "N2"),
            Pick("Credit", nameof(StagedRow.Credit), StagedColumn.Credit, ColumnKind.Number, 120, true, format: "N2"),
            Pick("Bank", nameof(StagedRow.BankName), StagedColumn.BankName, ColumnKind.Text, 140, true),
            Pick("Br. code", nameof(StagedRow.BranchCode), StagedColumn.BranchCode, ColumnKind.Mono, 80, true),
            Pick("Narration 1", nameof(StagedRow.Narration1), StagedColumn.Narration1, ColumnKind.Text, 220, true),

            // Hidden until ticked in the Columns picker.
            Pick("CNIC", nameof(StagedRow.Cnic), StagedColumn.Cnic, ColumnKind.Mono, 130, false),
            Pick("Application no", nameof(StagedRow.ApplicationNo), StagedColumn.ApplicationNo, ColumnKind.Mono, 130, false),
            Pick("Reg no", nameof(StagedRow.RegNo), StagedColumn.RegNo, ColumnKind.Mono, 100, false),
            Pick("Account no", nameof(StagedRow.AccountNo), StagedColumn.AccountNo, ColumnKind.Mono, 140, false),
            Pick("Project", nameof(StagedRow.Project), StagedColumn.Project, ColumnKind.Text, 140, false),
            Pick("Dealer", nameof(StagedRow.DealerName), StagedColumn.DealerName, ColumnKind.Text, 150, false),
            Pick("Branch", nameof(StagedRow.BranchName), StagedColumn.BranchName, ColumnKind.Text, 140, false),
            Pick("Down payment", nameof(StagedRow.DownPaymentAmount), StagedColumn.DownPaymentAmount, ColumnKind.Number, 128, false, format: "N2"),
            Pick("Challan no", nameof(StagedRow.ChallanNo), StagedColumn.ChallanNo, ColumnKind.Mono, 110, false),
            Pick("Cheque / inst no", nameof(StagedRow.ChequeInstNo), StagedColumn.ChequeInstNo, ColumnKind.Mono, 130, false),
            Pick("Data source", nameof(StagedRow.DataSource), StagedColumn.DataSource, ColumnKind.Text, 130, false),
            Pick("Running balance", nameof(StagedRow.RunningBalance), StagedColumn.RunningBalance, ColumnKind.Number, 130, false, format: "N2"),
            Pick("Discount", nameof(StagedRow.Discount), StagedColumn.Discount, ColumnKind.Number, 100, false, format: "N2"),
            Pick("Plot no", nameof(StagedRow.PlotNo), StagedColumn.PlotNo, ColumnKind.Mono, 90, false),
            Pick("Street no", nameof(StagedRow.StreetNo), StagedColumn.StreetNo, ColumnKind.Mono, 90, false),
            Pick("Narration 2", nameof(StagedRow.Narration2), StagedColumn.Narration2, ColumnKind.Text, 240, false),
            Pick("Narration 3", nameof(StagedRow.Narration3), StagedColumn.Narration3, ColumnKind.Text, 240, false),
            Pick("Narration 4", nameof(StagedRow.Narration4), StagedColumn.Narration4, ColumnKind.Text, 240, false),
            Pick("Narration 5", nameof(StagedRow.Narration5), StagedColumn.Narration5, ColumnKind.Text, 240, false),
            Pick("CNIC in narration", nameof(StagedRow.NarrationCnic), StagedColumn.NarrationCnic, ColumnKind.Mono, 140, false),
        });

        if (conflict)
        {
            cols.Add(new("Changes", "Conflict.ChangeCount", ColumnKind.Number, Width: 96));
            cols.Add(new("What differs", "Conflict.Summary", ColumnKind.Text, Width: 560));
        }
        return cols;
    }
}
