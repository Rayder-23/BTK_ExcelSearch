using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ExcelSearch.ViewModels;

namespace ExcelSearch.Views;

/// <summary>
/// The only code-behind that does anything: it builds the DataGrid columns from the tab's column list, because the
/// columns differ per tab (the Invalid tab has an Error column, the Conflict tab shows what differs). Think of it
/// as the loop that writes the table header.
/// </summary>
public partial class StatusTabView : UserControl
{
    public StatusTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RowsGrid.PreviewMouseLeftButtonDown += OnGridMouseDown;
        RowsGrid.Sorting += OnSorting;
    }

    private StatusTabViewModel? _tab;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_tab is not null)
        {
            _tab.Rows.CollectionChanged -= OnRowsChanged;
            _tab.PropertyChanged -= OnTabChanged;
        }
        _tab = e.NewValue as StatusTabViewModel;
        if (_tab is not null)
        {
            _tab.Rows.CollectionChanged += OnRowsChanged;
            _tab.PropertyChanged += OnTabChanged;
        }
        BuildColumns(_tab);
    }

    /// <summary>A new page replaces the rows (the collection is cleared first): start it at the top.</summary>
    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset) return;
        Dispatcher.BeginInvoke(() => GridScroll.ToTop(RowsGrid), System.Windows.Threading.DispatcherPriority.Loaded);
        // The grid forgets its header arrows whenever its rows are replaced: put the arrow back once it has finished.
        Dispatcher.BeginInvoke(ShowSortArrow, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusTabViewModel.Sort))
            Dispatcher.BeginInvoke(ShowSortArrow, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>A header was clicked: let the server sort the whole status instead of the grid sorting the page.</summary>
    private void OnSorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        _tab?.SortBy(e.Column.SortMemberPath);
    }

    /// <summary>The arrow in the header template follows DataGridColumn.SortDirection; set it on the active column only.</summary>
    private void ShowSortArrow()
    {
        if (_tab is null) return;
        var active = _tab.Sort.Column.ToString();
        foreach (var column in RowsGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == active
                ? (_tab.Sort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    /// <summary>
    /// A click on an Overwrite checkbox must only tick it. By default the same click also selects the row, which opens
    /// the details panel, which moves the row (and the checkbox) away before the click finishes, so it took two clicks.
    /// Here the click is handled before the grid sees it: toggle the choice, do not touch the selection.
    /// </summary>
    private static void OnGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = GetParent(node))
        {
            if (node is not CheckBox box) continue;
            if (box.DataContext is PreviewRow row)
            {
                row.IsOverwrite = !row.IsOverwrite;
                e.Handled = true;
            }
            return;
        }
    }

    private static DependencyObject? GetParent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private void BuildColumns(StatusTabViewModel? tab)
    {
        RowsGrid.Columns.Clear();
        if (tab is null) return;

        // Only the Conflict tab opens a details panel under the selected row.
        RowsGrid.RowDetailsVisibilityMode = tab.ShowsDetails
            ? DataGridRowDetailsVisibilityMode.VisibleWhenSelected
            : DataGridRowDetailsVisibilityMode.Collapsed;

        foreach (var c in tab.Columns)
        {
            DataGridColumn column;
            if (c.Kind == ColumnKind.Overwrite)
            {
                // A template column (not DataGridCheckBoxColumn) so one click toggles even though the grid is read-only.
                column = new DataGridTemplateColumn
                {
                    Header = c.Header,
                    Width = new DataGridLength(c.Width),
                    CellTemplate = (DataTemplate)FindResource("OverwriteCell"),
                };
            }
            else
            {
                var styleKey = c.Kind switch
                {
                    ColumnKind.Mono => "CellMono",
                    ColumnKind.Number => "CellNumber",
                    ColumnKind.Error => "CellError",
                    _ => "CellText",
                };
                column = new DataGridTextColumn
                {
                    Header = c.Header,
                    Binding = new Binding(c.Path) { StringFormat = c.Format },
                    Width = c.Width > 0 ? new DataGridLength(c.Width) : DataGridLength.Auto,
                    ElementStyle = (Style)FindResource(styleKey),
                };
            }

            // The name of the whitelisted sort column; the ViewModel rejects anything that is not in the list.
            column.SortMemberPath = c.Sort?.ToString() ?? "";
            column.CanUserSort = c.Sort is not null;
            if (c.Sort is null) column.HeaderStyle = (Style)FindResource("UnsortableColumnHeader");
            RowsGrid.Columns.Add(column);

            // The "Columns" picker ticks/unticks this choice; show or hide the matching grid column.
            var choice = tab.ColumnChoices.FirstOrDefault(x => x.Column == c);
            if (choice is null) continue;
            column.Visibility = choice.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GridColumnChoice.IsVisible))
                    column.Visibility = choice.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            };
        }
        ShowSortArrow();
    }
}
