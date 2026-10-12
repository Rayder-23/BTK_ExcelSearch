using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    }

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
