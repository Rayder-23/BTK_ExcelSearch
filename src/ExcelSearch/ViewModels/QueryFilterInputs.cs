using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExcelSearch.Core.Query;

namespace ExcelSearch.ViewModels;

/// <summary>One text filter box on the Query screen: the typed text plus the "Exact" switch next to it.</summary>
public sealed partial class TextFilterInput : ObservableObject
{
    public TextFilterInput(string label) => Label = label;

    public string Label { get; }

    [ObservableProperty] private string _text = "";

    /// <summary>Off (default) = "starts with"; on = the whole value must match.</summary>
    [ObservableProperty] private bool _exact;

    public TextCriterion Criterion => new(Text, Exact ? TextMatchMode.Exact : TextMatchMode.StartsWith);

    public void Clear()
    {
        Text = "";
        Exact = false;
    }
}

/// <summary>One tickable entry in a multi-select dropdown.</summary>
public sealed partial class SelectableOption : ObservableObject
{
    public SelectableOption(string value, bool isSelected = false)
    {
        Value = value;
        _isSelected = isSelected;
    }

    public string Value { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// A multi-select dropdown (bank, project, dealer...): the list of values, which of them are ticked, a search box to
/// narrow a long list, and the one-line summary shown on the closed dropdown.
/// </summary>
public sealed partial class MultiSelectFilter : ObservableObject
{
    private readonly List<SelectableOption> _all = new();

    public MultiSelectFilter(string label, string anyText)
    {
        Label = label;
        AnyText = anyText;
        _visibleOptions = Array.Empty<SelectableOption>();
    }

    public string Label { get; }
    public string AnyText { get; }

    /// <summary>The options that match the search box (all of them when it is empty).</summary>
    [ObservableProperty] private IReadOnlyList<SelectableOption> _visibleOptions;

    [ObservableProperty] private string _searchText = "";

    /// <summary>False until the first list has arrived (or after it could not be loaded).</summary>
    [ObservableProperty] private bool _isLoaded;

    public IReadOnlyList<string> SelectedValues => _all.Where(o => o.IsSelected).Select(o => o.Value).ToArray();

    /// <summary>"Any bank", the single chosen value, or "3 selected".</summary>
    public string Summary => SelectedValues.Count switch
    {
        0 => AnyText,
        1 => SelectedValues[0],
        var n => $"{n} selected",
    };

    public bool HasSelection => SelectedValues.Count > 0;

    partial void OnSearchTextChanged(string value) => RefreshVisible();

    /// <summary>
    /// Replaces the list. Values that were ticked stay ticked, including ones no longer in the list (so a refresh in the
    /// background never silently changes what the user is about to search for).
    /// </summary>
    public void SetOptions(IEnumerable<string> values)
    {
        var ticked = new HashSet<string>(SelectedValues, StringComparer.OrdinalIgnoreCase);
        var list = values.Concat(ticked).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase);

        foreach (var old in _all) old.PropertyChanged -= OnOptionChanged;
        _all.Clear();
        foreach (var v in list)
        {
            var option = new SelectableOption(v, ticked.Contains(v));
            option.PropertyChanged += OnOptionChanged;
            _all.Add(option);
        }
        IsLoaded = true;
        RefreshVisible();
        RaiseSelectionChanged();
    }

    [RelayCommand]
    public void Clear()
    {
        foreach (var o in _all) o.IsSelected = false;
        SearchText = "";
    }

    private void OnOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableOption.IsSelected)) RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void RefreshVisible()
    {
        var text = SearchText.Trim();
        VisibleOptions = text.Length == 0
            ? _all.ToArray()
            : _all.Where(o => o.Value.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }
}

/// <summary>One entry of the "Columns" picker: a grid column and whether it is currently shown.</summary>
public sealed partial class ColumnChoice : ObservableObject
{
    public ColumnChoice(QueryColumn column, bool isVisible)
    {
        Column = column;
        _isVisible = isVisible;
    }

    public QueryColumn Column { get; }
    public string Header => Column.Header;

    [ObservableProperty] private bool _isVisible;
}
