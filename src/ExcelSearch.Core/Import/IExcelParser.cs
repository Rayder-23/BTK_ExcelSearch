namespace ExcelSearch.Core.Import;

public interface IExcelParser
{
    /// <summary>
    /// Opens the .xlsx at <paramref name="path"/>, hashes it, finds the header row, and returns a
    /// <see cref="ParsedFile"/> whose rows are produced lazily, one at a time, so memory stays flat for huge files.
    /// Problems with individual rows are reported on the row; problems with the file itself in FileErrors.
    /// </summary>
    /// <param name="progress">Receives the number of data rows read so far, while Rows is enumerated (optional).</param>
    /// <param name="ct">Checked while hashing and on every row; throws OperationCanceledException.</param>
    ParsedFile Parse(string path, IProgress<int>? progress, CancellationToken ct);
}
