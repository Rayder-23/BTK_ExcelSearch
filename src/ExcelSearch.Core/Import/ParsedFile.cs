namespace ExcelSearch.Core.Import;

/// <summary>
/// A workbook opened for streaming. FileName, FileHash, SheetName and FileErrors are known as soon as
/// the parser returns; <see cref="Rows"/> is read lazily from the file as you enumerate it.
/// Rows can be enumerated ONCE (it is a forward-only stream). Dispose this object when done so the
/// file is released even if you stop enumerating early. If FileErrors is non-empty, Rows is empty.
/// </summary>
public sealed class ParsedFile : IDisposable
{
    private readonly IDisposable? _resource;
    private IEnumerable<ImportRow> _rows = Array.Empty<ImportRow>();

    internal ParsedFile(IDisposable? resource = null) => _resource = resource;

    public string FileName { get; internal set; } = "";

    /// <summary>SHA-256 (lowercase hex) of the file bytes, computed by streaming the file.</summary>
    public string FileHash { get; internal set; } = "";

    public string SheetName { get; internal set; } = "";

    public List<string> FileErrors { get; } = new();

    public IEnumerable<ImportRow> Rows
    {
        get => _rows;
        internal set => _rows = value;
    }

    public void Dispose() => _resource?.Dispose();
}
