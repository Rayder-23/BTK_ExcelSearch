namespace ExcelSearch.Services;

public enum DialogChoice { Yes, No, Cancel }

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

    /// <summary>Opens the folder containing the file in Explorer, with the file selected.</summary>
    void OpenFolder(string filePath);

    bool Confirm(string title, string message);

    DialogChoice AskYesNoCancel(string title, string message);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);
}
