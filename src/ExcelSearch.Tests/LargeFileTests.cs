using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ExcelSearch.Core.Import;
using Xunit.Abstractions;

namespace ExcelSearch.Tests;

/// <summary>[Fact] that is skipped unless the environment variable EXCELSEARCH_RUN_LARGE=1 is set.</summary>
public sealed class LargeFactAttribute : FactAttribute
{
    public const string Variable = "EXCELSEARCH_RUN_LARGE";

    public LargeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Slow. Set {Variable}=1 to run (200,000-row streaming/memory check).";
    }
}

public class LargeFileTests
{
    private const int RowCount = 200_000;
    private readonly ITestOutputHelper _output;

    public LargeFileTests(ITestOutputHelper output) => _output = output;

    [LargeFact]
    public void Parses_200k_rows_streaming_with_low_memory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"excelsearch-large-{Guid.NewGuid():N}.xlsx");
        try
        {
            var gen = Stopwatch.StartNew();
            WriteSyntheticWorkbook(path, RowCount);
            gen.Stop();
            var fileMb = new FileInfo(path).Length / 1024.0 / 1024.0;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var baselineMb = proc.WorkingSet64 / 1024.0 / 1024.0;
            long peakBytes = proc.WorkingSet64;

            var rows = 0;
            var valid = 0;
            var sw = Stopwatch.StartNew();
            using (var file = new ExcelParser().Parse(path, null, CancellationToken.None))
            {
                Assert.Empty(file.FileErrors);
                foreach (var row in file.Rows) // never materialized: each row is dropped after counting
                {
                    rows++;
                    if (row.IsValid) valid++;
                    if (rows % 5000 == 0)
                    {
                        proc.Refresh();
                        peakBytes = Math.Max(peakBytes, proc.WorkingSet64);
                    }
                }
            }
            sw.Stop();
            proc.Refresh();

            var peakMb = peakBytes / 1024.0 / 1024.0;
            _output.WriteLine($"File: {fileMb:F1} MB, generated in {gen.Elapsed.TotalSeconds:F1}s");
            _output.WriteLine($"Parsed {rows:N0} rows ({valid:N0} valid) in {sw.Elapsed.TotalSeconds:F1}s " +
                              $"({rows / sw.Elapsed.TotalSeconds:N0} rows/s)");
            _output.WriteLine($"Working set: baseline {baselineMb:F0} MB, peak during parse (sampled every 5,000 rows) {peakMb:F0} MB, " +
                              $"growth {peakMb - baselineMb:F0} MB; process lifetime peak {proc.PeakWorkingSet64 / 1024.0 / 1024.0:F0} MB");

            Assert.Equal(RowCount, rows);
            Assert.Equal(RowCount, valid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Writes a minimal .xlsx by hand, row by row straight into the zip, so generating the test file
    /// does not itself use much memory (text is stored inline, no shared-strings table).
    /// </summary>
    internal static void WriteSyntheticWorkbook(string path, int rows, string keyPrefix = "")
    {
        string[] headers =
        {
            "Value Date", "Br. Code", "Branch Name", "Customer Name", "CNIC No", "payorder no", "Txn Ref/ Seq No",
            "Debit", "Credit", "Run. Bal", "Bank Name", "Acct no", "Narraction 1",
        };

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        Add(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        Add(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Add(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");

        var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        w.Write("<row r=\"1\">");
        for (var c = 0; c < headers.Length; c++) w.Write(Str(c, 1, headers[c]));
        w.Write("</row>");

        for (var i = 1; i <= rows; i++)
        {
            var r = i + 1;
            w.Write($"<row r=\"{r}\">");
            w.Write(Str(0, r, $"{1 + i % 28:00}/{1 + i % 12:00}/2024"));
            w.Write(Str(1, r, "0" + (i % 90)));
            w.Write(Str(2, r, "Branch " + i % 90));
            w.Write(Str(3, r, "Customer " + i));
            w.Write(Str(4, r, $"{35202:00000}-{1000000 + i % 9000000}-1"));
            w.Write(Str(5, r, keyPrefix + "PO" + i));
            w.Write(Str(6, r, keyPrefix + "TX" + i));
            w.Write(Num(7, r, i % 1000 * 10));
            w.Write(Num(8, r, i % 500 * 100));
            w.Write(Num(9, r, i));
            w.Write(Str(10, r, "Bank " + i % 7));
            w.Write(Str(11, r, "00" + (100000 + i % 5000)));
            w.Write(Str(12, r, "Payment for plot " + i % 400));
            w.Write("</row>");
        }

        w.Write("</sheetData></worksheet>");
    }

    private static void Add(ZipArchive zip, string name, string xml)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(new UTF8Encoding(false).GetBytes(xml));
    }

    private static string Col(int index) => ((char)('A' + index)).ToString();

    private static string Str(int col, int row, string text) =>
        $"<c r=\"{Col(col)}{row}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(text)}</t></is></c>";

    private static string Num(int col, int row, double value) =>
        $"<c r=\"{Col(col)}{row}\"><v>{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>";
}
