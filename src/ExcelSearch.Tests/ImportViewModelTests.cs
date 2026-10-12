using System.Data.Common;
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

    public Task<PagedRows> GetPageAsync(int batchId, RowStatus status, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        lock (PageRequests) PageRequests.Add((batchId, status, pageNumber, pageSize));
        var rows = new[] { new StagedRow { ExcelRowNumber = pageNumber, Status = status } };
        return Task.FromResult(new PagedRows(rows, Counts.GetValueOrDefault(status)));
    }

    /// <summary>Totals the paging tests rely on (GetPageAsync reports these as the status total).</summary>
    public Dictionary<RowStatus, int> Counts { get; } = new();

    /// <summary>Conflict pages are recorded with the other page requests so paging tests treat all tabs alike.</summary>
    public Func<int, ConflictRow> ConflictFor { get; set; } = n => new ConflictRow(
        new StagedRow { ExcelRowNumber = n, Status = RowStatus.Conflict, PayOrderNo = "PO", TxnRefSeqNo = "T" },
        new[] { new FieldChange("Debit", "1.00", "2.00", IsAmount: true) });

    public Task<PagedConflicts> GetConflictPageAsync(int batchId, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        lock (PageRequests) PageRequests.Add((batchId, RowStatus.Conflict, pageNumber, pageSize));
        return Task.FromResult(new PagedConflicts(new[] { ConflictFor(pageNumber) }, Counts.GetValueOrDefault(RowStatus.Conflict)));
    }

    public Task<int> SetConflictOverwriteAsync(int batchId, bool overwrite, IReadOnlyCollection<int>? excelRowNumbers = null, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<CommitResult> CommitAsync(int batchId, CancellationToken ct = default)
    {
        Commits.Add(batchId);
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

    public string? PickExcelFile() { Picks++; return FileToPick; }
    public bool Confirm(string title, string message) { ConfirmMessages.Add(message); return ConfirmAnswer; }
    public DialogChoice AskYesNoCancel(string title, string message) => ChoiceAnswer;
    public void ShowInfo(string title, string message) => Infos.Add(message);
    public void ShowError(string title, string message) => Errors.Add(message);
}

public class ImportViewModelTests
{
    private readonly FakeImportPreviewService _service = new();
    private readonly FakeDialogs _dialogs = new();

    private ImportViewModel Create() => new(_service, _dialogs);

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
        Assert.Equal(new[] { "New", "Conflicts", "Invalid", "Already in database", "Repeated in file" },
            vm.Tabs.Select(t => t.Title));
        Assert.Equal(@"C:\data\sample.xlsx", Assert.Single(_service.StagedPaths));
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
        Assert.Contains("3 new rows", summary);
        Assert.Contains("2 conflicts will be skipped", summary);
        Assert.Contains("1 invalid rows will not be imported", summary);
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
    public async Task Commit_failure_RowsChangedSincePreview_tells_the_user_to_start_over()
    {
        var vm = await StagedAsync();
        _service.OnCommit = _ => Task.FromResult(new CommitResult(false, CommitFailure.RowsChangedSincePreview, "Rows changed."));

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(ImportState.Preview, vm.State);
        Assert.Contains("Rows changed.", vm.Message);
        Assert.Contains("Start over", vm.Message);
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
    public async Task Start_over_discards_then_opens_the_file_chooser_again()
    {
        var vm = await StagedAsync();
        Assert.Equal(1, _dialogs.Picks);

        await vm.StartOverCommand.ExecuteAsync(null);

        Assert.Equal(7, Assert.Single(_service.Discards));
        Assert.Equal(2, _dialogs.Picks);
        Assert.Equal(2, _service.StagedPaths.Count);
        Assert.Equal(ImportState.Preview, vm.State);   // the fake stages the new file straight away
    }

    [Fact]
    public async Task Start_over_declined_changes_nothing()
    {
        var vm = await StagedAsync();
        _dialogs.ConfirmAnswer = false;

        await vm.StartOverCommand.ExecuteAsync(null);

        Assert.Empty(_service.Discards);
        Assert.Equal(1, _dialogs.Picks);
        Assert.Equal(ImportState.Preview, vm.State);
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

    [Fact]
    public async Task Other_tabs_have_no_details_panel_and_put_row_number_first()
    {
        var vm = await StagedAsync();

        Assert.All(vm.Tabs, t => Assert.Equal("Row", t.Columns[0].Header));
        Assert.All(vm.Tabs.Where(t => t.Status != RowStatus.Conflict), t => Assert.False(t.ShowsDetails));
        var invalid = vm.Tabs.Single(t => t.Status == RowStatus.Invalid);
        Assert.Equal("Error", invalid.Columns[1].Header);   // ErrorText right after the row number
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
