using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ExcelSearch.Core.Query;
using ExcelSearch.ViewModels;

namespace ExcelSearch.Views;

/// <summary>
/// The only code-behind that does anything, three small jobs XAML cannot do on its own:
///  1. build the grid columns from the ViewModel's column list (like StatusTabView);
///  2. turn a header click into a server-side sort (the grid's own client-side sort is switched off) and show the sort arrow;
///  3. make Enter in a filter box run the search.
/// </summary>
public partial class QueryView : UserControl
{
    /// <summary>Page sizes for the "rows per page" box.</summary>
    public static IReadOnlyList<int> PageSizes => QueryViewModel.PageSizes;

    /// <summary>The two choices for what an export writes.</summary>
    public static IReadOnlyList<string> ExportScopes => QueryViewModel.ExportScopes;

    private QueryViewModel? _vm;
    private bool _enterWentDown;

    public QueryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ResultsGrid.Sorting += OnSorting;

        // Enter: see the two handlers below.
        FilterPanel.PreviewKeyDown += OnFilterKeyDown;
        FilterPanel.KeyUp += OnFilterKeyUp;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnViewModelChanged;
        _vm = e.NewValue as QueryViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnViewModelChanged;
        BuildColumns(_vm);
    }

    private void BuildColumns(QueryViewModel? vm)
    {
        ResultsGrid.Columns.Clear();
        if (vm is null) return;

        foreach (var choice in vm.ColumnChoices)
        {
            var c = choice.Column;
            var styleKey = c.Kind switch
            {
                ColumnKind.Mono => "CellMono",
                ColumnKind.Number => "CellNumber",
                _ => "CellText",
            };
            var column = new DataGridTextColumn
            {
                Header = c.Header,
                Binding = new Binding(c.Path) { StringFormat = c.Format },
                Width = c.Width > 0 ? new DataGridLength(c.Width) : DataGridLength.Auto,
                ElementStyle = (Style)FindResource(styleKey),
                // The name of the whitelisted sort column; the ViewModel rejects anything that is not in the list.
                SortMemberPath = c.Sort?.ToString() ?? "",
                CanUserSort = c.Sort is not null,
                Visibility = choice.IsVisible ? Visibility.Visible : Visibility.Collapsed,
            };
            ResultsGrid.Columns.Add(column);

            // The "Columns" picker ticks/unticks this choice; show or hide the matching grid column.
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ColumnChoice.IsVisible))
                    column.Visibility = choice.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            };
        }
        ShowSortArrow();
    }

    /// <summary>A header was clicked: let the server sort the whole result instead of the grid sorting the page.</summary>
    private void OnSorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        _vm?.SortBy(e.Column.SortMemberPath);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The grid forgets its header arrows whenever its rows are replaced, so the arrow is re-applied after each new page
        // (queued, so it runs after the grid has finished swapping the rows).
        if (e.PropertyName is nameof(QueryViewModel.Sort) or nameof(QueryViewModel.Rows))
            Dispatcher.BeginInvoke(ShowSortArrow, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The arrow in the header template follows DataGridColumn.SortDirection; set it on the active column only.</summary>
    private void ShowSortArrow()
    {
        if (_vm is null) return;
        var active = _vm.Sort.Column.ToString();
        foreach (var column in ResultsGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == active
                ? (_vm.Sort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    // ---- Enter runs the search --------------------------------------------------------------------------
    // A date box commits its text on Enter and marks the key handled, so a plain KeyBinding would never fire there.
    // Instead: note the Enter going down in a text box, and run the search when it comes back up (by then the date
    // box has stored its value). Buttons and drop-down lists are left alone so Enter still presses/chooses as usual.

    private void OnFilterKeyDown(object sender, KeyEventArgs e) =>
        _enterWentDown = e.Key == Key.Enter && e.OriginalSource is TextBox;

    private void OnFilterKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_enterWentDown) return;
        _enterWentDown = false;
        if (_vm?.SearchCommand.CanExecute(null) == true) _vm.SearchCommand.Execute(null);
        e.Handled = true;
    }
}
