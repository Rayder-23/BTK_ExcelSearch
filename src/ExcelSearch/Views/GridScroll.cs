using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExcelSearch.Views;

/// <summary>Scrolls a DataGrid back to its first row and leftmost column (used when a new page of rows arrives).</summary>
internal static class GridScroll
{
    public static void ToTop(DataGrid grid)
    {
        var viewer = Find(grid);
        viewer?.ScrollToVerticalOffset(0);
    }

    private static ScrollViewer? Find(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer sv) return sv;
            if (Find(child) is { } found) return found;
        }
        return null;
    }
}
