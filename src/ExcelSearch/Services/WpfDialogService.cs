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
