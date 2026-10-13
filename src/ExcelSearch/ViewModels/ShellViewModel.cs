using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExcelSearch.Data;
using ExcelSearch.Services;

namespace ExcelSearch.ViewModels;

/// <summary>
/// One entry in the left navigation list. A null ViewModel means "not available" (shown disabled, with <see cref="DisabledReason"/> as its tooltip).
/// </summary>
public sealed record NavItem(string Title, object? ViewModel, string? DisabledReason = null)
{
    public bool IsEnabled => ViewModel is not null;
}

/// <summary>The main window's logic: the navigation list and which screen is showing.</summary>
public partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(ImportViewModel import, QueryViewModel query, DatabaseOptions database,
        IDialogService dialogs, IAppLifecycle lifecycle)
    {
        _dialogs = dialogs;
        _lifecycle = lifecycle;
        _active = database.IsSqlite ? LocalDb : SqlServer;
        _selectedDatabase = _active;
        Import = import;
        Query = query;

        // The Query screen is only built on SQL Server so far; with the local SQLite database its entry stays disabled.
        var queryItem = database.IsSqlite
            ? new NavItem("Query", null, "Not yet available for Local DB.")
            : new NavItem("Query", query);
        NavItems = new[] { new NavItem("Import", import), queryItem };
        DatabaseLabel = database.IsSqlite ? LocalDb : SqlServer;
        IsLocalDb = database.IsSqlite;
        DatabaseFilePath = database.IsSqlite ? database.ResolveSqlitePath() : "";
        _selectedNavItem = NavItems[0];
        _currentViewModel = import;
    }

    public const string SqlServer = "SQL Server";
    public const string LocalDb = "Local DB";

    private readonly IDialogService _dialogs;
    private readonly IAppLifecycle _lifecycle;
    private readonly string _active;

    /// <summary>The choices in the database selector. "Local DB" is the SQLite file; "SQL Server" is the server in appsettings.</summary>
    public IReadOnlyList<string> DatabaseChoices { get; } = new[] { SqlServer, LocalDb };

    [ObservableProperty] private string _selectedDatabase;

    /// <summary>Set when the user has already agreed to leave (a restart), so the window closes without asking again.</summary>
    public bool CloseApproved { get; private set; }

    partial void OnSelectedDatabaseChanged(string value)
    {
        if (value == _active) return;
        if (!_dialogs.Confirm("ExcelSearch", $"Switch to {value}? The app has to restart to do that.\n\n" +
                $"Your data stays where it is: {SqlServer} and {LocalDb} are two separate databases."))
        {
            SelectedDatabase = _active;
            return;
        }
        _ = SwitchAsync(value);
    }

    private async Task SwitchAsync(string choice)
    {
        // Same questions as closing the window (a staged file, a running import) before we restart.
        if (!await ConfirmCloseAsync())
        {
            SelectedDatabase = _active;
            return;
        }
        try
        {
            UserSettings.SaveProvider(choice == LocalDb ? DatabaseProvider.Sqlite : DatabaseProvider.SqlServer);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("ExcelSearch", "The choice could not be saved: " + ex.Message);
            SelectedDatabase = _active;
            return;
        }
        CloseApproved = true;
        _lifecycle.Restart();
    }

    public ImportViewModel Import { get; }
    public QueryViewModel Query { get; }
    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Which database the app is running on, shown at the bottom of the navigation rail.</summary>
    public string DatabaseLabel { get; }

    /// <summary>True when running on Local DB (drives the colour of the database card).</summary>
    public bool IsLocalDb { get; }

    /// <summary>The SQLite file (empty on SQL Server); shown as the tooltip of the file button.</summary>
    public string DatabaseFilePath { get; }

    /// <summary>Opens the folder that holds the Local DB file, with the file selected.</summary>
    [RelayCommand]
    private void OpenDatabaseFolder()
    {
        if (DatabaseFilePath.Length > 0) _dialogs.OpenFolder(DatabaseFilePath);
    }

    [ObservableProperty] private NavItem _selectedNavItem;

    /// <summary>What the window's ContentControl shows; a DataTemplate in MainWindow maps it to a View.</summary>
    [ObservableProperty] private object _currentViewModel;

    partial void OnSelectedNavItemChanged(NavItem value)
    {
        if (value.ViewModel is null) return;
        CurrentViewModel = value.ViewModel;

        // The Query screen refreshes its dropdown lists whenever it is shown after an import (a no-op otherwise).
        if (value.ViewModel is QueryViewModel q) _ = q.OnShownAsync();
    }

    public Task<bool> ConfirmCloseAsync() => Import.ConfirmCloseAsync();
}
