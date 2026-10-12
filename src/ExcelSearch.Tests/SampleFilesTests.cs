using ExcelSearch.Core.Import;

namespace ExcelSearch.Tests;

/// <summary>
/// Parses the workbooks in Samples/test/ (made by Scripts/samples/make_samples.py) and checks they contain what
/// Samples/README.md promises. Parse only, no database.
/// </summary>
public class SampleFilesTests
{
    private static string SamplePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExcelSearch.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Samples", "test", name);
    }

    private static (List<ImportRow> Rows, List<string> FileErrors) Parse(string name)
    {
        using var file = new ExcelParser().Parse(SamplePath(name), null, CancellationToken.None);
        return (file.Rows.ToList(), file.FileErrors.ToList());
    }

    [Fact]
    public void Perfect_file_has_40_valid_rows()
    {
        var (rows, errors) = Parse("01_perfect_all_new.xlsx");
        Assert.Empty(errors);
        Assert.Equal(40, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsValid, string.Join("; ", r.Errors.Select(e => e.Message))));
        Assert.Equal(40, rows.Select(r => (r.PayOrderNo, r.TxnRefSeqNo)).Distinct().Count());
        Assert.Equal(new DateTime(2024, 4, 7), rows[6].ValueDate);    // text date 07/04/2024
        Assert.Equal("3520212345671", rows[8].Cnic);                  // digits only
    }

    [Fact]
    public void Invalid_file_has_13_invalid_rows_and_a_clashing_pair()
    {
        var (rows, errors) = Parse("02_invalid_rows.xlsx");
        Assert.Empty(errors);
        Assert.Equal(31, rows.Count);                    // the blank row is skipped
        Assert.Equal(13, rows.Count(r => !r.IsValid));
        Assert.Equal(18, rows.Count(r => r.IsValid));    // 16 good + the 2 clashing rows (invalid only once classified)

        var clash = rows.Where(r => r.PayOrderNo == "PO-B-0026").ToList();
        Assert.Equal(2, clash.Count);
        Assert.NotEqual(clash[0].RowHash, clash[1].RowHash);
        Assert.Equal(3, rows.Single(r => r.PayOrderNo is null && r.TxnRefSeqNo is null).Errors.Count);
    }

    [Fact]
    public void Reimport_file_overlaps_file_01_as_designed()
    {
        var (first, _) = Parse("01_perfect_all_new.xlsx");
        var (second, errors) = Parse("03_reimport_of_01_duplicates_conflicts.xlsx");
        Assert.Empty(errors);
        Assert.Equal(36, second.Count);
        Assert.Equal(1, second.Count(r => !r.IsValid));

        var byKey = first.ToDictionary(r => (r.PayOrderNo, r.TxnRefSeqNo), r => r.RowHash);
        var overlapping = second.Where(r => byKey.ContainsKey((r.PayOrderNo, r.TxnRefSeqNo))).ToList();
        Assert.Equal(25, overlapping.Count);
        Assert.Equal(15, overlapping.Count(r => byKey[(r.PayOrderNo, r.TxnRefSeqNo)] == r.RowHash));   // identical
        Assert.Equal(10, overlapping.Count(r => byKey[(r.PayOrderNo, r.TxnRefSeqNo)] != r.RowHash));   // changed
        Assert.Equal(10, second.Count(r => r.IsValid && !byKey.ContainsKey((r.PayOrderNo, r.TxnRefSeqNo))));   // 8 new + 2 repeats
    }

    [Fact]
    public void Repeats_file_has_distinct_rows_identical_repeats_and_a_clashing_pair()
    {
        var (rows, errors) = Parse("04_repeated_rows_in_file.xlsx");
        Assert.Empty(errors);
        Assert.Equal(26, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsValid));
        var groups = rows.GroupBy(r => (r.PayOrderNo, r.TxnRefSeqNo)).ToList();
        Assert.Equal(21, groups.Count);                                           // 20 + the clashing key
        var identical = groups.Where(g => g.Count() > 1 && g.Select(r => r.RowHash).Distinct().Count() == 1).ToList();
        var clashing = groups.Where(g => g.Select(r => r.RowHash).Distinct().Count() > 1).ToList();
        Assert.Equal(new[] { 2, 4 }, identical.Select(g => g.Count()).OrderBy(n => n));   // repeated rows: 3 + 1 extra copies
        Assert.Equal(2, Assert.Single(clashing).Count());
    }

    [Fact]
    public void File_with_a_missing_required_header_is_rejected()
    {
        var (rows, errors) = Parse("05_missing_required_header.xlsx");
        Assert.Empty(rows);
        Assert.Contains(errors, e => e.Contains("payorder no", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Paging_file_has_enough_rows_for_several_pages_per_tab()
    {
        var (rows, errors) = Parse("06_large_for_paging.xlsx");
        Assert.Empty(errors);
        Assert.Equal(1420, rows.Count);
        Assert.Equal(250, rows.Count(r => !r.IsValid));
        Assert.Equal(1170, rows.Count(r => r.IsValid));   // 1,050 distinct + 120 identical repeats
        Assert.Equal(1050, rows.Where(r => r.IsValid).Select(r => (r.PayOrderNo, r.TxnRefSeqNo)).Distinct().Count());
    }

    [Fact]
    public void A_text_file_named_xlsx_is_rejected()
    {
        var (rows, errors) = Parse("07_not_really_excel.xlsx");
        Assert.Empty(rows);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Header_on_row_4_with_variant_header_spellings_is_read()
    {
        var (rows, errors) = Parse("08_header_on_row_4_variant_headers.xlsx");
        Assert.Empty(errors);
        Assert.Equal(10, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsValid));
        Assert.Equal(5, rows[0].ExcelRowNumber);          // Excel's own row number, after 3 title rows + header
        Assert.NotNull(rows[0].DealerName);
        Assert.NotNull(rows[0].Narration1);
    }
}
