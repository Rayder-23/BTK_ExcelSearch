namespace ExcelSearch.Core.Import;

/// <summary>One validation problem on a row, tied to the field (C# property name) that caused it.</summary>
public sealed record RowError(string Field, string Message);
