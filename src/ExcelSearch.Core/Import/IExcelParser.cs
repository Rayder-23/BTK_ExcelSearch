namespace ExcelSearch.Core.Import;

public interface IExcelParser
{
    /// <summary>
    /// Reads the workbook at <paramref name="path"/> and returns every data row, normalized and validated.
    /// Problems with individual rows are reported on the row; problems with the file itself in FileErrors.
    /// </summary>
    /// <param name="progress">Receives the number of data rows processed so far (optional).</param>
    ParseResult Parse(string path, IProgress<int>? progress, CancellationToken ct);
}
