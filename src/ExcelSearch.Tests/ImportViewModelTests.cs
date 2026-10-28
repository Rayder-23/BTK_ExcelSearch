using System.Data.Common;
using ExcelSearch.Core.Export;
using ExcelSearch.Core.Staging;
using ExcelSearch.Services;
using ExcelSearch.ViewModels;

namespace ExcelSearch.Tests;

/// <summary>A stand-in for the real service: the test decides what staging/commit/discard return. No database.</summary>
internal sealed class FakeImportPreviewService : IImportPreviewService
{
    public Func<string, CancellationToken, Task<StageResult>> OnStage { get; set; } =
        (_, _) => Task.FromResult(FakeImportPreviewService.Staged(1));
    public Func<int, Task<CommitResult>> OnCommit { get; set; } =
        _ => Task.FromResult(new CommitResult(true, CommitFailure.None, "ok", NewRows: 3));
    public Func<int, Task<DiscardResult>> OnDiscard { get; set; } = _ => Task.FromResult(new DiscardResult(true));

    public List<string> StagedPaths { get; } = new();
    public List<(int Batch, RowStatus Status, int Page, int Size)> PageRequests { get; } = new();
    public List<int> Commits { get; } = new();
    public List<int> Discards { get; } = new();

    public Task<StageResult> StageFileAsync(string path, string? importedBy, IProgress<int>? progress, CancellationToken ct = default)
    {
        StagedPaths.Add(path);
        return OnStage(path, ct);
    }

    /// <summary>The sort of every page request, in order.</summary>
    public List<StagedSort> Sorts { get; } = new();

    public Task<PagedRows> GetPageAsync(int batchId, RowStatus status, int pageNumber, int pageSize, StagedSort sort = default, CancellationToken ct = default)
    {
        lock (PageRequests) { PageRequests.Add((batchId, status, pageNumber, pageSize)); Sorts.Add(sort); }
        var rows = new[] { new StagedRow { ExcelRowNumber = pageNumber, Status = status } };
        return Task.FromResult(new PagedRows(rows, Counts.GetValueOrDefault(status)));
    }

    /// <summary>Totals the paging tests rely on (GetPageAsync reports these as the status total).</summary>
    public Dictionary<RowStatus, int> Counts { get; } = new();

    /// <summary>Conflict pages are recorded with the other page requests so paging tests treat all tabs alike.</summary>
    public Func<int, ConflictRow> ConflictFor { get; set; } = n => new ConflictRow(
        new StagedRow { ExcelRowNumber = n, Status = RowStatus.Conflict, PayOrderNo = "PO", TxnRefSeqNo = "T" },
        new[] { new FieldChange("Debit", "1.00", "2.00", IsAmount: true) });

    public Task<PagedConflicts> GetConflictPageAsync(int batchId, int pageNumber, int pageSize, StagedSort sort = default, CancellationToken ct = default)
    {
        lock (PageRequests) { PageRequests.Add((batchId, RowStatus.Conflict, pageNumber, pageSize)); Sorts.Add(sort); }
        return Task.FromResult(new PagedConflicts(new[] { ConflictFor(pageNumber) }, Counts.GetValueOrDefault(RowStatus.Conflict)));
    }

    /// <summary>Every SetConflictOverwriteAsync call: (batch, overwrite, row numbers or null for "all").</summary>
    public List<(int Batch, bool Overwrite, int[]? Rows)> OverwriteCalls { get; } = new();
    public Func<Task>? OnSetOverwrite { get; set; }

    /// <summary>What the "database" says about how many conflicts are set to overwrite.</summary>
    public int OverwriteInDb { get; set; }

    public async Task<int> SetConflictOverwriteAsync(int batchId, bool overwrite, IReadOnlyCollection<int>? excelRowNumbers = null, CancellationToken ct = default)
    {
        OverwriteCalls.Add((batchId, overwrite, excelRowNumbers?.ToArray()));
        if (OnSetOverwrite is not null) await OnSetOverwrite();
        return 1;
    }

    public Task<IReadOnlyDictionary<RowStatus, int>> GetCountsAsync(int batchId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<RowStatus, int>>(new Dictionary<RowStatus, int>(Counts));

    public Task<ConflictCounts> GetConflictCountsAsync(int batchId, CancellationToken ct = default) =>
        Task.FromResult(new ConflictCounts(Counts.GetValueOrDefault(RowStatus.Conflict), OverwriteInDb));

    public Func<RefreshResult> OnRefresh { get; set; } = () => new RefreshResult(true, null, Array.Empty<StatusMove>());
    public int Refreshes { get; private set; }

    public Task<RefreshResult> RefreshAgainstDatabaseAsync(int batchId, CancellationToken ct = default)
    {
        Refreshes++;
        return Task.FromResult(OnRefresh());
    }

    /// <summary>The skipFlaggedDuplicates argument of every commit, in order.</summary>
    public List<bool> CommitSkipFlags { get; } = new();

    /// <summary>What the "database" says about key-less rows flagged as duplicates.</summary>
    public int FlaggedInDb { get; set; }

    public Task<int> GetFlaggedKeylessCountAsync(int batchId, CancellationToken ct = default) => Task.FromResult(FlaggedInDb);

    public Task<CommitResult> CommitAsync(int batchId, bool skipFlaggedDuplicates = false, CancellationToken ct = default)
    {
        Commits.Add(batchId);
        CommitSkipFlags.Add(skipFlaggedDuplicates);
        return OnCommit(batchId);
    }

    public Task<DiscardResult> DiscardAsync(int batchId, CancellationToken ct = default)
    {
        Discards.Add(batchId);
        return OnDiscard(batchId);
    }

    public Task<CleanupResult> CleanupStaleBatchesAsync(CancellationToken ct = default) =>
        Task.FromResult(new CleanupResult(0, 0));

    public static StageResult Staged(int batchId, int @new = 3, int conflict = 2, int invalid = 1, int exact = 4, int fileDup = 5) =>
        new(batchId, Array.Empty<string>(), new Dictionary<RowStatus, int>
        {
            [RowStatus.New] = @new, [RowStatus.Conflict] = conflict, [RowStatus.Invalid] = invalid,
            [RowStatus.ExactDuplicate] = exact, [RowStatus.FileDuplicate] = fileDup,
        }, @new + conflict + invalid + exact + fileDup, 1000, TimeSpan.FromSeconds(1));
}

internal sealed class FakeDialogs : IDialogService
{
    public string? FileToPick { get; set; } = @"C:\data\sample.xlsx";
    public bool ConfirmAnswer { get; set; } = true;
    public DialogChoice ChoiceAnswer { get; set; } = DialogChoice.Yes;

    public List<string> ConfirmMessages { get; } = new();
    public List<string> Infos { get; } = new();
    public List<string> Errors { get; } = new();
    public int Picks { get; private set; }

    public string? SaveToPick { get; set; } = @"C:\out\sample_errors.xlsx";
    public string? SuggestedSaveName { get; private set; }
    public List<string> OpenedFolders { get; } = new();

    public string? PickExcelFile() { Picks++; return FileToPick; }
    public string? PickSavePath(string suggestedName) { SuggestedSaveName = suggestedName; return SaveToPick; }
    public void OpenFolder(string filePath) => OpenedFolders.Add(filePath);
    public ExportTarget? ExportTargetToPick { get; set; }
    public List<(string Name, bool AllowExcel)> ExportPicks { get; } = new();
    public ExportTarget? PickExportPath(string suggestedName, bool allowExcel) { ExportPicks.Add((suggestedName, allowExcel)); return ExportTargetToPick; }
    public bool Confirm(string title, string message) { ConfirmMessages.Add(message); return ConfirmAnswer; }
    public DialogChoice AskYesNoCancel(string title, string message) => ChoiceAnswer;
    public void ShowInfo(string title, string message) => Infos.Add(message);
    public void ShowError(string title, string message) => Errors.Add(message);
}

internal sealed class FakeExporter : IInvalidRowExporter
{
    public Func<string, int, string, CancellationToken, Task<ExportResult>> OnExport { get; set; } =
        (_, _, dest, _) => Task.FromResult(new ExportResult(true, 5, ExportMode.SourceFile, null, dest));
    public List<(string Source, int Batch, string Destination)> Calls { get; } = new();

    public Task<ExportResult> ExportAsync(string sourcePath, int batchId, string destinationPath, IProgress<int>? progress, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add((sourcePath, batchId, destinationPath));
        return OnExport(sourcePath, batchId, destinationPath, ct);
    }
}

public class ImportViewModelTests
{
    private readonly FakeImportPreviewService _service = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeExporter _exporter = new();

    private ImportViewModel Create() => new(_service, _dialogs, _exporter);

    private async Task<ImportViewModel> StagedAsync(int @new = 3, int conflict = 2)
    {
        _service.OnStage = (_, _) => Task.FromResult(FakeImportPreviewService.Staged(7, @new, conflict));
        _service.Counts[RowStatus.New] = @new;
        _service.Counts[RowStatus.Conflict] = conflict;
        var vm = Create();
        await vm.ChooseFileCommand.ExecuteAsync(null);
        Assert.Equal(ImportState.Preview, vm.State);
        return vm;
    }

    [Fact]
    public async Task Choosing_a_file_goes_Idle_to_Staging_to_Preview_with_the_counts()
    {
        var gate = new TaskCompletionSource<StageResult>();
        _service.OnStage = (_, _) => gate.Task;
        var vm = Create();
        Assert.Equal(ImportState.Idle, vm.State);

        var running = vm.ChooseFileCommand.ExecuteAsync(null);
        Assert.Equal(ImportState.Staging, vm.State);

        gate.SetResult(FakeImportPreviewService.Staged(7));
        await running;

        Assert.Equal(ImportState.Preview, vm.State);
        Assert.Equal(7, vm.BatchId);
        Assert.Equal("sample.xlsx", vm.FileName);
        Assert.Equal(15, vm.TotalRows);
        Assert.Equal((3, 2, 1, 4, 5),
            (vm.NewCount, vm.ConflictCount, vm.InvalidCount, vm.ExactDuplicateCount, vm.FileDuplicateCount));
        Assert.Equal(new[] { "New", "Conflicts", "Invalid", "Already in database", "Repeated in file", "Possible duplicates" },
            vm.Tabs.Select(t => t.Title));
        Assert.Equal(@"C:\data\sample.xlsx", Assert.Single(_service.StagedPaths));
    }

    [Fact]
    public async Task Flagged_duplicates_are_imported_unless_the_user_ticks_skip()
    {
        _service.FlaggedInDb = 4;
        var vm = await StagedAsync(@new: 3, conflict: 0);

        // The button says what will happen, and the commit passes the choice on.
        Assert.True(vm.HasFlagged);
        Assert.Equal("Import 3 new rows (+4 flagged duplicates)", vm.CommitButtonText);
        await vm.CommitCommand.ExecuteAsync(null);
        Assert.Equal(new[] { false }, _service.CommitSkipFlags);
        Assert.Contains("insert 4 more rows", _dialogs.ConfirmMessages.Single());

        var again = await StagedAsync(@new: 3, conflict: 0);
        again.SkipFlaggedDuplicates = true;
        Assert.Equal("Import 3 new rows", again.CommitButtonText);
        await again.CommitCommand.ExecuteAsync(null);
        Assert.Equal(new[] { false, true }, _service.CommitSkipFlags);
    }

    [Fact]
    public async Task Cancelling_the_file_dialog_stays_Idle_and_stages_nothing()
    {
        _dialogs.FileToPick = null;
        var vm = Create();

        await vm.ChooseFileCommand.ExecuteAsync(null);

        Assert.Equal(ImportState.Idle, vm.State);
        Assert.Empty(_service.StagedPaths);
    }

    [Fact]
    public async Task File_errors_keep_Idle_show_the_message_and_create_no_batch()
    {
        _service.OnStage = (_, _) => Task.FromResult(new StageResult(null,
            new[] { "Missing header: PayOrderNo" }, new Dictionary<RowStatus, int>(), 0, 0, TimeSpan.Zero));
        var vm = Create();

        await vm.ChooseFileCommand.ExecuteAsync(null);

        Assert.Equal(ImportState.Idle, vm.State);
        Assert.True(vm.HasMessage);
        Assert.Contains("Missing header: PayOrderNo", vm.Message);
        Assert.Null(vm.BatchId);
        Assert.Empty(vm.Tabs);
        Assert.Empty(_service.PageRequests);
        Assert.Empty(_service.Discards);   // nothing was created, so nothing to discard
    }

    [Fact]
    public async Task Cancel_while_staging_returns_to_Idle()
    {
        var started = new TaskCompletionSource();
        CancellationToken seen = default;
        _service.OnStage = async (_, ct) =>
        {
            seen = ct;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);   // runs until cancelled
            return FakeImportPreviewService.Staged(1);
        };
        var vm = Create();

        var running = vm.ChooseFileCommand.ExecuteAsync(null);
        Assert.Equal(ImportState.Staging, vm.State);
        await started.Task;   // the service is now running with its token
        Assert.True(vm.ChooseFileCancelCommand.CanExecute(null));

        vm.ChooseFileCancelCommand.Execute(null);
        await running;

        Assert.Equal(ImportState.Idle, vm.State);
        Assert.Null(vm.BatchId);
        Assert.Contains("cancelled", vm.Message);
        Assert.True(seen.IsCancellationRequested);   // the cancel reached the service, which then discards the partial batch
    }

    [Fact]
    public async Task Commit_asks_for_confirmation_with_the_summary_then_goes_to_Done_with_the_counts()
    {
        var vm = await StagedAsync(@new: 3, conflict: 2);
        _service.OnCommit = _ => Task.FromResult(new CommitResult(true, CommitFailure.None, "ok",
            NewRows: 3, SkippedConflicts: 2, RejectedRows: 1));

        Assert.True(vm.CommitCommand.CanExecute(null));
        await vm.CommitCommand.ExecuteAsync(null);

        var summary = Assert.Single(_dialogs.ConfirmMessages);
        Assert.Contains("insert 3 new rows", summary);
        Assert.Contains("overwrite 0 existing rows", summary);
        Assert.Contains("skip 11 rows", summary);                 // 2 conflicts + 4 already in database + 5 repeated in file
        Assert.Contains("not import 1 invalid rows", summary);
        Assert.Equal(7, Assert.Single(_service.Commits));
        Assert.Equal(ImportState.Done, vm.State);
        Assert.Equal(3, vm.LastCommit!.NewRows);
        Assert.Equal(2, vm.LastCommit.SkippedConflicts);
        Assert.Null(vm.BatchId);

        vm.ImportAnotherCommand.Execute(null);
        Assert.Equal(ImportState.Idle, vm.State);
    }

    [Fact]
    public async Task Commit_failure_keeps_Preview_and_shows_the_message()
    {
        var vm = await StagedAsync();
        _service.OnCommit = _ => Task.FromResult(new CommitResult(false, CommitFailure.LockTimeout,
            "Another import is committing, please try again in a moment."));

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(ImportState.Preview, vm.State);
        Assert.Equal(7, vm.BatchId);
        Assert.Equal("Another import is committing, please try again in a moment.", vm.Message);
        Assert.Null(vm.LastCommit);
    }

    [Fact]
    public async Task Commit_failure_RowsChangedSincePreview_offers_the_recheck()
    {
        var vm = await StagedAsync();
        _service.OnCommit = _ => Task.FromResult(new CommitResult(false, CommitFailure.RowsChangedSincePreview, "Rows changed."));

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(ImportState.Preview, vm.State);
        Assert.Contains("Rows changed.", vm.Message);
        Assert.Contains("Re-check against database", vm.Message);   // offers the re-check
    }

    [Fact]
    public async Task Commit_is_disabled_when_there_are_no_new_rows()
    {
        var vm = await StagedAsync(@new: 0, conflict: 4);

        Assert.False(vm.CommitCommand.CanExecute(null));
        Assert.Equal("Import 0 new rows", vm.CommitButtonText);
    }

    [Fact]
    public async Task Declining_the_commit_confirmation_does_not_commit()
    {
        var vm = await StagedAsync();
        _dialogs.ConfirmAnswer = false;

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Empty(_service.Commits);
        Assert.Equal(ImportState.Preview, vm.State);
    }

    [Fact]
    public async Task Discard_declined_keeps_the_batch_and_Preview()
    {
        var vm = await StagedAsync();
        _dialogs.ConfirmAnswer = false;

        await vm.DiscardCommand.ExecuteAsync(null);

        Assert.Empty(_service.Discards);
        Assert.Equal(ImportState.Preview, vm.State);
        Assert.Equal(7, vm.BatchId);
    }

    [Fact]
    public async Task Discard_confirmed_discards_the_batch_and_returns_to_Idle()
    {
        var vm = await StagedAsync();

        await vm.DiscardCommand.ExecuteAsync(null);

        Assert.Equal(7, Assert.Single(_service.Discards));
        Assert.Equal(ImportState.Idle, vm.State);
        Assert.Null(vm.BatchId);
        Assert.Empty(vm.Tabs);
    }

    [Fact]
    public async Task Discard_refused_by_the_service_shows_its_reason_and_returns_to_Idle()
    {
        var vm = await StagedAsync();
        _service.OnDiscard = _ => Task.FromResult(new DiscardResult(false, "Batch 7 was already committed, so it cannot be discarded."));

        await vm.DiscardCommand.ExecuteAsync(null);

        Assert.Equal("Batch 7 was already committed, so it cannot be discarded.", Assert.Single(_dialogs.Infos));
        Assert.Equal(ImportState.Idle, vm.State);
    }

    [Fact]
    public async Task Paging_requests_the_right_status_page_and_size_of_100()
    {
        var vm = await StagedAsync(@new: 3, conflict: 250);   // 250 conflicts = 3 pages

        // Opening the preview loads page 1 of the New tab.
        await WaitForAsync(() => _service.PageRequests.Count >= 1);
        Assert.Equal((7, RowStatus.New, 1, 100), _service.PageRequests[0]);

        var conflicts = vm.Tabs.Single(t => t.Status == RowStatus.Conflict);
        vm.SelectedTab = conflicts;
        await WaitForAsync(() => conflicts.IsLoaded);
        Assert.Contains((7, RowStatus.Conflict, 1, 100), _service.PageRequests);
        Assert.Equal(3, conflicts.TotalPages);
        Assert.Equal("Page 1 of 3  (250 rows)", conflicts.PageText);
        Assert.False(conflicts.PreviousPageCommand.CanExecute(null));

        await conflicts.NextPageCommand.ExecuteAsync(null);
        Assert.Equal((7, RowStatus.Conflict, 2, 100), _service.PageRequests[^1]);
        Assert.Equal(2, conflicts.PageNumber);

        await conflicts.NextPageCommand.ExecuteAsync(null);
        Assert.Equal((7, RowStatus.Conflict, 3, 100), _service.PageRequests[^1]);
        Assert.False(conflicts.NextPageCommand.CanExecute(null));   // last page

        await conflicts.PreviousPageCommand.ExecuteAsync(null);
        Assert.Equal((7, RowStatus.Conflict, 2, 100), _service.PageRequests[^1]);
    }

    [Fact]
    public async Task Close_with_a_staged_batch_Yes_discards_No_keeps_Cancel_stays()
    {
        var vm = await StagedAsync();

        _dialogs.ChoiceAnswer = DialogChoice.Cancel;
        Assert.False(await vm.ConfirmCloseAsync());
        Assert.Empty(_service.Discards);

        _dialogs.ChoiceAnswer = DialogChoice.No;
        Assert.True(await vm.ConfirmCloseAsync());
        Assert.Empty(_service.Discards);

        _dialogs.ChoiceAnswer = DialogChoice.Yes;
        Assert.True(await vm.ConfirmCloseAsync());
        Assert.Equal(7, Assert.Single(_service.Discards));
    }

    [Fact]
    public async Task Close_when_Idle_just_closes_and_when_Committing_refuses()
    {
        var idle = Create();
        Assert.True(await idle.ConfirmCloseAsync());

        var gate = new TaskCompletionSource<CommitResult>();
        var vm = await StagedAsync();
        _service.OnCommit = _ => gate.Task;
        var committing = vm.CommitCommand.ExecuteAsync(null);
        Assert.Equal(ImportState.Committing, vm.State);

        Assert.False(await vm.ConfirmCloseAsync());
        Assert.Single(_dialogs.Infos);

        gate.SetResult(new CommitResult(true, CommitFailure.None, "ok", NewRows: 3));
        await committing;
    }

    [Fact]
    public async Task A_database_failure_while_staging_shows_a_clear_dialog_and_does_not_crash()
    {
        _service.OnStage = (_, _) => throw new FakeDbException("Cannot open database");
        var vm = Create();

        await vm.ChooseFileCommand.ExecuteAsync(null);   // must not throw

        Assert.Equal(ImportState.Idle, vm.State);
        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("database could not be reached", error);
    }

    [Fact]
    public async Task An_unexpected_failure_while_committing_shows_a_dialog_and_keeps_Preview()
    {
        var vm = await StagedAsync();
        _service.OnCommit = _ => throw new InvalidOperationException("boom");

        await vm.CommitCommand.ExecuteAsync(null);   // must not throw

        Assert.Equal(ImportState.Preview, vm.State);
        Assert.Contains("boom", Assert.Single(_dialogs.Errors));
    }

    [Fact]
    public async Task Conflict_tab_loads_conflict_details_and_selects_the_first_row()
    {
        var vm = await StagedAsync(@new: 3, conflict: 5);
        var conflicts = vm.Tabs.Single(t => t.Status == RowStatus.Conflict);

        vm.SelectedTab = conflicts;
        await WaitForAsync(() => conflicts.IsLoaded);

        var row = Assert.Single(conflicts.Rows);
        Assert.True(row.IsConflict);
        Assert.Equal("Debit", Assert.Single(row.Conflict!.Changes).Label);
        Assert.Same(row, conflicts.SelectedRow);           // the first conflict is opened straight away
        Assert.True(conflicts.ShowsDetails);
        Assert.Contains("What differs", conflicts.Columns.Select(c => c.Header));
        Assert.Contains("differ", conflicts.Hint, StringComparison.OrdinalIgnoreCase);
    }

    // ---- columns picker and server-side sorting ----------------------------------------------------------

    [Fact]
    public async Task Preview_tabs_offer_every_field_in_the_picker_show_the_txn_ref_by_default_and_sort_every_data_column()
    {
        var vm = await StagedAsync(@new: 3, conflict: 5);
        var newTab = vm.Tabs.Single(t => t.Status == RowStatus.New);
        var conflicts = vm.Tabs.Single(t => t.Status == RowStatus.Conflict);

        Assert.Equal(30, newTab.ColumnChoices.Count);                     // all 30 staged fields can be shown
        Assert.True(newTab.ColumnChoices.Single(c => c.Header == "Txn ref").IsVisible);
        Assert.False(newTab.ColumnChoices.Single(c => c.Header == "Narration 5").IsVisible);
        Assert.All(newTab.Columns, c => Assert.NotNull(c.Sort));         // every column of an ordinary tab sorts
        Assert.Equal(new[] { "Pay order no", "Txn ref" }, conflicts.ColumnChoices.Where(c => c.IsVisible).Select(c => c.Header));
        Assert.Equal(new[] { "Changes", "What differs" }, conflicts.Columns.Where(c => c.Sort is null).Select(c => c.Header));

        newTab.ShowAllColumnsCommand.Execute(null);
        Assert.All(newTab.ColumnChoices, c => Assert.True(c.IsVisible));
        newTab.ResetColumnsCommand.Execute(null);
        Assert.False(newTab.ColumnChoices.Single(c => c.Header == "Narration 5").IsVisible);
    }

    [Fact]
    public async Task Sorting_by_a_header_reloads_page_1_from_the_server_and_a_second_click_flips_the_direction()
    {
        var vm = await StagedAsync(@new: 250, conflict: 0);
        var tab = vm.Tabs.Single(t => t.Status == RowStatus.New);
        vm.SelectedTab = tab;
        await WaitForAsync(() => tab.IsLoaded);
        await tab.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, tab.PageNumber);
        _service.Sorts.Clear();

        tab.SortBy(nameof(StagedColumn.BankName));
        await WaitForAsync(() => !tab.IsLoading && tab.PageNumber == 1);
        tab.SortBy(nameof(StagedColumn.BankName));
        await WaitForAsync(() => _service.Sorts.Count == 2 && !tab.IsLoading);
        tab.SortBy("NotAColumn");                                          // ignored
        tab.SortBy(nameof(StagedColumn.Overwrite));                        // not a column of this tab: ignored

        Assert.Equal(new[] { new StagedSort(StagedColumn.BankName, false), new StagedSort(StagedColumn.BankName, true) }, _service.Sorts);
        Assert.Equal(new StagedSort(StagedColumn.BankName, true), tab.Sort);
    }

    // ---- feature 1: conflict overwrite choices -----------------------------------------------------------

    private async Task<(ImportViewModel Vm, StatusTabViewModel Conflicts)> ConflictTabAsync(int @new = 3, int conflict = 5)
    {
        var vm = await StagedAsync(@new, conflict);
        var conflicts = vm.Tabs.Single(t => t.Status == RowStatus.Conflict);
        vm.SelectedTab = conflicts;
        await WaitForAsync(() => conflicts.IsLoaded);
        return (vm, conflicts);
    }

    [Fact]
    public async Task Toggling_a_conflicts_overwrite_saves_it_for_that_one_row_and_reads_the_count_from_the_database()
    {
        var (vm, conflicts) = await ConflictTabAsync();
        var row = conflicts.Rows.Single();
        Assert.False(row.IsOverwrite);

        _service.OverwriteInDb = 1;           // what the database will report after the save
        row.IsOverwrite = true;
        await conflicts.LastUpdate;

        var call = Assert.Single(_service.OverwriteCalls);
        Assert.Equal((7, true), (call.Batch, call.Overwrite));
        Assert.Equal(new[] { row.Row.ExcelRowNumber }, call.Rows);       // only that row, not "all"
        Assert.Equal(1, vm.OverwriteCount);
        Assert.Equal("1 of 5 conflicts will overwrite", conflicts.OverwriteSummary);
    }

    [Fact]
    public async Task Untoggling_saves_false_and_a_failed_save_puts_the_checkbox_back_and_shows_an_error()
    {
        var (vm, conflicts) = await ConflictTabAsync();
        var row = conflicts.Rows.Single();

        _service.OnSetOverwrite = () => throw new InvalidOperationException("save failed");
        row.IsOverwrite = true;
        await conflicts.LastUpdate;

        Assert.False(row.IsOverwrite);                                    // reverted to what is really saved
        Assert.Contains("save failed", Assert.Single(_dialogs.Errors));
        Assert.Equal(0, vm.OverwriteCount);
    }

    [Fact]
    public async Task Overwrite_all_and_Skip_all_use_null_row_numbers_and_reload_the_page()
    {
        var (vm, conflicts) = await ConflictTabAsync(conflict: 250);
        var loadsBefore = _service.PageRequests.Count(r => r.Status == RowStatus.Conflict);

        _service.OverwriteInDb = 250;
        await conflicts.OverwriteAllCommand.ExecuteAsync(null);
        Assert.Equal((7, true, (int[]?)null), _service.OverwriteCalls[^1]);
        Assert.Equal(250, vm.OverwriteCount);                             // from the database, not from the 1 row on screen
        Assert.Equal("250 of 250 conflicts will overwrite", conflicts.OverwriteSummary);

        _service.OverwriteInDb = 0;
        await conflicts.SkipAllCommand.ExecuteAsync(null);
        Assert.Equal((7, false, (int[]?)null), _service.OverwriteCalls[^1]);
        Assert.Equal(0, vm.OverwriteCount);

        Assert.Equal(loadsBefore + 2, _service.PageRequests.Count(r => r.Status == RowStatus.Conflict));   // page re-read each time
    }

    [Theory]
    [InlineData(0, 0, "Import 0 new rows", false)]
    [InlineData(3, 0, "Import 3 new rows", true)]
    [InlineData(0, 2, "Import 0 new, update 2", true)]
    [InlineData(3, 2, "Import 3 new, update 2", true)]
    public async Task Commit_button_text_and_enabling_follow_new_and_overwrite_counts(int @new, int overwrite, string text, bool enabled)
    {
        _service.OverwriteInDb = overwrite;                               // the preview reads M from the database
        var vm = await StagedAsync(@new, conflict: 4);

        Assert.Equal(text, vm.CommitButtonText);
        Assert.Equal(enabled, vm.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Confirmation_lists_insert_overwrite_skip_and_reject_with_the_right_numbers()
    {
        _service.OverwriteInDb = 2;
        var vm = await StagedAsync(@new: 3, conflict: 5);                 // 5 conflicts, 4 exact, 5 repeated, 1 invalid

        await vm.CommitCommand.ExecuteAsync(null);

        var text = Assert.Single(_dialogs.ConfirmMessages);
        Assert.Contains("insert 3 new rows", text);
        Assert.Contains("overwrite 2 existing rows", text);
        Assert.Contains("skip 12 rows", text);                            // (5 - 2) conflicts + 4 + 5
        Assert.Contains("not import 1 invalid rows", text);
    }

    // ---- feature 2: re-check against the database ---------------------------------------------------------

    [Fact]
    public async Task Recheck_reloads_the_counts_and_tabs_and_shows_what_moved()
    {
        var vm = await StagedAsync(@new: 12, conflict: 0);
        _service.Counts[RowStatus.Invalid] = 1;
        _service.OnRefresh = () =>
        {
            // someone else imported meanwhile: 10 New became Already in database, 2 became Conflicts
            _service.Counts[RowStatus.New] = 0;
            _service.Counts[RowStatus.ExactDuplicate] = 14;
            _service.Counts[RowStatus.Conflict] = 2;
            _service.OverwriteInDb = 0;
            return new RefreshResult(true, null, new[]
            {
                new StatusMove(RowStatus.New, RowStatus.ExactDuplicate, 10),
                new StatusMove(RowStatus.New, RowStatus.Conflict, 2),
            });
        };
        vm.SelectedTab = vm.Tabs.Single(t => t.Status == RowStatus.ExactDuplicate);
        var oldTabs = vm.Tabs.ToList();

        Assert.True(vm.RefreshCommand.CanExecute(null));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, _service.Refreshes);
        Assert.Equal((0, 2, 14), (vm.NewCount, vm.ConflictCount, vm.ExactDuplicateCount));
        Assert.Equal("12 rows changed: 10 New → Already in database, 2 New → Conflict.", vm.Message);
        Assert.False(vm.CommitCommand.CanExecute(null));                  // nothing new left, no overwrites chosen
        Assert.NotSame(oldTabs[0], vm.Tabs[0]);                           // the tabs were rebuilt from the new counts
        Assert.Equal(RowStatus.ExactDuplicate, vm.SelectedTab!.Status);   // and the user stays on the tab they were on
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Recheck_with_no_changes_says_so_and_a_refusal_shows_its_reason()
    {
        var vm = await StagedAsync();

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.StartsWith("Nothing changed", vm.Message);

        _service.OnRefresh = () => RefreshResult.Refused("Batch 7 is 'Committed', not 'Staged', so it can no longer be re-checked.");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("'Committed'", vm.Message);
        Assert.Equal(3, vm.NewCount);                                     // counts untouched by a refusal
    }

    // ---- feature 3: export invalid rows ------------------------------------------------------------------

    [Fact]
    public async Task Export_asks_for_a_save_path_named_after_the_file_and_reports_success_with_open_folder()
    {
        var vm = await StagedAsync();
        _exporter.OnExport = (_, _, dest, _) => Task.FromResult(new ExportResult(true, 12, ExportMode.SourceFile, null, dest));

        Assert.True(vm.ExportInvalidCommand.CanExecute(null));
        await vm.ExportInvalidCommand.ExecuteAsync(null);

        Assert.Equal("sample_errors.xlsx", _dialogs.SuggestedSaveName);
        var call = Assert.Single(_exporter.Calls);
        Assert.Equal((@"C:\data\sample.xlsx", 7, @"C:\out\sample_errors.xlsx"), (call.Source, call.Batch, call.Destination));
        Assert.Contains("Exported 12 invalid rows", vm.Message);
        Assert.Equal(@"C:\out\sample_errors.xlsx", Assert.Single(_dialogs.OpenedFolders));
        Assert.False(vm.IsExporting);
        Assert.Equal(ImportState.Preview, vm.State);
    }

    [Fact]
    public async Task Export_failure_shows_an_error_dialog_and_keeps_the_preview()
    {
        var vm = await StagedAsync();
        _exporter.OnExport = (_, _, _, _) => throw new IOException("disk full");

        await vm.ExportInvalidCommand.ExecuteAsync(null);   // must not throw

        Assert.Contains("disk full", Assert.Single(_dialogs.Errors));
        Assert.False(vm.IsExporting);
        Assert.Equal(ImportState.Preview, vm.State);
        Assert.Empty(_dialogs.OpenedFolders);
    }

    [Fact]
    public async Task Export_can_be_cancelled_while_running()
    {
        var vm = await StagedAsync();
        var started = new TaskCompletionSource();
        _exporter.OnExport = async (_, _, dest, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ExportResult(true, 1, ExportMode.SourceFile, null, dest);
        };

        var running = vm.ExportInvalidCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsExporting);
        Assert.False(vm.CommitCommand.CanExecute(null));                  // other actions are blocked meanwhile
        Assert.True(vm.ExportInvalidCancelCommand.CanExecute(null));

        vm.ExportInvalidCancelCommand.Execute(null);
        await running;

        Assert.False(vm.IsExporting);
        Assert.Contains("cancelled", vm.Message);
        Assert.Empty(_dialogs.OpenedFolders);
        Assert.Empty(_dialogs.Errors);
    }

    // Lets the fire-and-forget first page load finish without sleeping a fixed time.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out waiting for the page to load");
    }

    private sealed class FakeDbException : DbException
    {
        public FakeDbException(string message) : base(message) { }
    }
}
