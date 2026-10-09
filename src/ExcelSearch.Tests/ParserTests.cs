using ClosedXML.Excel;
using ExcelSearch.Core.Import;

namespace ExcelSearch.Tests;

public class HeaderAliasTests
{
    [Theory]
    [InlineData("Value Date", "ValueDate")]
    [InlineData("Br. Code", "BranchCode")]
    [InlineData("Txn Ref/ Seq No", "TxnRefSeqNo")]
    [InlineData("payorder no", "PayOrderNo")]
    [InlineData("Cheque Inst. No.", "ChequeInstNo")]
    [InlineData("CNIC No", "Cnic")]
    [InlineData("Run. Bal", "RunningBalance")]
    [InlineData("Acct no", "AccountNo")]
    [InlineData("Name of Data", "DataSource")]
    [InlineData("Dealer Name", "DealerName")]
    [InlineData("Delear Name", "DealerName")]      // typo in source files
    [InlineData("Narration 1", "Narration1")]
    [InlineData("Narraction 1", "Narration1")]     // typo in source files
    [InlineData("Narration 5", "Narration5")]
    public void Resolves_header_to_field(string header, string field)
        => Assert.Equal(field, HeaderAliases.Resolve(header));

    [Fact]
    public void Unknown_header_is_ignored()
        => Assert.Null(HeaderAliases.Resolve("Some Extra Column"));
}

public class NormalizerTests
{
    [Fact]
    public void Identifier_numbers_render_as_plain_digits()
    {
        Assert.Equal("1234567890123", FieldNormalizer.Text(1234567890123d));
        Assert.Equal("42", FieldNormalizer.Text(42.0d));        // no ".0"
        Assert.Equal("5000000000000", FieldNormalizer.Text(5e12)); // no "5E+12"
        Assert.Equal("00123", FieldNormalizer.Text("00123"));    // leading zeros survive in text
        Assert.Equal("AB 1", FieldNormalizer.Text("  AB 1  "));
        Assert.Null(FieldNormalizer.Text("   "));
        Assert.Null(FieldNormalizer.Text(null));
    }

    [Fact]
    public void Cnic_keeps_digits_only()
    {
        Assert.Equal("3520212345671", FieldNormalizer.Cnic("35202-1234567-1"));
        Assert.Equal("3520212345671", FieldNormalizer.Cnic(3520212345671d));
        Assert.Null(FieldNormalizer.Cnic("n/a"));
    }

    [Theory]
    [InlineData("1,234,567.891", "1234567.89")]
    [InlineData("  500 ", "500.00")]
    [InlineData("-12.345", "-12.35")]
    public void Amount_text_is_parsed_and_rounded(string input, string expected)
    {
        Assert.True(FieldNormalizer.TryAmount(input, out var v, out _));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), v);
    }

    [Fact]
    public void Amount_number_is_rounded_to_two_places()
    {
        Assert.True(FieldNormalizer.TryAmount(10.126d, out var v, out _));
        Assert.Equal(10.13m, v);
    }

    [Fact]
    public void Amount_blank_is_null_and_junk_is_error()
    {
        Assert.True(FieldNormalizer.TryAmount("", out var blank, out _));
        Assert.Null(blank);
        Assert.False(FieldNormalizer.TryAmount("abc", out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("03/04/2024", 2024, 4, 3)]   // day-first: 3 April, never March 4
    [InlineData("3-4-2024", 2024, 4, 3)]
    [InlineData("2024-04-03", 2024, 4, 3)]
    [InlineData("03-Apr-2024", 2024, 4, 3)]
    public void Date_text_uses_explicit_day_first_formats(string input, int y, int m, int d)
    {
        Assert.True(FieldNormalizer.TryDate(input, out var v, out _));
        Assert.Equal(new DateTime(y, m, d), v);
    }

    [Fact]
    public void Date_accepts_datetime_and_serial_number()
    {
        Assert.True(FieldNormalizer.TryDate(new DateTime(2024, 4, 3, 14, 30, 0), out var a, out _));
        Assert.Equal(new DateTime(2024, 4, 3), a);
        Assert.True(FieldNormalizer.TryDate(45385d, out var b, out _)); // 2024-04-03
        Assert.Equal(new DateTime(2024, 4, 3), b);
    }

    [Theory]
    [InlineData("April 3rd")]
    [InlineData("13/13/2024")]
    [InlineData("2024/04/03")]
    public void Date_unrecognised_text_is_error(string input)
    {
        Assert.False(FieldNormalizer.TryDate(input, out var v, out var error));
        Assert.Null(v);
        Assert.NotNull(error);
    }
}

public class ParserTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("excelsearch-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly string[] AllHeaders =
    {
        "Value Date", "Br. Code", "Branch Name", "Reg No", "Application no", "plot no", "Street No", "Challan No",
        "Customer Name", "Cheque Inst. No.", "CNIC No", "payorder no", "Txn Ref/ Seq No", "Discount",
        "Down payment Amount", "Debit", "Credit", "Run. Bal", "Bank Name", "Acct no", "Project", "Delear Name",
        "Name of Data", "Narraction 1", "Narration 2", "Narration 3", "Narration 4", "Narration 5", "Extra Junk",
    };

    private string Workbook(Action<IXLWorksheet> fill)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
        using var wb = new XLWorkbook();
        fill(wb.AddWorksheet("Sheet1"));
        // ExcelDataReader reads cached values, so formulas must be evaluated and stored when saving.
        wb.SaveAs(path, new SaveOptions { EvaluateFormulasBeforeSaving = true });
        return path;
    }

    private static void WriteHeaders(IXLWorksheet ws, int row = 1, string[]? headers = null)
    {
        var h = headers ?? AllHeaders;
        for (var i = 0; i < h.Length; i++) ws.Cell(row, i + 1).Value = h[i];
    }

    private static int Col(string header) => Array.IndexOf(AllHeaders, header) + 1;

    /// <summary>Parses and fully enumerates the streaming result so tests can inspect everything.</summary>
    private static ParseResultSnapshot Parse(string path)
    {
        using var file = new ExcelParser().Parse(path, null, CancellationToken.None);
        return new ParseResultSnapshot(file.FileName, file.FileHash, file.SheetName, file.FileErrors.ToList(), file.Rows.ToList());
    }

    private sealed record ParseResultSnapshot(string FileName, string FileHash, string SheetName,
        List<string> FileErrors, List<ImportRow> Rows);

    [Fact]
    public void Both_typo_headers_and_extra_columns_are_handled()
    {
        var path = Workbook(ws =>
        {
            WriteHeaders(ws);
            ws.Cell(2, Col("Value Date")).Value = new DateTime(2024, 4, 3);
            ws.Cell(2, Col("payorder no")).Value = "PO1";
            ws.Cell(2, Col("Txn Ref/ Seq No")).Value = "T1";
            ws.Cell(2, Col("Delear Name")).Value = "Dealer A";
            ws.Cell(2, Col("Narraction 1")).Value = "first narration";
        });

        var r = Parse(path);
        Assert.Empty(r.FileErrors);
        var row = Assert.Single(r.Rows);
        Assert.Equal("Dealer A", row.DealerName);
        Assert.Equal("first narration", row.Narration1);
        Assert.True(row.IsValid);
    }

    [Fact]
    public void Header_row_is_found_below_title_rows()
    {
        var path = Workbook(ws =>
        {
            ws.Cell(1, 1).Value = "Bank statement";
            WriteHeaders(ws, row: 4);
            ws.Cell(5, Col("Value Date")).Value = new DateTime(2024, 4, 3);
            ws.Cell(5, Col("payorder no")).Value = "PO1";
            ws.Cell(5, Col("Txn Ref/ Seq No")).Value = "T1";
        });

        var row = Assert.Single(Parse(path).Rows);
        Assert.Equal(5, row.ExcelRowNumber);
    }

    [Fact]
    public void Missing_required_header_is_a_file_error_and_no_rows_are_parsed()
    {
        var path = Workbook(ws =>
        {
            WriteHeaders(ws, headers: new[] { "Value Date", "payorder no", "Customer Name" });
            ws.Cell(2, 1).Value = new DateTime(2024, 4, 3);
            ws.Cell(2, 2).Value = "PO1";
        });

        var r = Parse(path);
        Assert.Empty(r.Rows);
        var error = Assert.Single(r.FileErrors);
        Assert.Contains("Txn Ref/ Seq No", error);
    }

    [Fact]
    public void Row_values_are_normalized_and_blank_rows_skipped()
    {
        var path = Workbook(ws =>
        {
            WriteHeaders(ws);
            ws.Cell(2, Col("Value Date")).Value = "03/04/2024";
            ws.Cell(2, Col("payorder no")).Value = 1234567890123d;
            ws.Cell(2, Col("Txn Ref/ Seq No")).Value = "T1";
            ws.Cell(2, Col("CNIC No")).Value = "35202-1234567-1";
            ws.Cell(2, Col("Debit")).Value = "1,250.505";
            ws.Cell(2, Col("Acct no")).Value = "00123";
            // row 3 left blank
            ws.Cell(4, Col("Value Date")).Value = "not a date";
            ws.Cell(4, Col("payorder no")).Value = "PO2";
            ws.Cell(4, Col("Txn Ref/ Seq No")).Value = "T2";
            ws.Cell(4, Col("Credit")).Value = "abc";
        });

        var r = Parse(path);
        Assert.Equal(2, r.Rows.Count);

        var good = r.Rows[0];
        Assert.Equal(new DateTime(2024, 4, 3), good.ValueDate);
        Assert.Equal("1234567890123", good.PayOrderNo);
        Assert.Equal("3520212345671", good.Cnic);
        Assert.Equal(1250.51m, good.Debit);
        Assert.Equal("00123", good.AccountNo);
        Assert.True(good.IsValid);
        Assert.Equal(64, good.RowHash.Length);

        var bad = r.Rows[1];
        Assert.Equal(4, bad.ExcelRowNumber);
        Assert.Contains(bad.Errors, e => e.Field == "ValueDate");
        Assert.Contains(bad.Errors, e => e.Field == "Credit");
        Assert.Equal(2, bad.Errors.Count); // all errors collected, no duplicate "required" error for the bad date
    }

    [Fact]
    public void Formula_cell_is_read_by_its_value()
    {
        var path = Workbook(ws =>
        {
            WriteHeaders(ws);
            ws.Cell(2, Col("Value Date")).Value = new DateTime(2024, 4, 3);
            ws.Cell(2, Col("payorder no")).Value = "PO1";
            ws.Cell(2, Col("Txn Ref/ Seq No")).Value = "T1";
            ws.Cell(2, Col("Credit")).FormulaA1 = "3500000-642555";
        });

        var row = Assert.Single(Parse(path).Rows);
        Assert.Equal(2857445m, row.Credit);
    }

    [Fact]
    public void Missing_required_fields_each_give_one_error()
    {
        var path = Workbook(ws =>
        {
            WriteHeaders(ws);
            ws.Cell(2, Col("Customer Name")).Value = "Nobody";
        });

        var row = Assert.Single(Parse(path).Rows);
        Assert.Equal(new[] { "PayOrderNo", "TxnRefSeqNo", "ValueDate" }, row.Errors.Select(e => e.Field).OrderBy(x => x));
    }

    [Fact]
    public void Row_hash_is_stable_lowercase_hex_and_changes_with_values()
    {
        var a = new ImportRow { PayOrderNo = "P", TxnRefSeqNo = "T", ValueDate = new DateTime(2024, 4, 3), Debit = 1m };
        var b = new ImportRow { PayOrderNo = "P", TxnRefSeqNo = "T", ValueDate = new DateTime(2024, 4, 3), Debit = 1.00m };
        var c = new ImportRow { PayOrderNo = "P", TxnRefSeqNo = "T", ValueDate = new DateTime(2024, 4, 3), Debit = 2m };

        Assert.Equal(RowHasher.Compute(a), RowHasher.Compute(b));
        Assert.NotEqual(RowHasher.Compute(a), RowHasher.Compute(c));
        Assert.Matches("^[0-9a-f]{64}$", RowHasher.Compute(a));
    }

    [Fact]
    public void Sample_file_counts()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "software_filed_updated.xlsx");
        var r = Parse(path);

        Assert.Empty(r.FileErrors);
        Assert.Equal(64, r.FileHash.Length);
        Assert.Equal("data base filed", r.SheetName);

        var valid = r.Rows.Count(x => x.IsValid);
        var invalid = r.Rows.Count - valid;

        // Expectation from the task: 14 data rows, 1 valid, 13 invalid (only the first row has both
        // payorder no and txn ref/seq no).
        Assert.Equal(14, r.Rows.Count);
        Assert.Equal(1, valid);
        Assert.Equal(13, invalid);

        // The =3500000-642555 formula cell is read through its cached value.
        Assert.Contains(r.Rows, x => x.Credit == 2857445m);
    }

    [Fact]
    public void Too_long_text_is_an_error_and_the_value_is_truncated_to_the_column_size()
    {
        var path = Workbook(ws =>
        {
            WriteHeaders(ws);
            ws.Cell(2, Col("Value Date")).Value = new DateTime(2024, 4, 3);
            ws.Cell(2, Col("payorder no")).Value = new string('P', 51);   // max 50
            ws.Cell(2, Col("Txn Ref/ Seq No")).Value = new string('T', 50); // exactly 50: fine
            ws.Cell(2, Col("Br. Code")).Value = new string('B', 21);       // max 20
            ws.Cell(2, Col("CNIC No")).Value = new string('9', 21);        // max 20 digits
            ws.Cell(2, Col("Narration 3")).Value = new string('N', 501);   // max 500
        });

        var row = Assert.Single(Parse(path).Rows);
        Assert.False(row.IsValid);
        Assert.Equal(new[] { "BranchCode", "Cnic", "Narration3", "PayOrderNo" },
            row.Errors.Select(e => e.Field).OrderBy(x => x));
        Assert.Equal(50, row.PayOrderNo!.Length);
        Assert.Equal(50, row.TxnRefSeqNo!.Length);
        Assert.Equal(20, row.BranchCode!.Length);
        Assert.Equal(20, row.Cnic!.Length);
        Assert.Equal(500, row.Narration3!.Length);
    }

    [Fact]
    public void Rows_are_streamed_and_file_is_released_when_enumeration_ends()
    {
        var path = Workbook(ws =>
        {
            WriteHeaders(ws);
            for (var r = 2; r <= 4; r++)
            {
                ws.Cell(r, Col("Value Date")).Value = new DateTime(2024, 4, 3);
                ws.Cell(r, Col("payorder no")).Value = "PO" + r;
                ws.Cell(r, Col("Txn Ref/ Seq No")).Value = "T" + r;
            }
        });

        var reported = new List<int>();
        using (var file = new ExcelParser().Parse(path, new Progress<int>(reported.Add), CancellationToken.None))
        {
            Assert.Equal(64, file.FileHash.Length);
            using var e = file.Rows.GetEnumerator();
            Assert.True(e.MoveNext());
            Assert.Equal(2, e.Current.ExcelRowNumber);
        }

        File.Delete(path); // would throw if the parser still held the file open
    }

    [Fact]
    public void Cancellation_stops_enumeration()
    {
        var path = Workbook(ws =>
        {
            WriteHeaders(ws);
            ws.Cell(2, Col("Value Date")).Value = new DateTime(2024, 4, 3);
            ws.Cell(2, Col("payorder no")).Value = "PO1";
            ws.Cell(2, Col("Txn Ref/ Seq No")).Value = "T1";
        });

        using var cts = new CancellationTokenSource();
        using var file = new ExcelParser().Parse(path, null, cts.Token);
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => file.Rows.ToList());
    }
}
