using System.Security.Cryptography;
using ClosedXML.Excel;

namespace ExcelSearch.Core.Import;

/// <summary>ClosedXML implementation of <see cref="IExcelParser"/>.</summary>
public sealed class ExcelParser : IExcelParser
{
    private const int HeaderScanRows = 10;

    public ParseResult Parse(string path, IProgress<int>? progress, CancellationToken ct)
    {
        var result = new ParseResult { FileName = Path.GetFileName(path) };

        // Read the bytes once (FileShare.ReadWrite so a workbook open in Excel can still be read),
        // hash them, then load the workbook from memory.
        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.FileErrors.Add($"Cannot read file: {ex.Message}");
            return result;
        }

        result.FileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            result.FileErrors.Add($"Not a readable .xlsx workbook: {ex.Message}");
            return result;
        }

        using (workbook)
        {
            ct.ThrowIfCancellationRequested();

            if (workbook.Worksheets.Count == 0)
            {
                result.FileErrors.Add("The workbook has no worksheets.");
                return result;
            }

            // Use the first sheet that has a header row; if none does, report against the first sheet.
            IXLWorksheet? sheet = null;
            Dictionary<string, int>? columns = null;
            int headerRow = 0;
            foreach (var ws in workbook.Worksheets)
            {
                if (TryFindHeader(ws, out headerRow, out columns, out _))
                {
                    sheet = ws;
                    break;
                }
            }

            if (sheet is null || columns is null)
            {
                var first = workbook.Worksheets.First();
                result.SheetName = first.Name;
                TryFindHeader(first, out _, out _, out var missing);
                result.FileErrors.Add("Required header(s) not found in the first " + HeaderScanRows + " rows: " +
                                      string.Join(", ", missing));
                return result;
            }

            result.SheetName = sheet.Name;
            ReadRows(sheet, headerRow, columns, result, progress, ct);
        }

        MarkFileDuplicates(result.Rows);
        return result;
    }

    /// <summary>
    /// Scans the first rows for one containing all required headers. When none does, <paramref name="missing"/>
    /// lists the required headers absent from the best candidate row.
    /// </summary>
    private static bool TryFindHeader(IXLWorksheet ws, out int headerRow, out Dictionary<string, int>? columns,
        out List<string> missing)
    {
        headerRow = 0;
        columns = null;
        missing = HeaderAliases.Required.Select(r => r.Display).ToList();

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var bestFound = -1;

        for (var r = 1; r <= Math.Min(HeaderScanRows, lastRow); r++)
        {
            var map = new Dictionary<string, int>();
            var row = ws.Row(r);
            var lastCol = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (var c = 1; c <= lastCol; c++)
            {
                var field = HeaderAliases.Resolve(row.Cell(c).GetString());
                if (field is not null) map.TryAdd(field, c); // first column wins if a header repeats
            }

            var found = HeaderAliases.Required.Count(req => map.ContainsKey(req.Field));
            if (found == HeaderAliases.Required.Length)
            {
                headerRow = r;
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

    private static void ReadRows(IXLWorksheet sheet, int headerRow, Dictionary<string, int> columns,
        ParseResult result, IProgress<int>? progress, CancellationToken ct)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow;
        var processed = 0;

        for (var r = headerRow + 1; r <= lastRow; r++)
        {
            ct.ThrowIfCancellationRequested();

            var raw = new Dictionary<string, object?>();
            foreach (var (field, col) in columns)
            {
                var cell = sheet.Cell(r, col);
                raw[field] = ReadCell(cell, out var cellError);
                if (cellError is not null) raw[field] = new CellErrorMarker(cellError);
            }

            // Completely blank rows (every mapped cell empty) are skipped silently.
            if (raw.Values.All(v => v is null || (v is string s && string.IsNullOrWhiteSpace(s)))) continue;

            result.Rows.Add(BuildRow(r, raw));
            progress?.Report(++processed);
        }
    }

    /// <summary>Reads a cell by value. Formula cells give their calculated result.</summary>
    private static object? ReadCell(IXLCell cell, out string? error)
    {
        error = null;
        var v = cell.Value;
        if (v.IsBlank) return null;
        if (v.IsError)
        {
            error = $"cell contains an Excel error ({v})";
            return null;
        }
        if (v.IsText) return v.GetText();
        if (v.IsNumber) return v.GetNumber();
        if (v.IsDateTime) return v.GetDateTime();
        if (v.IsBoolean) return v.GetBoolean();
        return v.ToString();
    }

    private sealed record CellErrorMarker(string Message);

    private static ImportRow BuildRow(int excelRow, Dictionary<string, object?> raw)
    {
        var row = new ImportRow { ExcelRowNumber = excelRow };

        void Text(string field, Action<string?> set)
        {
            if (!raw.TryGetValue(field, out var v)) return;
            if (v is CellErrorMarker m) { row.Errors.Add(new RowError(field, m.Message)); return; }
            set(FieldNormalizer.Text(v));
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

        if (raw.TryGetValue(nameof(ImportRow.Cnic), out var cnic))
        {
            if (cnic is CellErrorMarker m) row.Errors.Add(new RowError(nameof(ImportRow.Cnic), m.Message));
            else row.Cnic = FieldNormalizer.Cnic(cnic);
        }

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

    /// <summary>
    /// Rows sharing (PayOrderNo, TxnRefSeqNo): identical hashes mean the later rows are harmless duplicates;
    /// differing hashes mean a conflict, reported on every row of the group. Keys compare case-insensitively
    /// because the database collation does.
    /// </summary>
    private static void MarkFileDuplicates(List<ImportRow> rows)
    {
        var groups = rows
            .Where(r => r.PayOrderNo is not null && r.TxnRefSeqNo is not null)
            .GroupBy(r => (r.PayOrderNo!.ToUpperInvariant(), r.TxnRefSeqNo!.ToUpperInvariant()))
            .Where(g => g.Count() > 1);

        foreach (var g in groups)
        {
            var members = g.OrderBy(r => r.ExcelRowNumber).ToList();
            if (members.Select(r => r.RowHash).Distinct().Count() == 1)
            {
                foreach (var later in members.Skip(1)) later.IsFileDuplicate = true;
                continue;
            }

            foreach (var r in members)
            {
                var others = string.Join(", ", members.Where(o => o != r).Select(o => o.ExcelRowNumber));
                r.Errors.Add(new RowError(nameof(ImportRow.PayOrderNo),
                    $"Same PayOrderNo + TxnRefSeqNo as row(s) {others} in this file, but the values differ"));
            }
        }
    }
}
