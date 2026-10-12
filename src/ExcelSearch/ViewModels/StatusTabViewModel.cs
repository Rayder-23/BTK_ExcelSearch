using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExcelSearch.Core.Staging;

namespace ExcelSearch.ViewModels;

/// <summary>How a grid cell is drawn: plain text, monospaced (keys), monospaced right-aligned (amounts), or an error message.</summary>
public enum ColumnKind { Text, Mono, Number, Error }

/// <summary>One grid column: header text, the PreviewRow property path it shows, an optional format, and a width.</summary>
public sealed record GridColumn(string Header, string Path, ColumnKind Kind = ColumnKind.Text, string? Format = null, double Width = 0);

/// <summary>
/// One grid row. Every tab shows the staged row; the Conflict tab also carries what differs from the database.
/// </summary>
public sealed record PreviewRow(StagedRow Row, ConflictRow? Conflict = null)
{
    public bool IsConflict => Conflict is not null;
}

/// <summary>
/// One tab of the preview (one RowStatus): its rows for the current page, the paging state and Previous/Next.
/// Pages are fetched from the server on demand (100 rows), never the whole status at once.
/// </summary>
public partial class StatusTabViewModel : ObservableObject
{
    public const int PageSize = 100;

    private readonly IImportPreviewService _service;
    private readonly int _batchId;
    private readonly Action<Exception> _onError;
    private int _loadVersion;   // lets a slow, older page load be ignored if a newer one was started

    public StatusTabViewModel(IImportPreviewService service, int batchId, RowStatus status, string title, int count,
        Action<Exception> onError)
    {
        _service = service;
        _batchId = batchId;
        _onError = onError;
        Status = status;
        Title = title;
        Count = count;
        Columns = BuildColumns(status);
        Hint = HintFor(status);
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
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    private bool _isLoading;

    public bool IsLoaded { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalRows / (double)PageSize));
    public bool IsEmpty => IsLoaded && TotalRows == 0;
    public string PageText => $"Page {PageNumber:N0} of {TotalPages:N0}  ({TotalRows:N0} rows)";

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
                // Conflicts come with the existing database row compared column by column.
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

            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            IsLoaded = true;
            TotalRows = total;
            PageNumber = page;
            // Open the first conflict straight away so the user sees what a conflict looks like.
            SelectedRow = ShowsDetails ? Rows.FirstOrDefault() : null;
            OnPropertyChanged(nameof(IsEmpty));
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
                              "Select a row to see exactly what differs. They are skipped on import; overwriting comes in the next step.",
        RowStatus.Invalid => "These rows have problems and will not be imported. The Error column says what to fix in the Excel file.",
        RowStatus.ExactDuplicate => "These rows are already in the database with identical values. They are skipped.",
        RowStatus.FileDuplicate => "These rows repeat an identical earlier row of this file. Only the first copy is used.",
        _ => "",
    };

    // ExcelRowNumber first everywhere; ErrorText right after it on the Invalid tab. Keys and amounts are monospaced
    // so digits line up; the Conflict tab shows what differs instead of the amount columns.
    private static IReadOnlyList<GridColumn> BuildColumns(RowStatus status)
    {
        var cols = new List<GridColumn>
        {
            new("Row", "Row.ExcelRowNumber", ColumnKind.Number, Width: 64),
        };

        if (status == RowStatus.Invalid)
            cols.Add(new("Error", "Row.ErrorText", ColumnKind.Error, Width: 420));

        cols.Add(new("Pay order no", "Row.PayOrderNo", ColumnKind.Mono, Width: 150));
        cols.Add(new("Txn ref", "Row.TxnRefSeqNo", ColumnKind.Mono, Width: 90));

        if (status == RowStatus.Conflict)
        {
            cols.Add(new("Changes", "Conflict.ChangeCount", ColumnKind.Number, Width: 80));
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
