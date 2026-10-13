using ExcelSearch.Core.Query;

namespace ExcelSearch.Tests;

/// <summary>The export writer on its own (no database).</summary>
public class TransactionExportWriterTests
{
    [Fact]
    public async Task Writer_exports_only_the_chosen_columns_in_the_given_order_to_xlsx_and_csv()
    {
        var rows = new[]
        {
            FakeQueryService.Rec(1), FakeQueryService.Rec(2) with { CustomerName = "Khan, \"Ali\"", Credit = null },
        };
        async IAsyncEnumerable<IReadOnlyList<TransactionRecord>> Chunks()
        {
            await Task.Yield();
            yield return rows;
        }
        var chosen = new[] { "Credit", "CustomerName", "ValueDate" };                   // not the grid order: the caller's order is kept
        var xlsx = Path.Combine(Path.GetTempPath(), "TEST_" + Guid.NewGuid().ToString("N") + ".xlsx");
        var csv = Path.ChangeExtension(xlsx, ".csv");
        try
        {
            Assert.Equal(2, await TransactionExportWriter.WriteAsync(Chunks(), ExportFormat.Xlsx, xlsx, null, default, chosen));
            using (var wb = new ClosedXML.Excel.XLWorkbook(xlsx))
            {
                var ws = wb.Worksheet(1);
                Assert.Equal(3, ws.LastColumnUsed()!.ColumnNumber());                   // three columns, not 28
                Assert.Equal(new[] { "Credit", "Customer Name", "Value Date" }, Enumerable.Range(1, 3).Select(c => ws.Cell(1, c).GetString()));
                Assert.Equal(20d, ws.Cell(2, 1).GetDouble());
                Assert.Equal("Khan, \"Ali\"", ws.Cell(3, 2).GetString());
                Assert.True(ws.Cell(3, 1).IsEmpty());                                   // the null credit
                Assert.Equal(rows[0].ValueDate.ToDateTime(TimeOnly.MinValue), ws.Cell(2, 3).GetDateTime());
            }

            await TransactionExportWriter.WriteAsync(Chunks(), ExportFormat.Csv, csv, null, default, chosen);
            var lines = QueryServiceContract.ParseCsv(File.ReadAllText(csv, System.Text.Encoding.UTF8).TrimStart('\uFEFF'));
            Assert.Equal(3, lines.Count);
            Assert.All(lines, l => Assert.Equal(3, l.Count));
            Assert.Equal(new[] { "Credit", "Customer Name", "Value Date" }, lines[0]);
            Assert.Equal("Khan, \"Ali\"", lines[2][1]);
            Assert.Equal("", lines[2][0]);
        }
        finally
        {
            File.Delete(xlsx);
            File.Delete(csv);
        }
    }

    [Fact]
    public async Task Writer_null_columns_writes_all_28_and_bad_lists_are_refused_before_any_file_exists()
    {
        async IAsyncEnumerable<IReadOnlyList<TransactionRecord>> Chunks()
        {
            await Task.Yield();
            yield return new[] { FakeQueryService.Rec(1) };
        }
        var path = Path.Combine(Path.GetTempPath(), "TEST_" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            await TransactionExportWriter.WriteAsync(Chunks(), ExportFormat.Csv, path, null, default, null);
            Assert.Equal(28, QueryServiceContract.ParseCsv(File.ReadAllText(path, System.Text.Encoding.UTF8).TrimStart('\uFEFF'))[0].Count);
            Assert.Equal(28, TransactionExportWriter.AllColumns.Count);
            File.Delete(path);

            foreach (var bad in new[] { new string[0], new[] { "NotAColumn" }, new[] { "PayOrderNo", "PayOrderNo" }, new[] { "1; DROP TABLE x" }, new[] { "payorderno" } })
            {
                await Assert.ThrowsAsync<ArgumentException>(() => TransactionExportWriter.WriteAsync(Chunks(), ExportFormat.Csv, path, null, default, bad));
                Assert.False(File.Exists(path));
                Assert.False(File.Exists(path + ".part"));
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".part");
        }
    }
}
