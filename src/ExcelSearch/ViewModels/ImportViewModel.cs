using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExcelSearch.Core.Staging;
using ExcelSearch.Services;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.ViewModels;

public enum ImportState { Idle, Staging, Preview, Committing, Done }

/// <summary>
/// The import screen's logic: choose a file, stage it, preview it, commit or discard. It only calls
/// IImportPreviewService and IDialogService; no database, Excel or WPF types, so it is unit-tested with fakes.
/// </summary>
public partial class ImportViewModel : ObservableObject
{
    private const string AppTitle = "ExcelSearch";

    private readonly IImportPreviewService _service;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ImportViewModel>? _logger;

    public ImportViewModel(IImportPreviewService service, IDialogService dialogs, ILogger<ImportViewModel>? logger = null)
    {
        _service = service;
        _dialogs = dialogs;
        _logger = logger;
    }

    // ---- state the view binds to -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsStaging), nameof(IsPreview), nameof(IsCommitting), nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand), nameof(StartOverCommand))]
    private ImportState _state = ImportState.Idle;

    public bool IsIdle => State == ImportState.Idle;
    public bool IsStaging => State == ImportState.Staging;
    public bool IsPreview => State == ImportState.Preview;
    public bool IsCommitting => State == ImportState.Committing;
    public bool IsDone => State == ImportState.Done;

    /// <summary>Error or status text shown in a banner (file rejected, commit failed, cancelled...).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    [ObservableProperty] private int _rowsLoaded;
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private int _totalRows;

    /// <summary>The staged batch, or null when there is none (Idle, or after Done).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand), nameof(StartOverCommand))]
    private int? _batchId;

    /// <summary>True while a short operation (discard) runs, so a second button cannot be pressed meanwhile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand), nameof(StartOverCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _newCount;

    [ObservableProperty] private int _conflictCount;
    [ObservableProperty] private int _invalidCount;
    [ObservableProperty] private int _exactDuplicateCount;
    [ObservableProperty] private int _fileDuplicateCount;

    public string CommitButtonText => $"Import {NewCount:N0} new rows";

    [ObservableProperty] private CommitResult? _lastCommit;

    public ObservableCollection<StatusTabViewModel> Tabs { get; } = new();

    [ObservableProperty] private StatusTabViewModel? _selectedTab;

    // Showing a tab loads its first page (lazily: tabs the user never opens cost nothing).
    partial void OnSelectedTabChanged(StatusTabViewModel? value) => _ = value?.EnsureLoadedAsync();

    // ---- choose + stage ----------------------------------------------------------------------------------

    /// <summary>
    /// Opens the file dialog, then parses, loads and classifies the file off the UI thread. IncludeCancelCommand
    /// also generates ChooseFileCancelCommand, which cancels the token passed in here.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ChooseFileAsync(CancellationToken ct)
    {
        string? path;
        try
        {
            path = _dialogs.PickExcelFile();
        }
        catch (Exception ex)
        {
            HandleError(ex);
            return;
        }
        if (path is null) return;   // user closed the dialog: stay where we are

        Message = null;
        RowsLoaded = 0;
        State = ImportState.Staging;

        // Progress<T> made on the UI thread delivers its callbacks on the UI thread, so setting RowsLoaded is safe.
        var progress = new Progress<int>(n => RowsLoaded = n);
        var user = ImportPreviewService.CurrentUser();

        try
        {
            var result = await Task.Run(() => _service.StageFileAsync(path, user, progress, ct), ct);

            if (result.BatchId is not { } id)
            {
                // Rejected file: no batch exists, nothing to clean up.
                Message = "This file cannot be imported:\n• " + string.Join("\n• ", result.FileErrors);
                State = ImportState.Idle;
                return;
            }

            ShowPreview(id, path, result);
        }
        catch (Exception ex) when (ct.IsCancellationRequested || ex is OperationCanceledException)
        {
            // The service has already discarded the partial batch.
            Message = "Import cancelled. Nothing was kept.";
            State = ImportState.Idle;
        }
        catch (Exception ex)
        {
            HandleError(ex);
            Message = ErrorDescriber.Describe(ex);
            State = ImportState.Idle;
        }
    }

    private void ShowPreview(int id, string path, StageResult result)
    {
        BatchId = id;
        FileName = Path.GetFileName(path);
        TotalRows = result.RowsLoaded;
        int Count(RowStatus s) => result.Counts.TryGetValue(s, out var n) ? n : 0;
        NewCount = Count(RowStatus.New);
        ConflictCount = Count(RowStatus.Conflict);
        InvalidCount = Count(RowStatus.Invalid);
        ExactDuplicateCount = Count(RowStatus.ExactDuplicate);
        FileDuplicateCount = Count(RowStatus.FileDuplicate);

        Tabs.Clear();
        Tabs.Add(NewTab(id, RowStatus.New, "New", NewCount));
        Tabs.Add(NewTab(id, RowStatus.Conflict, "Conflicts", ConflictCount));
        Tabs.Add(NewTab(id, RowStatus.Invalid, "Invalid", InvalidCount));
        Tabs.Add(NewTab(id, RowStatus.ExactDuplicate, "Already in database", ExactDuplicateCount));
        Tabs.Add(NewTab(id, RowStatus.FileDuplicate, "Repeated in file", FileDuplicateCount));

        State = ImportState.Preview;
        SelectedTab = Tabs[0];   // triggers the first page load
    }

    private StatusTabViewModel NewTab(int id, RowStatus status, string title, int count) =>
        new(_service, id, status, title, count, HandleError);

    // ---- commit ------------------------------------------------------------------------------------------

    private bool CanCommit() => State == ImportState.Preview && BatchId is not null && NewCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (BatchId is not { } id) return;

        var summary = $"This will insert {NewCount:N0} new rows into the database.\n" +
                      $"{ConflictCount:N0} conflicts will be skipped.\n" +
                      $"{InvalidCount:N0} invalid rows will not be imported.\n\nContinue?";
        if (!_dialogs.Confirm("Import new rows", summary)) return;

        Message = null;
        State = ImportState.Committing;
        try
        {
            // No conflict overwrites yet (that is the next step): every Conflict row stays as it is.
            var result = await Task.Run(() => _service.CommitAsync(id));

            if (result.Success)
            {
                LastCommit = result;
                BatchId = null;   // committed: nothing left to discard
                State = ImportState.Done;
                return;
            }

            Message = result.FailureReason == CommitFailure.RowsChangedSincePreview
                ? result.Message + "\n\nUse \"Start over\" to discard this batch and import the file again."
                : result.Message;
            State = ImportState.Preview;
        }
        catch (Exception ex)
        {
            HandleError(ex);
            State = ImportState.Preview;   // the batch is still staged; the user can retry or discard
        }
    }

    // ---- discard / start over / import another -----------------------------------------------------------

    private bool CanDiscard() => State == ImportState.Preview && BatchId is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task DiscardAsync()
    {
        if (!_dialogs.Confirm("Discard", $"Discard \"{FileName}\"? The staged rows will be deleted. " +
                                         "Nothing has been added to the database."))
            return;

        if (await DiscardBatchAsync())
            ResetToIdle("The staged file was discarded.");
    }

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task StartOverAsync()
    {
        if (!_dialogs.Confirm("Start over", $"Discard \"{FileName}\" and choose another file?"))
            return;

        if (await DiscardBatchAsync())
        {
            ResetToIdle(null);
            await ChooseFileCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private void ImportAnother() => ResetToIdle(null);

    /// <summary>Discards the batch. Returns false only if something unexpected failed (the user stays in Preview).</summary>
    private async Task<bool> DiscardBatchAsync()
    {
        if (BatchId is not { } id) return true;
        IsBusy = true;
        try
        {
            var result = await _service.DiscardAsync(id);
            // A refusal (already committed/discarded elsewhere) is explained, and the batch is no longer ours either way.
            if (!result.Discarded) _dialogs.ShowInfo(AppTitle, result.Reason ?? "The batch could not be discarded.");
            return true;
        }
        catch (Exception ex)
        {
            HandleError(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ResetToIdle(string? message)
    {
        BatchId = null;
        Tabs.Clear();
        SelectedTab = null;
        LastCommit = null;
        Message = message;
        State = ImportState.Idle;
    }

    // ---- window closing ----------------------------------------------------------------------------------

    /// <summary>Called when the window is closing. Returns false if the user (or a running commit) says to stay.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        switch (State)
        {
            case ImportState.Staging:
                if (!_dialogs.Confirm(AppTitle, "A file is still being loaded. Cancel it and exit? Nothing will be kept."))
                    return false;
                ChooseFileCancelCommand.Execute(null);
                try { await ChooseFileCommand.ExecutionTask!; } catch { /* cancelled or failed: either way we are leaving */ }
                return true;

            case ImportState.Committing:
                _dialogs.ShowInfo(AppTitle, "An import is being written to the database. Please wait until it finishes.");
                return false;

            case ImportState.Preview when BatchId is not null:
                switch (_dialogs.AskYesNoCancel(AppTitle,
                            $"\"{FileName}\" is staged but not imported. Discard it before closing?\n\n" +
                            "Yes = discard and exit, No = exit and keep it (it is removed automatically after 24 hours), " +
                            "Cancel = stay."))
                {
                    case DialogChoice.Yes: return await DiscardBatchAsync();
                    case DialogChoice.No: return true;
                    default: return false;
                }

            default:
                return true;
        }
    }

    // ---- errors ------------------------------------------------------------------------------------------

    /// <summary>Logs the exception and tells the user in plain words. Never rethrows: the app must not crash.</summary>
    private void HandleError(Exception ex)
    {
        _logger?.LogError(ex, "Unexpected error on the import screen.");
        try
        {
            _dialogs.ShowError(AppTitle, ErrorDescriber.Describe(ex));
        }
        catch (Exception dialogEx)
        {
            _logger?.LogError(dialogEx, "Could not show the error dialog.");
        }
    }
}
