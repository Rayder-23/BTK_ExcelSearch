using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExcelSearch.Core.Staging;

namespace ExcelSearch.ViewModels;

/// <summary>How a grid cell is drawn: text, monospaced (keys), monospaced right-aligned (amounts), an error message, or the Overwrite checkbox.</summary>
public enum ColumnKind { Text, Mono, Number, Error, Overwrite }

/// <summary>One grid column: header text, the PreviewRow property path it shows, an optional format, and a width.</summary>
public sealed record GridColumn(string Header, string Path, ColumnKind Kind = ColumnKind.Text, string? Format = null, double Width = 0);

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
        Hint = HintFor(status);
        ConflictTotal = status == RowStatus.Conflict ? count : 0;
    }

    public RowStatus Status { get; }
    public string Title { get; }
    public int Count { get; }
    public IReadOnlyList<GridColumn> Columns { get; }

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
                var result = await _service.GetConflictPageAsync(_batchId, page, PageSize);
                rows = result.Rows.Select(c => new PreviewRow(c.Incoming, c)).ToList();
                total = result.Total;
            }
            else
            {
                var result = await _service.GetPageAsync(_batchId, Status, page, PageSize);
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
        RowStatus.ExactDuplicate => "These rows are already in the database with identical values. They are skipped.",
        RowStatus.FileDuplicate => "These rows repeat an identical earlier row of this file. Only the first copy is used.",
        _ => "",
    };

    // ExcelRowNumber first everywhere; ErrorText right after it on the Invalid tab. Keys and amounts are monospaced
    // so digits line up; the Conflict tab shows the Overwrite checkbox and what differs instead of the amount columns.
    private static IReadOnlyList<GridColumn> BuildColumns(RowStatus status)
    {
        var cols = new List<GridColumn>
        {
            new("Row", "Row.ExcelRowNumber", ColumnKind.Number, Width: 64),
        };

        if (status == RowStatus.Invalid)
            cols.Add(new("Error", "Row.ErrorText", ColumnKind.Error, Width: 420));

        if (status == RowStatus.Conflict)
            cols.Add(new("Overwrite", nameof(PreviewRow.IsOverwrite), ColumnKind.Overwrite, Width: 112));

        cols.Add(new("Pay order no", "Row.PayOrderNo", ColumnKind.Mono, Width: 150));
        cols.Add(new("Txn ref", "Row.TxnRefSeqNo", ColumnKind.Mono, Width: 90));

        if (status == RowStatus.Conflict)
        {
            cols.Add(new("Changes", "Conflict.ChangeCount", ColumnKind.Number, Width: 96));
            cols.Add(new("What differs", "Conflict.Summary", ColumnKind.Text, Width: 560));
            return cols;
        }

        cols.AddRange(new GridColumn[]
        {
            new("Value date", "Row.ValueDate", ColumnKind.Mono, "dd-MMM-yyyy", 110),
            new("Customer", "Row.CustomerName", ColumnKind.Text, Width: 210),
            new("Debit", "Row.Debit", ColumnKind.Number, "N2", 120),
            new("Credit", "Row.Credit", ColumnKind.Number, "N2", 120),
            new("Balance", "Row.RunningBalance", ColumnKind.Number, "N2", 130),
            new("Bank", "Row.BankName", ColumnKind.Text, Width: 140),
            new("Project", "Row.Project", ColumnKind.Text, Width: 220),
        });
        return cols;
    }
}
