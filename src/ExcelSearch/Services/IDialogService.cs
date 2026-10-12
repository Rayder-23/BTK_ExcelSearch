using ExcelSearch.Core.Query;

namespace ExcelSearch.Services;

public enum DialogChoice { Yes, No, Cancel }

/// <summary>Where and in what format to save a query export.</summary>
public sealed record ExportTarget(string Path, ExportFormat Format);

/// <summary>
/// Everything the ViewModels need from "the screen": file picker and message boxes. ViewModels never touch
/// MessageBox or OpenFileDialog directly (they would be untestable); tests supply a fake of this interface.
/// </summary>
public interface IDialogService
{
    /// <summary>Returns the chosen .xlsx path, or null if the user cancelled.</summary>
    string? PickExcelFile();

    /// <summary>Returns where to save the exported .xlsx (with <paramref name="suggestedName"/> pre-filled), or null if cancelled.</summary>
    string? PickSavePath(string suggestedName);

    /// <summary>
    /// Asks where to save a results export. With <paramref name="allowExcel"/> false only CSV is offered (too many rows for
    /// a sheet). The format follows the file name the user ends up with. Null if cancelled.
    /// </summary>
    ExportTarget? PickExportPath(string suggestedName, bool allowExcel);

    /// <summary>Opens the folder containing the file in Explorer, with the file selected.</summary>
    void OpenFolder(string filePath);

    bool Confirm(string title, string message);

    DialogChoice AskYesNoCancel(string title, string message);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);
}
