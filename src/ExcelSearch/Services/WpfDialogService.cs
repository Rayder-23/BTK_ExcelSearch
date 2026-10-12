using System.Windows;
using Microsoft.Win32;

namespace ExcelSearch.Services;

/// <summary>The real dialogs: standard Windows message boxes and the Open File dialog.</summary>
public sealed class WpfDialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public string? PickExcelFile()
    {
        var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", Title = "Choose an Excel file" };
        var ok = Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FileName : null;
    }

    public string? PickSavePath(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            Title = "Save the invalid rows",
            FileName = suggestedName,
            DefaultExt = ".xlsx",
            AddExtension = true,
            OverwritePrompt = true,
        };
        var ok = Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FileName : null;
    }

    public ExportTarget? PickExportPath(string suggestedName, bool allowExcel)
    {
        var dialog = new SaveFileDialog
        {
            Filter = allowExcel ? "Excel workbook (*.xlsx)|*.xlsx|CSV file (*.csv)|*.csv" : "CSV file (*.csv)|*.csv",
            Title = "Export results",
            FileName = suggestedName,
            AddExtension = true,
            OverwritePrompt = true,
        };
        var ok = Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? ResolveTarget(dialog.FileName, dialog.FilterIndex, allowExcel) : null;
    }

    /// <summary>
    /// The file name wins over the filter dropdown: "x.csv" is a CSV whichever filter was showing. A name with another
    /// extension gets the extension of the selected filter. Excel is never returned when it is not allowed.
    /// </summary>
    internal static ExportTarget ResolveTarget(string fileName, int filterIndex, bool allowExcel)
    {
        var ext = System.IO.Path.GetExtension(fileName);
        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)) return new ExportTarget(fileName, Core.Query.ExportFormat.Csv);
        if (allowExcel && ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) return new ExportTarget(fileName, Core.Query.ExportFormat.Xlsx);

        var excel = allowExcel && filterIndex == 1;
        return new ExportTarget(fileName + (excel ? ".xlsx" : ".csv"), excel ? Core.Query.ExportFormat.Xlsx : Core.Query.ExportFormat.Csv);
    }

    public void OpenFolder(string filePath)
    {
        // explorer.exe /select,"<file>" opens the folder with the file highlighted.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
        {
            UseShellExecute = true,
        });
    }

    public bool Confirm(string title, string message) =>
        Show(title, message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public DialogChoice AskYesNoCancel(string title, string message) =>
        Show(title, message, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => DialogChoice.Yes,
            MessageBoxResult.No => DialogChoice.No,
            _ => DialogChoice.Cancel,
        };

    public void ShowInfo(string title, string message) =>
        Show(title, message, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) =>
        Show(title, message, MessageBoxButton.OK, MessageBoxImage.Error);

    private static MessageBoxResult Show(string title, string message, MessageBoxButton buttons, MessageBoxImage icon) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, title, buttons, icon)
            : MessageBox.Show(message, title, buttons, icon);
}
