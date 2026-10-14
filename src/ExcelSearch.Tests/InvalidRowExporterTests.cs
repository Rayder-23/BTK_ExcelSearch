using System.Security.Cryptography;
using ClosedXML.Excel;
using ExcelSearch.Core.Export;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;

namespace ExcelSearch.Tests;

/// <summary>An in-memory stand-in for the staging table's Invalid rows, with the same keyset-paging contract.</summary>
internal sealed class FakeInvalidSource : IInvalidRowSource
{
    public BatchInfo? Batch { get; set; }
    public List<InvalidRowError> Errors { get; } = new();
    public List<StagedRow> Staged { get; } = new();
    public Action<int>? OnFetch { get; set; }
    public int Fetches { get; private set; }

    public Task<BatchInfo?> GetBatchAsync(int batchId, CancellationToken ct = default) => Task.FromResult(Batch);

    public Task<IReadOnlyList<InvalidRowError>> GetInvalidErrorsAfterAsync(int batchId, int afterRow, int take, CancellationToken ct = default)
    {
        Fetches++;
        OnFetch?.Invoke(Fetches);
        return Task.FromResult<IReadOnlyList<InvalidRowError>>(
            Errors.Where(e => e.ExcelRowNumber > afterRow).OrderBy(e => e.ExcelRowNumber).Take(take).ToList());
    }

    public Task<IReadOnlyList<StagedRow>> GetInvalidRowsAfterAsync(int batchId, int afterRow, int take, CancellationToken ct = default)
    {
        Fetches++;
        OnFetch?.Invoke(Fetches);
        return Task.FromResult<IReadOnlyList<StagedRow>>(
            Staged.Where(e => e.ExcelRowNumber > afterRow).OrderBy(e => e.ExcelRowNumber).Take(take).ToList());
    }
}

public class InvalidRowExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excelsearch-export-" + Guid.NewGuid().ToString("N"));

    public InvalidRowExporterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static BatchInfo BatchFor(string path) => new(1, Path.GetFileName(path), "Sheet1", Sha(path), "Staged");

    /// <summary>
    /// Rows 2..6: 2 and 4 are valid, 3 / 5 / 6 are the ones marked invalid by the tests.
    /// Row 3 has a number and text with leading zeros; row 5 has a real date; row 6 has a text value in a number column.
    /// </summary>
    private string SourceWorkbook()
    {
        var path = P("source.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        string[] headers = { "Value Date", "Br. Code", "payorder no", "Txn Ref/ Seq No", "Debit", "Customer Name" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        void Row(int r, DateTime? date, string branch, string? po, string tx, XLCellValue debit, string customer)
        {
            if (date is not null)
            {
                ws.Cell(r, 1).Value = date.Value;
                ws.Cell(r, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            ws.Cell(r, 2).Value = branch;
            if (po is not null) ws.Cell(r, 3).Value = po;
            ws.Cell(r, 4).Value = tx;
            ws.Cell(r, 5).Value = debit;
            ws.Cell(r, 6).Value = customer;
        }

        Row(2, new DateTime(2024, 4, 3), "007", "PO1", "T1", 1250.5, "Ahmed");
        Row(3, null, "0042", "PO2", "T1", 99, "Sara");                       // invalid: no date
        Row(4, new DateTime(2024, 4, 4), "008", "PO3", "T1", 5, "Bilal");
        Row(5, new DateTime(2024, 5, 1), "000123", null, "T2", 12.25, "Hina"); // invalid: no pay order
        Row(6, new DateTime(2024, 5, 2), "009", "PO6", "T3", "abc", "Kamran"); // invalid: Debit is text
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public async Task Source_mode_writes_header_original_values_extra_columns_and_keeps_order()
    {
        var src = SourceWorkbook();
        var fake = new FakeInvalidSource { Batch = BatchFor(src) };
        fake.Errors.Add(new(5, "PayOrderNo is required"));      // deliberately out of order: the source returns them sorted
        fake.Errors.Add(new(3, "ValueDate is required"));
        fake.Errors.Add(new(6, "'abc' is not a valid amount"));
        var dest = P("out.xlsx");

        var result = await new InvalidRowExporter(fake).ExportAsync(src, 1, dest, null);

        Assert.True(result.Success);
        Assert.Equal(ExportMode.SourceFile, result.Mode);
        Assert.Null(result.Note);
        Assert.Equal(3, result.RowsWritten);

        using var wb = new XLWorkbook(dest);
        var ws = wb.Worksheet(1);
        Assert.Equal(new[] { "Value Date", "Br. Code", "payorder no", "Txn Ref/ Seq No", "Debit", "Customer Name", "Source Row", "Errors" },
            Enumerable.Range(1, 8).Select(c => ws.Cell(1, c).GetString()));

        // Order: ascending by source row.
        Assert.Equal(new[] { 3, 5, 6 }, new[] { 2, 3, 4 }.Select(r => (int)ws.Cell(r, 7).GetDouble()));
        Assert.Equal("ValueDate is required", ws.Cell(2, 8).GetString());
        Assert.Equal("PayOrderNo is required", ws.Cell(3, 8).GetString());

        // Row 3: number stays a number, leading zeros survive as text, the empty date stays empty.
        Assert.True(ws.Cell(2, 1).IsEmpty());
        Assert.Equal(XLDataType.Text, ws.Cell(2, 2).DataType);
        Assert.Equal("0042", ws.Cell(2, 2).GetString());
        Assert.Equal(XLDataType.Number, ws.Cell(2, 5).DataType);
        Assert.Equal(99d, ws.Cell(2, 5).GetDouble());

        // Row 5: a real date stays a date and keeps a date format; "000123" stays text; the missing key stays empty.
        Assert.Equal(XLDataType.DateTime, ws.Cell(3, 1).DataType);
        Assert.Equal(new DateTime(2024, 5, 1), ws.Cell(3, 1).GetDateTime());
        // A date number format: a built-in date id (14-22) or a custom format string that contains a day part.
        var fmt = ws.Cell(3, 1).Style.NumberFormat;
        Assert.True(fmt.NumberFormatId is >= 14 and <= 22 || fmt.Format.Contains('d'), $"not a date format: id {fmt.NumberFormatId} \"{fmt.Format}\"");
        Assert.Equal("000123", ws.Cell(3, 2).GetString());
        Assert.True(ws.Cell(3, 3).IsEmpty());
        Assert.Equal(12.25, ws.Cell(3, 5).GetDouble());

        // Row 6: the bad text is kept exactly as typed.
        Assert.Equal("abc", ws.Cell(4, 5).GetString());
    }

    [Fact]
    public async Task Rows_are_fetched_in_several_keyset_chunks_and_all_are_written_in_order()
    {
        var src = SourceWorkbook();
        var fake = new FakeInvalidSource { Batch = BatchFor(src) };
        fake.Errors.AddRange(new[] { 3, 5, 6 }.Select(n => new InvalidRowError(n, "e" + n)));

        var result = await new InvalidRowExporter(fake, chunkSize: 2).ExportAsync(src, 1, P("out.xlsx"), null);

        Assert.Equal(3, result.RowsWritten);
        Assert.True(fake.Fetches >= 2, "with a chunk size of 2 and 3 rows the source must be asked more than once");
        using var wb = new XLWorkbook(P("out.xlsx"));
        Assert.Equal(new[] { "e3", "e5", "e6" }, new[] { 2, 3, 4 }.Select(r => wb.Worksheet(1).Cell(r, 8).GetString()));
    }

    [Fact]
    public async Task Round_trip_the_invalid_rows_of_sample_02_parse_the_same_then_parse_valid_once_fixed()
    {
        var sample = SampleFilesTests.SamplePathFor("02_invalid_rows.xlsx");
        List<ImportRow> parsed;
        using (var file = new ExcelParser().Parse(sample, null, CancellationToken.None))
            parsed = file.Rows.ToList();
        var invalid = parsed.Where(r => !r.IsValid).ToList();
        Assert.Equal(11, invalid.Count);

        var fake = new FakeInvalidSource { Batch = BatchFor(sample) };
        fake.Errors.AddRange(invalid.Select(r => new InvalidRowError(r.ExcelRowNumber, string.Join("; ", r.Errors.Select(e => e.Message)))));
        var dest = P("errors.xlsx");

        var result = await new InvalidRowExporter(fake).ExportAsync(sample, 1, dest, null);
        Assert.Equal(ExportMode.SourceFile, result.Mode);
        Assert.Equal(11, result.RowsWritten);

        // The extra columns ("Source Row", "Errors") must be ignored by the real parser; the same errors must come back.
        List<ImportRow> reparsed;
        using (var file = new ExcelParser().Parse(dest, null, CancellationToken.None))
        {
            Assert.Empty(file.FileErrors);
            reparsed = file.Rows.ToList();
        }
        Assert.Equal(invalid.Count, reparsed.Count);
        Assert.All(reparsed, r => Assert.False(r.IsValid));
        // Same errors, except an Excel error value (#DIV/0!) comes back as that text in an amount column.
        static string Msgs(ImportRow r) => string.Join("|", r.Errors.Select(e => e.Message.Replace("cell contains an Excel error (#DIV/0!)", "\u0027#DIV/0!\u0027 is not a valid amount")));
        Assert.Equal(invalid.Select(Msgs), reparsed.Select(Msgs));

        // Fix the exported file the way a user would, then everything must parse as valid.
        using (var wb = new XLWorkbook(dest))
        {
            var ws = wb.Worksheet(1);
            var header = Enumerable.Range(1, ws.LastColumnUsed()!.ColumnNumber()).ToDictionary(c => ws.Cell(1, c).GetString(), c => c);
            for (var r = 2; r <= ws.LastRowUsed()!.RowNumber(); r++)
            {
                ws.Cell(r, header["payorder no"]).Value = "FIX-" + r;
                ws.Cell(r, header["Txn Ref/ Seq No"]).Value = "T1";
                ws.Cell(r, header["Value Date"]).Value = new DateTime(2024, 6, 1);
                ws.Cell(r, header["Debit"]).Value = 10;
                ws.Cell(r, header["Credit"]).Value = 0;
                ws.Cell(r, header["Run. Bal"]).Value = 100;
                ws.Cell(r, header["Customer Name"]).Value = "Fixed Customer";
                ws.Cell(r, header["Br. Code"]).Value = "B1";
            }
            wb.Save();
        }

        List<ImportRow> fixedRows;
        using (var file = new ExcelParser().Parse(dest, null, CancellationToken.None))
            fixedRows = file.Rows.ToList();
        Assert.Equal(11, fixedRows.Count);
        Assert.All(fixedRows, r => Assert.True(r.IsValid, string.Join("; ", r.Errors.Select(e => e.Message))));
    }

    [Fact]
    public async Task Missing_source_file_falls_back_to_staged_values_and_says_so()
    {
        var fake = new FakeInvalidSource { Batch = new BatchInfo(1, "gone.xlsx", "Sheet1", new string('a', 64), "Staged") };
        fake.Staged.Add(new StagedRow
        {
            ExcelRowNumber = 7, ErrorText = "ValueDate is required", PayOrderNo = "PO7", TxnRefSeqNo = "T7",
            Debit = 12.5m, CustomerName = "Sara", BranchCode = "0042",
        });
        var dest = P("fallback.xlsx");

        var result = await new InvalidRowExporter(fake).ExportAsync(P("does-not-exist.xlsx"), 1, dest, null);

        Assert.Equal(ExportMode.StagedValues, result.Mode);
        Assert.Contains("was not found", result.Note);
        Assert.Equal(1, result.RowsWritten);

        using var wb = new XLWorkbook(dest);
        var ws = wb.Worksheet(1);
        var header = Enumerable.Range(1, 30).Select(c => ws.Cell(1, c).GetString()).Where(h => h != "").ToList();
        Assert.Equal("Source Row", header[^2]);
        Assert.Equal("Errors", header[^1]);
        Assert.Equal(7d, ws.Cell(2, header.IndexOf("Source Row") + 1).GetDouble());
        Assert.Equal("ValueDate is required", ws.Cell(2, header.IndexOf("Errors") + 1).GetString());
        Assert.Equal(12.5, ws.Cell(2, header.IndexOf("Debit") + 1).GetDouble());
        Assert.Equal("PO7", ws.Cell(2, header.IndexOf("payorder no") + 1).GetString());
        Assert.Equal("0042", ws.Cell(2, header.IndexOf("Br. Code") + 1).GetString());
    }

    [Fact]
    public async Task Changed_source_file_falls_back_to_staged_values()
    {
        var src = SourceWorkbook();
        var fake = new FakeInvalidSource { Batch = BatchFor(src) };       // hash of the file as imported
        fake.Staged.Add(new StagedRow { ExcelRowNumber = 3, ErrorText = "ValueDate is required", PayOrderNo = "PO2", TxnRefSeqNo = "T1" });
        using (var wb = new XLWorkbook(src))                                // the user edits the file afterwards
        {
            wb.Worksheet(1).Cell(3, 6).Value = "Edited";
            wb.Save();
        }

        var result = await new InvalidRowExporter(fake).ExportAsync(src, 1, P("out.xlsx"), null);

        Assert.Equal(ExportMode.StagedValues, result.Mode);
        Assert.Contains("has changed", result.Note);
        Assert.Equal(1, result.RowsWritten);
    }

    [Fact]
    public async Task No_invalid_rows_writes_no_file()
    {
        var src = SourceWorkbook();
        var fake = new FakeInvalidSource { Batch = BatchFor(src) };
        var dest = P("none.xlsx");

        var result = await new InvalidRowExporter(fake).ExportAsync(src, 1, dest, null);

        Assert.True(result.Success);
        Assert.Equal(0, result.RowsWritten);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Cancelling_part_way_leaves_no_file_behind()
    {
        var src = SourceWorkbook();
        using var cts = new CancellationTokenSource();
        var fake = new FakeInvalidSource { Batch = BatchFor(src) };
        fake.Errors.AddRange(new[] { 3, 5, 6 }.Select(n => new InvalidRowError(n, "e")));
        fake.OnFetch = n => { if (n == 2) cts.Cancel(); };          // cancel when the second chunk is requested
        var dest = P("cancelled.xlsx");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new InvalidRowExporter(fake, chunkSize: 2).ExportAsync(src, 1, dest, null, cts.Token));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }
}
