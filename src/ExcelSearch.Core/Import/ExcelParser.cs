using System.Security.Cryptography;
using System.Text;
using ExcelDataReader;

namespace ExcelSearch.Core.Import;

/// <summary>
/// ExcelDataReader implementation of <see cref="IExcelParser"/> (.xlsx only). ExcelDataReader is a forward-only
/// reader, so a file with hundreds of thousands of rows is never held in memory: each row is read, normalized,
/// validated and handed to the caller, then dropped.
/// </summary>
public sealed class ExcelParser : IExcelParser
{
    private const int HeaderScanRows = 10;
    private const int ProgressEvery = 500;

    static ExcelParser()
    {
        // ExcelDataReader looks up code page 1252 even for .xlsx; .NET only knows it after this registration.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ParsedFile Parse(string path, IProgress<int>? progress, CancellationToken ct)
    {
        FileStream? stream = null;
        IExcelDataReader? reader = null;
        var file = new ParsedFile { FileName = Path.GetFileName(path) };

        try
        {
            // Pass 1: hash the file by streaming it through SHA-256 (never loaded whole).
            file.FileHash = HashFile(path, ct);

            // Pass 2: open the same file for reading. FileShare.ReadWrite so a workbook open in Excel still works.
            stream = OpenRead(path);
            reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reader?.Dispose();
            stream?.Dispose();
            file.FileErrors.Add($"Cannot read file: {ex.Message}");
            return file;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            reader?.Dispose();
            stream?.Dispose();
            file.FileErrors.Add($"Not a readable .xlsx workbook: {ex.Message}");
            return file;
        }

        var resource = new Disposables(reader, stream);
        var result = new ParsedFile(resource)
        {
            FileName = file.FileName,
            FileHash = file.FileHash,
        };

        try
        {
            // Use the first sheet that has a header row; if none does, report against the first sheet.
            List<string>? missingOnFirstSheet = null;
            string? firstSheetName = null;
            do
            {
                ct.ThrowIfCancellationRequested();
                firstSheetName ??= reader.Name;

                if (TryFindHeader(reader, out var headerRow, out var columns, out var missing))
                {
                    result.SheetName = reader.Name;
                    result.Rows = StreamRows(reader, resource, columns!, headerRow, progress, ct);
                    return result;
                }

                missingOnFirstSheet ??= missing;
            } while (reader.NextResult());

            result.SheetName = firstSheetName ?? "";
            result.FileErrors.Add("Required header(s) not found in the first " + HeaderScanRows + " rows: " +
                                  string.Join(", ", missingOnFirstSheet ?? HeaderAliases.Required.Select(r => r.Display).ToList()));
            resource.Dispose();
            return result;
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 81920, FileOptions.SequentialScan);

    private static string HashFile(string path, CancellationToken ct)
    {
        using var fs = OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Reads up to the first 10 rows of the current sheet looking for one that holds all required headers.
    /// On success the reader is positioned ON the header row. On failure <paramref name="missing"/> lists the
    /// required headers absent from the best candidate row.
    /// </summary>
    private static bool TryFindHeader(IExcelDataReader reader, out int headerRow, out Dictionary<string, int>? columns,
        out List<string> missing)
    {
        headerRow = 0;
        columns = null;
        missing = HeaderAliases.Required.Select(r => r.Display).ToList();
        var bestFound = -1;

        var rowNumber = 0;
        while (rowNumber < HeaderScanRows && reader.Read())
        {
            rowNumber++;
            var map = new Dictionary<string, int>();
            for (var c = 0; c < reader.FieldCount; c++)
            {
                var cell = reader.GetValue(c);
                if (cell is null) continue;
                var field = HeaderAliases.Resolve(Convert.ToString(cell, System.Globalization.CultureInfo.InvariantCulture));
                if (field is not null) map.TryAdd(field, c); // first column wins if a header repeats
            }

            var found = HeaderAliases.Required.Count(req => map.ContainsKey(req.Field));
            if (found == HeaderAliases.Required.Length)
            {
                headerRow = rowNumber;
                columns = map;
                missing = new List<string>();
                return true;
            }

            if (found > bestFound)
            {
                bestFound = found;
                missing = HeaderAliases.Required.Where(req => !map.ContainsKey(req.Field)).Select(req => req.Display).ToList();
            }
        }

        return false;
    }

    /// <summary>Lazy row stream. The finally block releases the file when enumeration ends, fails or is abandoned.</summary>
    private static IEnumerable<ImportRow> StreamRows(IExcelDataReader reader, IDisposable resource,
        Dictionary<string, int> columns, int headerRow, IProgress<int>? progress, CancellationToken ct)
    {
        try
        {
            var rowNumber = headerRow;
            var processed = 0;

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rowNumber++;

                var raw = new Dictionary<string, object?>(columns.Count);
                var blank = true;
                foreach (var (field, col) in columns)
                {
                    var value = col < reader.FieldCount ? ReadValue(reader.GetValue(col)) : null;
                    if (value is string s && string.IsNullOrWhiteSpace(s)) value = null;
                    if (value is not null) blank = false;
                    raw[field] = value;
                }

                // Completely blank rows (every mapped cell empty) are skipped silently.
                if (blank) continue;

                processed++;
                if (processed % ProgressEvery == 0) progress?.Report(processed);
                yield return BuildRow(rowNumber, raw);
            }

            progress?.Report(processed);
        }
        finally
        {
            resource.Dispose();
        }
    }

    private sealed record CellErrorMarker(string Message);

    /// <summary>Maps what ExcelDataReader returns to null, string, double, DateTime, bool or an error marker.</summary>
    private static object? ReadValue(object? v) => v switch
    {
        null => null,
        string or double or DateTime or bool => v,
        int or long or short or byte or float or decimal => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture),
        _ when v.GetType().Name.Contains("Error", StringComparison.OrdinalIgnoreCase) =>
            new CellErrorMarker($"cell contains an Excel error ({v})"),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static ImportRow BuildRow(int excelRow, Dictionary<string, object?> raw)
    {
        var row = new ImportRow { ExcelRowNumber = excelRow };

        // Normalize to trimmed text, cut to the column size (too long = error, truncated value kept).
        void Text(string field, Action<string?> set)
        {
            if (!raw.TryGetValue(field, out var v)) return;
            if (v is CellErrorMarker m) { row.Errors.Add(new RowError(field, m.Message)); return; }
            var text = field == nameof(ImportRow.Cnic) ? FieldNormalizer.Cnic(v) : FieldNormalizer.Text(v);
            set(FieldLimits.Apply(field, text, out var tooLong));
            if (tooLong is not null) row.Errors.Add(new RowError(field, tooLong));
        }

        void Amount(string field, Action<decimal?> set)
        {
            if (!raw.TryGetValue(field, out var v)) return;
            if (v is CellErrorMarker m) { row.Errors.Add(new RowError(field, m.Message)); return; }
            if (FieldNormalizer.TryAmount(v, out var value, out var error)) set(value);
            else row.Errors.Add(new RowError(field, error!));
        }

        Text(nameof(ImportRow.BranchCode), v => row.BranchCode = v);
        Text(nameof(ImportRow.BranchName), v => row.BranchName = v);
        Text(nameof(ImportRow.RegNo), v => row.RegNo = v);
        Text(nameof(ImportRow.ApplicationNo), v => row.ApplicationNo = v);
        Text(nameof(ImportRow.PlotNo), v => row.PlotNo = v);
        Text(nameof(ImportRow.StreetNo), v => row.StreetNo = v);
        Text(nameof(ImportRow.ChallanNo), v => row.ChallanNo = v);
        Text(nameof(ImportRow.ChequeInstNo), v => row.ChequeInstNo = v);
        Text(nameof(ImportRow.CustomerName), v => row.CustomerName = v);
        Text(nameof(ImportRow.Cnic), v => row.Cnic = v);
        Text(nameof(ImportRow.PayOrderNo), v => row.PayOrderNo = v);
        Text(nameof(ImportRow.TxnRefSeqNo), v => row.TxnRefSeqNo = v);
        Text(nameof(ImportRow.BankName), v => row.BankName = v);
        Text(nameof(ImportRow.AccountNo), v => row.AccountNo = v);
        Text(nameof(ImportRow.Project), v => row.Project = v);
        Text(nameof(ImportRow.DealerName), v => row.DealerName = v);
        Text(nameof(ImportRow.DataSource), v => row.DataSource = v);
        Text(nameof(ImportRow.Narration1), v => row.Narration1 = v);
        Text(nameof(ImportRow.Narration2), v => row.Narration2 = v);
        Text(nameof(ImportRow.Narration3), v => row.Narration3 = v);
        Text(nameof(ImportRow.Narration4), v => row.Narration4 = v);
        Text(nameof(ImportRow.Narration5), v => row.Narration5 = v);

        Amount(nameof(ImportRow.Discount), v => row.Discount = v);
        Amount(nameof(ImportRow.DownPaymentAmount), v => row.DownPaymentAmount = v);
        Amount(nameof(ImportRow.Debit), v => row.Debit = v);
        Amount(nameof(ImportRow.Credit), v => row.Credit = v);
        Amount(nameof(ImportRow.RunningBalance), v => row.RunningBalance = v);

        var dateFailed = false;
        if (raw.TryGetValue(nameof(ImportRow.ValueDate), out var date))
        {
            if (date is CellErrorMarker m)
            {
                row.Errors.Add(new RowError(nameof(ImportRow.ValueDate), m.Message));
                dateFailed = true;
            }
            else if (FieldNormalizer.TryDate(date, out var d, out var error)) row.ValueDate = d;
            else
            {
                row.Errors.Add(new RowError(nameof(ImportRow.ValueDate), error!));
                dateFailed = true;
            }
        }

        // Required fields: one error per missing field (a date that failed to parse already has its own error).
        if (row.PayOrderNo is null)
            row.Errors.Add(new RowError(nameof(ImportRow.PayOrderNo), "PayOrderNo is required"));
        if (row.TxnRefSeqNo is null)
            row.Errors.Add(new RowError(nameof(ImportRow.TxnRefSeqNo), "TxnRefSeqNo is required"));
        if (row.ValueDate is null && !dateFailed)
            row.Errors.Add(new RowError(nameof(ImportRow.ValueDate), "ValueDate is required"));

        row.RowHash = RowHasher.Compute(row);
        return row;
    }

    private sealed class Disposables : IDisposable
    {
        private readonly IDisposable[] _items;
        public Disposables(params IDisposable[] items) => _items = items;
        public void Dispose()
        {
            foreach (var item in _items) item.Dispose();
        }
    }
}
