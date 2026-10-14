using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using ExcelSearch.Core.Export;
using ExcelSearch.Core.Staging;
using ExcelSearch.Services;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.ViewModels;

public enum ImportState { Idle, Staging, Preview, Committing, Done }

/// <summary>
/// The import screen's logic: choose a file, stage it, preview it, choose which conflicts to overwrite, re-check against
/// the database, export the invalid rows, commit or discard. It only calls IImportPreviewService, IInvalidRowExporter and
/// IDialogService; no database, Excel or WPF types, so it is unit-tested with fakes.
/// </summary>
public partial class ImportViewModel : ObservableObject
{
    private const string AppTitle = "ExcelSearch";

    private readonly IImportPreviewService _service;
    private readonly IDialogService _dialogs;
    private readonly IInvalidRowExporter _exporter;
    private readonly ILogger<ImportViewModel>? _logger;
    private readonly IMessenger _messenger;
    private string? _sourcePath;   // the workbook that was chosen, needed to export its invalid rows later

    public ImportViewModel(IImportPreviewService service, IDialogService dialogs, IInvalidRowExporter exporter,
        ILogger<ImportViewModel>? logger = null, IMessenger? messenger = null)
    {
        _service = service;
        _dialogs = dialogs;
        _exporter = exporter;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    // ---- state the view binds to -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsStaging), nameof(IsPreview), nameof(IsCommitting), nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand), nameof(RefreshCommand), nameof(ExportInvalidCommand))]
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

    /// <summary>Closes the banner. Messages never time out: one can appear after a long unattended import and must stay until read.</summary>
    [RelayCommand]
    private void DismissMessage() => Message = null;

    [ObservableProperty] private int _rowsLoaded;
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private int _totalRows;

    /// <summary>The staged batch, or null when there is none (Idle, or after Done).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand), nameof(RefreshCommand), nameof(ExportInvalidCommand))]
    private int? _batchId;

    /// <summary>True while a short operation (discard, re-check) runs, so a second button cannot be pressed meanwhile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand), nameof(RefreshCommand), nameof(ExportInvalidCommand))]
    private bool _isBusy;

    /// <summary>True while the invalid rows are being written to a file.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(DiscardCommand), nameof(RefreshCommand), nameof(ExportInvalidCommand))]
    private bool _isExporting;

    [ObservableProperty] private int _exportedRows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _newCount;

    [ObservableProperty] private int _conflictCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportInvalidCommand))]
    private int _invalidCount;

    [ObservableProperty] private int _exactDuplicateCount;
    [ObservableProperty] private int _fileDuplicateCount;
    [ObservableProperty] private int _likelyDuplicateCount;

    /// <summary>
    /// Rows without a pay order no + txn ref that are flagged as duplicates (Already in database, Repeated in file, Possible duplicate).
    /// A commit imports them unless <see cref="SkipFlaggedDuplicates"/> is ticked. Always the database's number.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText), nameof(HasFlagged))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _flaggedImportCount;

    public bool HasFlagged => FlaggedImportCount > 0;

    /// <summary>The user's choice: leave the flagged duplicates out. Off by default (they are imported).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private bool _skipFlaggedDuplicates;

    /// <summary>The flagged rows the next commit would import.</summary>
    private int FlaggedToImport => SkipFlaggedDuplicates ? 0 : FlaggedImportCount;

    /// <summary>How many conflicts are set to overwrite. Always the database's number, never counted from the visible page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private int _overwriteCount;

    public string CommitButtonText
    {
        get
        {
            var flagged = FlaggedToImport > 0 ? $" (+{FlaggedToImport:N0} flagged duplicates)" : "";
            return OverwriteCount > 0
                ? $"Import {NewCount:N0} new{flagged}, update {OverwriteCount:N0}"
                : $"Import {NewCount:N0} new rows{flagged}";
        }
    }

    [ObservableProperty] private CommitResult? _lastCommit;

    public ObservableCollection<StatusTabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvalidTabSelected))]
    private StatusTabViewModel? _selectedTab;

    /// <summary>The export button belongs to the Invalid tab.</summary>
    public bool IsInvalidTabSelected => SelectedTab?.Status == RowStatus.Invalid;

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

            _sourcePath = path;
            await ShowPreviewAsync(id, path, result);
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

    private async Task ShowPreviewAsync(int id, string path, StageResult result)
    {
        BatchId = id;
        FileName = Path.GetFileName(path);
        TotalRows = result.RowsLoaded;
        ApplyCounts(result.Counts);
        BuildTabs(id, null);

        State = ImportState.Preview;
        SelectedTab = Tabs[0];   // triggers the first page load
        await LoadOverwriteCountAsync(id);
        await LoadFlaggedCountAsync(id);
    }

    private void ApplyCounts(IReadOnlyDictionary<RowStatus, int> counts)
    {
        int Count(RowStatus s) => counts.TryGetValue(s, out var n) ? n : 0;
        NewCount = Count(RowStatus.New);
        ConflictCount = Count(RowStatus.Conflict);
        InvalidCount = Count(RowStatus.Invalid);
        ExactDuplicateCount = Count(RowStatus.ExactDuplicate);
        FileDuplicateCount = Count(RowStatus.FileDuplicate);
        LikelyDuplicateCount = Count(RowStatus.LikelyDuplicate);
    }

    /// <summary>Creates the six tabs; <paramref name="select"/> keeps the user on the tab they were looking at.</summary>
    private void BuildTabs(int id, RowStatus? select)
    {
        Tabs.Clear();
        Tabs.Add(NewTab(id, RowStatus.New, "New", NewCount));
        Tabs.Add(NewTab(id, RowStatus.Conflict, "Conflicts", ConflictCount));
        Tabs.Add(NewTab(id, RowStatus.Invalid, "Invalid", InvalidCount));
        Tabs.Add(NewTab(id, RowStatus.ExactDuplicate, "Already in database", ExactDuplicateCount));
        Tabs.Add(NewTab(id, RowStatus.FileDuplicate, "Repeated in file", FileDuplicateCount));
        Tabs.Add(NewTab(id, RowStatus.LikelyDuplicate, "Possible duplicates", LikelyDuplicateCount));
        if (select is { } s) SelectedTab = Tabs.First(t => t.Status == s);
    }

    private StatusTabViewModel NewTab(int id, RowStatus status, string title, int count) =>
        new(_service, id, status, title, count, HandleError, n => OverwriteCount = n);

    /// <summary>Reads "how many conflicts will overwrite" from the database (never from the rows on screen).</summary>
    private async Task LoadOverwriteCountAsync(int id)
    {
        try
        {
            OverwriteCount = (await _service.GetConflictCountsAsync(id)).Overwrite;
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    /// <summary>Reads "how many flagged key-less rows would be imported" from the database.</summary>
    private async Task LoadFlaggedCountAsync(int id)
    {
        try
        {
            FlaggedImportCount = await _service.GetFlaggedKeylessCountAsync(id);
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    // ---- re-check against the database -------------------------------------------------------------------

    private bool CanRefresh() => State == ImportState.Preview && BatchId is not null && !IsBusy && !IsExporting;

    /// <summary>
    /// Re-compares the staged rows with Transactions as it is now (someone may have imported since the preview), then
    /// reloads the counts and the current tab and says what moved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (BatchId is not { } id) return;

        IsBusy = true;
        Message = null;
        try
        {
            var result = await _service.RefreshAgainstDatabaseAsync(id);
            if (!result.Success)
            {
                Message = result.Describe();   // e.g. the batch is no longer Staged
                return;
            }

            var counts = await _service.GetCountsAsync(id);
            ApplyCounts(counts);
            BuildTabs(id, SelectedTab?.Status ?? RowStatus.New);
            await LoadOverwriteCountAsync(id);
            await LoadFlaggedCountAsync(id);
            Message = result.Describe();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- commit ------------------------------------------------------------------------------------------

    private bool CanCommit() =>
        State == ImportState.Preview && BatchId is not null && (NewCount > 0 || OverwriteCount > 0 || FlaggedToImport > 0) && !IsBusy && !IsExporting;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (BatchId is not { } id) return;

        var overwrite = OverwriteCount;
        var flaggedImported = FlaggedToImport;
        var flaggedTotal = ExactDuplicateCount + FileDuplicateCount + LikelyDuplicateCount;
        // Rows with a pay order no + txn ref that duplicate something are always skipped; flagged key-less rows only when asked.
        var skip = (ConflictCount - overwrite) + (flaggedTotal - flaggedImported);
        var summary = "This will:\n" +
                      $"• insert {NewCount:N0} new rows\n" +
                      (flaggedImported > 0 ? $"• insert {flaggedImported:N0} more rows that are flagged as possible duplicates\n" : "") +
                      $"• overwrite {overwrite:N0} existing rows with the values in the file\n" +
                      $"• skip {skip:N0} rows (conflicts you did not choose to overwrite, and duplicates that are skipped)\n" +
                      $"• not import {InvalidCount:N0} invalid rows\n\nContinue?";
        if (!_dialogs.Confirm("Import", summary)) return;

        Message = null;
        State = ImportState.Committing;
        try
        {
            // The overwrite choices are already saved in staging, so CommitAsync applies them.
            var skipFlagged = SkipFlaggedDuplicates;
            var result = await Task.Run(() => _service.CommitAsync(id, skipFlagged));

            if (result.Success)
            {
                LastCommit = result;
                BatchId = null;   // committed: nothing left to discard
                _messenger.Send(new ImportCommittedMessage(id));   // the Query screen refreshes its dropdown lists
                State = ImportState.Done;
                return;
            }

            Message = result.FailureReason == CommitFailure.RowsChangedSincePreview
                ? result.Message + "\n\nUse \"Re-check against database\" to refresh this preview with the current database contents."
                : result.Message;
            State = ImportState.Preview;
        }
        catch (Exception ex)
        {
            HandleError(ex);
            State = ImportState.Preview;   // the batch is still staged; the user can retry or discard
        }
    }

    // ---- export invalid rows -----------------------------------------------------------------------------

    private bool CanExport() =>
        State == ImportState.Preview && BatchId is not null && InvalidCount > 0 && _sourcePath is not null && !IsBusy && !IsExporting;

    /// <summary>
    /// Saves the batch's invalid rows (original values + Source Row + Errors) to an .xlsx. IncludeCancelCommand adds
    /// ExportInvalidCancelCommand for the Cancel button shown while it runs.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport), IncludeCancelCommand = true)]
    private async Task ExportInvalidAsync(CancellationToken ct)
    {
        if (BatchId is not { } id || _sourcePath is not { } source) return;

        var suggested = Path.GetFileNameWithoutExtension(source) + "_errors.xlsx";
        var destination = _dialogs.PickSavePath(suggested);
        if (destination is null) return;   // save dialog closed

        Message = null;
        ExportedRows = 0;
        IsExporting = true;
        var progress = new Progress<int>(n => ExportedRows = n);
        try
        {
            var result = await Task.Run(() => _exporter.ExportAsync(source, id, destination, progress, ct), ct);

            var text = result.RowsWritten == 0
                ? result.Note ?? "There were no invalid rows to export."
                : $"Exported {result.RowsWritten:N0} invalid rows to {destination}." + (result.Note is null ? "" : "\n" + result.Note);
            Message = text;
            if (result.RowsWritten > 0 && _dialogs.Confirm("Export finished", text + "\n\nOpen the folder?"))
                _dialogs.OpenFolder(destination);
        }
        catch (Exception ex) when (ct.IsCancellationRequested || ex is OperationCanceledException)
        {
            Message = "Export cancelled. No file was written.";
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsExporting = false;
        }
    }

    // ---- discard / import another -----------------------------------------------------------

    private bool CanDiscard() => State == ImportState.Preview && BatchId is not null && !IsBusy && !IsExporting;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task DiscardAsync()
    {
        if (!_dialogs.Confirm("Discard", $"Discard \"{FileName}\"? The staged rows will be deleted. " +
                                         "Nothing has been added to the database."))
            return;

        if (await DiscardBatchAsync())
            ResetToIdle("The staged file was discarded.");
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
        _sourcePath = null;
        OverwriteCount = 0;
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
                if (IsExporting)
                {
                    if (!_dialogs.Confirm(AppTitle, "The invalid rows are still being exported. Cancel the export and exit?"))
                        return false;
                    ExportInvalidCancelCommand.Execute(null);
                }
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
