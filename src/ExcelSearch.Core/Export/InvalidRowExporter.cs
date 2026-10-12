using System.Globalization;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;

namespace ExcelSearch.Core.Export;

/// <summary>
/// Exports a batch's Invalid rows with their ORIGINAL cell values plus "Source Row" and "Errors" columns.
///
/// Memory stays flat for any file size: the invalid row numbers arrive in keyset-paged chunks (default 10,000),
/// the source workbook is streamed once with ExcelDataReader, and the two are advanced in lockstep (a merge join on
/// the Excel row number). Each row is written straight to the output by a forward-only xlsx writer.
/// </summary>
public sealed class InvalidRowExporter : IInvalidRowExporter
{
    public const int DefaultChunkSize = 10_000;
    private const int ProgressEvery = 500;
    private const string SourceRowHeader = "Source Row";
    private const string ErrorsHeader = "Errors";
    private const string SheetName = "Invalid rows";

    // Headers used when falling back to staged (parsed) values; the same spellings the importer recognises,
    // so a fixed file can be imported again either way.
    private static readonly (string Header, Func<StagedRow, object?> Get)[] StagedColumns =
    {
        ("Value Date", r => r.ValueDate), ("Br. Code", r => r.BranchCode), ("Branch Name", r => r.BranchName),
        ("Reg No", r => r.RegNo), ("Application No", r => r.ApplicationNo), ("Plot No", r => r.PlotNo),
        ("Street No", r => r.StreetNo), ("Challan No", r => r.ChallanNo), ("Customer Name", r => r.CustomerName),
        ("Cheque/Inst No", r => r.ChequeInstNo), ("CNIC No", r => r.Cnic), ("payorder no", r => r.PayOrderNo),
        ("Txn Ref/ Seq No", r => r.TxnRefSeqNo), ("Discount", r => r.Discount),
        ("Down Payment Amount", r => r.DownPaymentAmount), ("Debit", r => r.Debit), ("Credit", r => r.Credit),
        ("Run. Bal", r => r.RunningBalance), ("Bank Name", r => r.BankName), ("Acct no", r => r.AccountNo),
        ("Project", r => r.Project), ("Dealer Name", r => r.DealerName), ("Name of Data", r => r.DataSource),
        ("Narration 1", r => r.Narration1), ("Narration 2", r => r.Narration2), ("Narration 3", r => r.Narration3),
        ("Narration 4", r => r.Narration4), ("Narration 5", r => r.Narration5),
    };

    private readonly IInvalidRowSource _source;
    private readonly int _chunkSize;

    public InvalidRowExporter(IInvalidRowSource source, int chunkSize = DefaultChunkSize)
    {
        _source = source;
        _chunkSize = chunkSize > 0 ? chunkSize : DefaultChunkSize;
    }

    public async Task<ExportResult> ExportAsync(string sourcePath, int batchId, string destinationPath,
        IProgress<int>? progress, CancellationToken ct = default)
    {
        var batch = await _source.GetBatchAsync(batchId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Batch {batchId} does not exist.");

        // The file on disk may have been edited or replaced since it was imported; row numbers would then point at
        // different rows. Only trust it if it is still byte-for-byte the file that was staged.
        var (mode, note) = await Task.Run(() => ChooseMode(sourcePath, batch.FileHash, ct), ct).ConfigureAwait(false);

        var temp = destinationPath + ".part";
        try
        {
            var written = await Task.Run(() => Write(batchId, sourcePath, temp, mode, progress, ct), ct).ConfigureAwait(false);
            if (written == 0)
                return ExportResult.Nothing("There were no invalid rows, so no file was written.");

            File.Move(temp, destinationPath, overwrite: true);
            return new ExportResult(true, written, mode, note, destinationPath);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort: do not leave a half-written file behind */ }
            throw;
        }
    }

    private static (ExportMode Mode, string? Note) ChooseMode(string sourcePath, string expectedHash, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))
            return (ExportMode.StagedValues,
                $"The original file \"{Path.GetFileName(sourcePath)}\" was not found, so the values read when it was imported were exported.");

        var actual = ExcelParser.HashFile(sourcePath, ct);
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)
            ? (ExportMode.SourceFile, null)
            : (ExportMode.StagedValues,
                $"The original file \"{Path.GetFileName(sourcePath)}\" has changed since it was imported, so the values read at import time were exported.");
    }

    private int Write(int batchId, string sourcePath, string temp, ExportMode mode, IProgress<int>? progress, CancellationToken ct)
    {
        if (File.Exists(temp)) File.Delete(temp);

        if (mode == ExportMode.StagedValues)
        {
            var headers = StagedColumns.Select(c => c.Header).Append(SourceRowHeader).Append(ErrorsHeader).ToArray();
            return Emit(temp, headers, StagedRows(batchId, ct), progress);
        }

        using var sheet = ExcelParser.OpenAtHeader(sourcePath, ct)
                          ?? throw new InvalidOperationException("The original file has no recognisable header row.");
        var keys = HeaderTexts(sheet);
        // ExcelDataReader returns null for cells holding an Excel error (#DIV/0! ...): write that text back so the error is not lost.
        var errorCells = ErrorCellScanner.Find(sourcePath, sheet.Reader.Name);
        return Emit(temp, keys.Append(SourceRowHeader).Append(ErrorsHeader).ToArray(),
            SourceRows(batchId, sheet, keys.Length, errorCells, ct), progress);
    }

    /// <summary>
    /// Writes the rows as they are produced. The file is only created when the first row arrives, so "no invalid rows"
    /// leaves nothing behind. Returns the number of data rows written.
    /// </summary>
    private static int Emit(string path, string[] headers, IEnumerable<object?[]> rows, IProgress<int>? progress)
    {
        FileStream? fs = null;
        StreamingXlsxWriter? writer = null;
        var count = 0;
        try
        {
            foreach (var row in rows)
            {
                if (writer is null)
                {
                    fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    writer = new StreamingXlsxWriter(fs, SheetName);
                    writer.WriteHeader(headers);
                }
                writer.WriteRow(row);
                count++;
                if (count % ProgressEvery == 0) progress?.Report(count);
            }
            writer?.Complete();
        }
        finally
        {
            writer?.Dispose();
            fs?.Dispose();
        }
        progress?.Report(count);
        return count;
    }

    // ---- source-file mode -------------------------------------------------------------------------------------

    /// <summary>The header row's own text (trailing empty columns dropped). Blank headers get "Column N"; repeated ones a suffix.</summary>
    private static string[] HeaderTexts(SourceSheet sheet)
    {
        var reader = sheet.Reader;
        string? Text(int c) => Convert.ToString(reader.GetValue(c), CultureInfo.InvariantCulture)?.Trim();

        var last = -1;
        for (var c = 0; c < reader.FieldCount; c++)
            if (!string.IsNullOrEmpty(Text(c))) last = c;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SourceRowHeader, ErrorsHeader };
        var headers = new string[last + 1];
        for (var c = 0; c <= last; c++)
        {
            var baseName = string.IsNullOrEmpty(Text(c)) ? $"Column {c + 1}" : Text(c)!;
            var name = baseName;
            for (var n = 2; !used.Add(name); n++) name = $"{baseName} ({n})";
            headers[c] = name;
        }
        return headers;
    }

    private IEnumerable<object?[]> SourceRows(int batchId, SourceSheet sheet, int columns,
        IReadOnlyDictionary<(int Row, int Col), string> errorCells, CancellationToken ct)
    {
        var reader = sheet.Reader;
        var rowNumber = sheet.HeaderRow;   // the reader is sitting on the header row

        foreach (var invalid in InvalidErrors(batchId, ct))
        {
            ct.ThrowIfCancellationRequested();

            // Advance the source to the invalid row (a merge join: both sides are in ascending row order).
            while (rowNumber < invalid.ExcelRowNumber)
            {
                if (!reader.Read()) yield break;
                rowNumber++;
            }
            if (rowNumber != invalid.ExcelRowNumber) continue;

            var row = new object?[columns + 2];
            for (var c = 0; c < columns; c++)
            {
                var value = c < reader.FieldCount ? Original(reader.GetValue(c)) : null;
                if (value is null && errorCells.Count > 0 && errorCells.TryGetValue((invalid.ExcelRowNumber, c), out var errorText))
                    value = errorText;
                row[c] = value;
            }
            row[columns] = invalid.ExcelRowNumber;
            row[columns + 1] = invalid.ErrorText;
            yield return row;
        }
    }

    /// <summary>Keeps numbers numbers, dates dates and text text. (Strings stay strings, so leading zeros survive.)</summary>
    private static object? Original(object? v) => v switch
    {
        null => null,
        string s => s,
        double or DateTime or bool => v,
        int or long or short or byte or float or decimal => Convert.ToDouble(v, CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    private IEnumerable<InvalidRowError> InvalidErrors(int batchId, CancellationToken ct)
    {
        // This iterator runs on a thread-pool thread (ExportAsync uses Task.Run), so waiting on the async store is safe.
        var after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = _source.GetInvalidErrorsAfterAsync(batchId, after, _chunkSize, ct).GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            foreach (var r in chunk) yield return r;
            if (chunk.Count < _chunkSize) yield break;
            after = chunk[^1].ExcelRowNumber;
        }
    }

    // ---- fallback mode: values parsed at import time ----------------------------------------------------------

    private IEnumerable<object?[]> StagedRows(int batchId, CancellationToken ct)
    {
        var after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = _source.GetInvalidRowsAfterAsync(batchId, after, _chunkSize, ct).GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            foreach (var r in chunk)
            {
                var row = new object?[StagedColumns.Length + 2];
                for (var c = 0; c < StagedColumns.Length; c++) row[c] = StagedColumns[c].Get(r);
                row[StagedColumns.Length] = r.ExcelRowNumber;
                row[StagedColumns.Length + 1] = r.ErrorText;
                yield return row;
            }
            if (chunk.Count < _chunkSize) yield break;
            after = chunk[^1].ExcelRowNumber;
        }
    }
}
