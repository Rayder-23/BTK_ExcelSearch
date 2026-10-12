using ClosedXML.Excel;
using ExcelSearch.Core.Export;

namespace ExcelSearch.Tests;

public class StreamingXlsxWriterTests
{
    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Column_letters(int index, string expected) => Assert.Equal(expected, StreamingXlsxWriter.ColumnName(index));

    [Fact]
    public void Escape_encodes_markup_and_drops_characters_xml_cannot_hold()
    {
        Assert.Equal("a &amp; b &lt; c &gt; &quot;q&quot; &apos;x&apos;", StreamingXlsxWriter.Escape("a & b < c > \"q\" 'x'"));
        Assert.Equal("ab", StreamingXlsxWriter.Escape("a\u0001\u0008b"));          // control characters are removed
        Assert.Equal("a\tb\nc\rd", StreamingXlsxWriter.Escape("a\tb\nc\rd"));      // tab, LF and CR are legal
        Assert.Equal("plain", StreamingXlsxWriter.Escape("plain"));
    }

    [Fact]
    public void Writes_a_valid_workbook_that_keeps_cell_types_text_and_special_characters()
    {
        using var ms = new MemoryStream();
        var when = new DateTime(2024, 5, 1, 13, 30, 0);
        using (var w = new StreamingXlsxWriter(ms, "Invalid & <rows>"))
        {
            w.WriteHeader(new[] { "Text", "Number", "Date", "Bool", "Empty", "Zeros" });
            w.WriteRow(new object?[] { "a & b < c \"q\"", 12.5, when, true, null, "007" });
            w.WriteRow(new object?[] { "line1\nline2", 7, new DateTime(2024, 1, 2), false, null, "000123" });
            w.WriteRow(new object?[] { "  padded  ", 3.25m, null, null, null, "0" });
            w.WriteRow(new object?[] { "日本語 😀 ünï", -4, null, null, "x", "" });
            w.WriteRow(new object?[] { "bad\u0001chars", 0, null, null, null, null });
            w.WriteRow(new object?[] { new string('z', 40_000), 1, null, null, null, null });
            w.Complete();
        }

        ms.Position = 0;
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet(1);
        Assert.Equal("Invalid & <rows>", ws.Name);
        Assert.Equal(new[] { "Text", "Number", "Date", "Bool", "Empty", "Zeros" }, Enumerable.Range(1, 6).Select(c => ws.Cell(1, c).GetString()));
        Assert.True(ws.Cell(1, 1).Style.Font.Bold);

        Assert.Equal("a & b < c \"q\"", ws.Cell(2, 1).GetString());
        Assert.Equal((XLDataType.Number, 12.5), (ws.Cell(2, 2).DataType, ws.Cell(2, 2).GetDouble()));
        Assert.Equal(XLDataType.DateTime, ws.Cell(2, 3).DataType);
        Assert.Equal(when, ws.Cell(2, 3).GetDateTime());
        Assert.Contains("d", ws.Cell(2, 3).Style.NumberFormat.Format);               // a real date format
        Assert.True(ws.Cell(2, 4).GetBoolean());
        Assert.True(ws.Cell(2, 5).IsEmpty());
        Assert.Equal((XLDataType.Text, "007"), (ws.Cell(2, 6).DataType, ws.Cell(2, 6).GetString()));

        Assert.Equal("line1\nline2", ws.Cell(3, 1).GetString().Replace("\r\n", "\n"));
        Assert.Equal("000123", ws.Cell(3, 6).GetString());
        Assert.False(ws.Cell(3, 4).GetBoolean());
        Assert.Equal("  padded  ", ws.Cell(4, 1).GetString());                        // spaces are preserved
        Assert.Equal(3.25, ws.Cell(4, 2).GetDouble());
        Assert.Equal("日本語 😀 ünï", ws.Cell(5, 1).GetString());
        Assert.Equal(-4d, ws.Cell(5, 2).GetDouble());
        Assert.Equal("badchars", ws.Cell(6, 1).GetString());
        Assert.Equal(32_767, ws.Cell(7, 1).GetString().Length);                      // Excel's per-cell limit
    }

    [Fact]
    public void Output_is_readable_by_the_import_reader_too()
    {
        var path = Path.Combine(Path.GetTempPath(), "writer-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            using (var fs = File.Create(path))
            using (var w = new StreamingXlsxWriter(fs, "Sheet1"))
            {
                w.WriteHeader(new[] { "Value Date", "payorder no", "Txn Ref/ Seq No", "Debit" });
                w.WriteRow(new object?[] { new DateTime(2024, 4, 3), "PO1", "007", 12.5 });
                w.Complete();
            }

            using var file = new ExcelSearch.Core.Import.ExcelParser().Parse(path, null, CancellationToken.None);
            Assert.Empty(file.FileErrors);
            var row = Assert.Single(file.Rows);
            Assert.True(row.IsValid, string.Join("; ", row.Errors.Select(e => e.Message)));
            Assert.Equal(new DateTime(2024, 4, 3), row.ValueDate);
            Assert.Equal("007", row.TxnRefSeqNo);
            Assert.Equal(12.5m, row.Debit);
        }
        finally { File.Delete(path); }
    }
}
