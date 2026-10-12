using CommunityToolkit.Mvvm.ComponentModel;

namespace ExcelSearch.ViewModels;

/// <summary>One entry in the left navigation list. A null ViewModel means "not built yet" (shown disabled).</summary>
public sealed record NavItem(string Title, object? ViewModel)
{
    public bool IsEnabled => ViewModel is not null;
}

/// <summary>The main window's logic: the navigation list and which screen is showing.</summary>
public partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(ImportViewModel import, QueryViewModel query)
    {
        Import = import;
        Query = query;
        NavItems = new[] { new NavItem("Import", import), new NavItem("Query", query) };
        _selectedNavItem = NavItems[0];
        _currentViewModel = import;
    }

    public ImportViewModel Import { get; }
    public QueryViewModel Query { get; }
    public IReadOnlyList<NavItem> NavItems { get; }

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
