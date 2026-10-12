using System.Diagnostics;
using ClosedXML.Excel;
using ExcelSearch.Core.Export;
using ExcelSearch.Core.Import;
using Xunit.Abstractions;
using static ExcelSearch.Tests.StagingEnv;

namespace ExcelSearch.Tests;

/// <summary>
/// The exporter together with the real staging store and parser (keyset paging, FileHash, row-number alignment).
/// Same DB rules as the other tests: TEST_ keys, every batch tracked, only the test's own rows deleted.
/// </summary>
public class ExportDbTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excelsearch-exportdb-" + Guid.NewGuid().ToString("N"));

    public ExportDbTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [DbFact]
    public async Task Fallback_export_pages_the_real_staging_table_in_keyset_chunks()
    {
        await using var env = Create();
        var rows = new List<ImportRow>();
        for (var i = 0; i < 25; i++) rows.Add(Row(2 + i * 2, env.Key($"bad{i}"), "T1", i, invalid: true));     // rows 2,4,6...
        for (var i = 0; i < 10; i++) rows.Add(Row(3 + i * 2, env.Key($"ok{i}"), "T1", i));                     // rows 3,5,7... valid
        var id = await env.StageAsync(rows.OrderBy(r => r.ExcelRowNumber));
        var dest = Path.Combine(_dir, "fallback.xlsx");

        var result = await new InvalidRowExporter(env.Store, chunkSize: 7)
            .ExportAsync(Path.Combine(_dir, "no-such-file.xlsx"), id, dest, null);

        Assert.Equal(ExportMode.StagedValues, result.Mode);
        Assert.Equal(25, result.RowsWritten);
        using var wb = new XLWorkbook(dest);
        var ws = wb.Worksheet(1);
        var header = Enumerable.Range(1, 30).Select(c => ws.Cell(1, c).GetString()).Where(h => h != "").ToList();
        var sourceRowCol = header.IndexOf("Source Row") + 1;
        var exported = Enumerable.Range(2, 25).Select(r => (int)ws.Cell(r, sourceRowCol).GetDouble()).ToList();
        Assert.Equal(Enumerable.Range(0, 25).Select(i => 2 + i * 2), exported);                // ascending, none lost across 4 chunks
        Assert.Equal("Debit is not a valid amount", ws.Cell(2, header.IndexOf("Errors") + 1).GetString());
        Assert.Null(ws.Cell(27, 1).Value.IsBlank ? null : "extra row");                        // nothing after the 25th
    }

    [DbFact]
    public async Task Source_export_end_to_end_with_the_real_parser_store_and_file_hash()
    {
        await using var env = Create();
        var source = Path.Combine(_dir, "orders.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            string[] headers = { "Value Date", "Br. Code", "payorder no", "Txn Ref/ Seq No", "Debit" };
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            for (var r = 2; r <= 21; r++)
            {
                if (r % 4 != 0) ws.Cell(r, 1).Value = new DateTime(2024, 4, 1).AddDays(r);      // every 4th row has no date: invalid
                ws.Cell(r, 2).Value = "00" + r;                                                 // text with leading zeros
                ws.Cell(r, 3).Value = env.Key("o" + r);
                ws.Cell(r, 4).Value = "T1";
                ws.Cell(r, 5).Value = r * 10.5;
            }
            wb.SaveAs(source);
        }

        using var file = new ExcelParser().Parse(source, null, CancellationToken.None);
        var id = await env.Store.CreateBatchAsync(file.FileName, file.SheetName, file.FileHash, "TEST\\user");
        env.TrackBatch(id);
        await env.Store.LoadRowsAsync(id, file.Rows, null);
        await env.Store.ClassifyAsync(id);

        var dest = Path.Combine(_dir, "orders_errors.xlsx");
        var result = await new InvalidRowExporter(env.Store, chunkSize: 2).ExportAsync(source, id, dest, null);

        Assert.Equal(ExportMode.SourceFile, result.Mode);
        Assert.Equal(new[] { 4, 8, 12, 16, 20 }.Length, result.RowsWritten);
        using var outWb = new XLWorkbook(dest);
        var o = outWb.Worksheet(1);
        Assert.Equal(new[] { 4, 8, 12, 16, 20 }, Enumerable.Range(2, 5).Select(r => (int)o.Cell(r, 6).GetDouble()));   // "Source Row"
        Assert.Equal("004", o.Cell(2, 2).GetString());                                                                   // leading zeros kept
        Assert.Equal(42d, o.Cell(2, 5).GetDouble());                                                                     // number stays a number
        Assert.Equal("ValueDate is required", o.Cell(2, 7).GetString());                                                 // "Errors"
        Assert.True(o.Cell(2, 1).IsEmpty());
    }

    [LargeDbFact]
    public async Task Exports_100k_invalid_rows_from_a_200k_row_file_and_reports_time_and_memory()
    {
        await using var env = Create();
        var source = Path.Combine(_dir, "big.xlsx");
        LargeFileTests.WriteSyntheticWorkbook(source, 200_000, env.Prefix + "-", invalidEveryOther: true);   // 100,000 rows with no date

        using (var file = new ExcelParser().Parse(source, null, CancellationToken.None))
        {
            var id0 = await env.Store.CreateBatchAsync(file.FileName, file.SheetName, file.FileHash, "TEST\\user");
            env.TrackBatch(id0);
            await env.Store.LoadRowsAsync(id0, file.Rows, null);
            await env.Store.ClassifyAsync(id0);
            var id = id0;

            GC.Collect();
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var baseline = proc.WorkingSet64;
            long peak = baseline;
            var progress = new Progress<int>(_ => { proc.Refresh(); peak = Math.Max(peak, proc.WorkingSet64); });

            var dest = Path.Combine(_dir, "big_errors.xlsx");
            var sw = Stopwatch.StartNew();
            var result = await new InvalidRowExporter(env.Store).ExportAsync(source, id, dest, progress);
            var elapsed = sw.Elapsed;
            proc.Refresh();
            peak = Math.Max(peak, proc.WorkingSet64);

            _output.WriteLine($"Exported {result.RowsWritten:N0} invalid rows ({result.Mode}) in {elapsed.TotalSeconds:F1}s " +
                              $"({result.RowsWritten / elapsed.TotalSeconds:N0} rows/s), file {new FileInfo(dest).Length / 1048576.0:F1} MB");
            _output.WriteLine($"Working set: baseline {baseline / 1048576.0:F0} MB, peak {peak / 1048576.0:F0} MB, growth {(peak - baseline) / 1048576.0:F0} MB");

            Assert.Equal(ExportMode.SourceFile, result.Mode);
            Assert.Equal(100_000, result.RowsWritten);

            // Read it back with the real parser: 100,000 rows, every one still invalid (no date), none lost.
            var parsed = 0;
            var stillInvalid = 0;
            using var back = new ExcelParser().Parse(dest, null, CancellationToken.None);
            Assert.Empty(back.FileErrors);
            foreach (var r in back.Rows)
            {
                parsed++;
                if (!r.IsValid) stillInvalid++;
            }
            _output.WriteLine($"Read back: {parsed:N0} rows, {stillInvalid:N0} invalid");
            Assert.Equal(100_000, parsed);
            Assert.Equal(100_000, stillInvalid);
        }
    }
}
