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
        DataContextChanged += (_, e) => BuildColumns(e.NewValue as StatusTabViewModel);
        RowsGrid.PreviewMouseLeftButtonDown += OnGridMouseDown;
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
            if (c.Kind == ColumnKind.Overwrite)
            {
                // A template column (not DataGridCheckBoxColumn) so one click toggles even though the grid is read-only.
                RowsGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = c.Header,
                    Width = new DataGridLength(c.Width),
                    CellTemplate = (DataTemplate)FindResource("OverwriteCell"),
                    SortMemberPath = c.Path,
                });
                continue;
            }

            var styleKey = c.Kind switch
            {
                ColumnKind.Mono => "CellMono",
                ColumnKind.Number => "CellNumber",
                ColumnKind.Error => "CellError",
                _ => "CellText",
            };

            var column = new DataGridTextColumn
            {
                Header = c.Header,
                Binding = new Binding(c.Path) { StringFormat = c.Format },
                Width = c.Width > 0 ? new DataGridLength(c.Width) : DataGridLength.Auto,
                ElementStyle = (Style)FindResource(styleKey),
            };
            RowsGrid.Columns.Add(column);
        }
    }
}
