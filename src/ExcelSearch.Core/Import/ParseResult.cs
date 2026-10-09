namespace ExcelSearch.Core.Import;

/// <summary>Outcome of parsing one workbook. If FileErrors is non-empty, Rows is empty.</summary>
public sealed class ParseResult
{
    public string FileName { get; set; } = "";

    /// <summary>SHA-256 (lowercase hex) of the file bytes; used for the "already imported" warning.</summary>
    public string FileHash { get; set; } = "";

    public string SheetName { get; set; } = "";

    public List<string> FileErrors { get; } = new();

    public List<ImportRow> Rows { get; } = new();
}
