using ExcelSearch.Core.Import;

namespace ExcelSearch.Tests;

/// <summary>
/// Parses the workbooks in Samples/test/ (made by Scripts/samples/make_samples.py) and checks they contain what
/// Samples/README.md promises. Parse only, no database.
/// </summary>
public class SampleFilesTests
{
    internal static string SamplePathFor(string name) => SamplePath(name);

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
    public void Invalid_file_has_11_invalid_rows_and_a_clashing_pair()
    {
        var (rows, errors) = Parse("02_invalid_rows.xlsx");
        Assert.Empty(errors);
        Assert.Equal(31, rows.Count);                    // the blank row is skipped
        Assert.Equal(11, rows.Count(r => !r.IsValid));
        Assert.Equal(20, rows.Count(r => r.IsValid));    // 16 good + the 2 key-less rows + the 2 clashing rows (invalid only once classified)

        var clash = rows.Where(r => r.PayOrderNo == "PO-B-0026").ToList();
        Assert.Equal(2, clash.Count);
        Assert.NotEqual(clash[0].RowHash, clash[1].RowHash);
        var noKeyNoDate = rows.Single(r => r.PayOrderNo is null && r.TxnRefSeqNo is null);
        Assert.Equal("ValueDate", Assert.Single(noKeyNoDate.Errors).Field);   // no key is fine; the missing date is the only error
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
        Assert.Contains(errors, e => e.Contains("Value Date", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Keyless_statement_file_is_all_valid_and_slips_and_cnics_are_extracted()
    {
        var (rows, errors) = Parse("11_keyless_statement.xlsx");
        Assert.Empty(errors);
        Assert.Equal(109, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsValid, string.Join("; ", r.Errors.Select(e => e.Message))));
        Assert.Equal(106, rows.Count(r => r.PayOrderNo is null && r.TxnRefSeqNo is null));   // 100 + 6 repeats
        Assert.Equal(3, rows.Count(r => r.PayOrderNo is not null));                           // the keyed rows of the mixed file
        Assert.All(rows.Where(r => r.PayOrderNo is null), r => Assert.Matches("^[0-9]{8}$", r.SlipNo!));
        Assert.Equal(35, rows.Count(r => r.NarrationCnic is not null && r.PayOrderNo is null)); // every third statement row (33) plus 2 of the repeats
        Assert.Equal(100, rows.Where(r => r.PayOrderNo is null).Select(r => r.RowHash).Distinct().Count());   // 6 are exact repeats
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

    [Fact]
    public void File_09_changes_the_first_250_rows_of_file_06_so_they_become_conflicts_once_06_is_committed()
    {
        var (big, _) = Parse("06_large_for_paging.xlsx");
        var (changed, errors) = Parse("09_reimport_of_06_changed_rows.xlsx");
        Assert.Empty(errors);
        Assert.Equal(250, changed.Count);
        Assert.All(changed, r => Assert.True(r.IsValid));

        var byKey = big.Where(r => r.IsValid).GroupBy(r => (r.PayOrderNo, r.TxnRefSeqNo)).ToDictionary(g => g.Key, g => g.First().RowHash);
        Assert.All(changed, r =>
        {
            Assert.True(byKey.ContainsKey((r.PayOrderNo, r.TxnRefSeqNo)), "same key as a row of file 06");
            Assert.NotEqual(byKey[(r.PayOrderNo, r.TxnRefSeqNo)], r.RowHash);   // but different values
        });
    }

    [Fact]
    public void Query_demo_file_has_3000_valid_rows_and_the_rows_the_query_checklist_looks_for()
    {
        var (rows, errors) = Parse("10_query_demo_3000_rows.xlsx");
        Assert.Empty(errors);
        Assert.Equal(3000, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsValid, r.PayOrderNo + ": " + string.Join("; ", r.Errors.Select(e => e.Message))));
        Assert.Equal(3000, rows.Select(r => (r.PayOrderNo, r.TxnRefSeqNo)).Distinct().Count());

        // The totals the checklist quotes.
        Assert.Equal(3_020_692_192.00m, rows.Sum(r => r.Credit ?? 0));
        Assert.Equal(6_388_848.10m, rows.Sum(r => r.Debit ?? 0));
        Assert.Equal(203_305_569.31m, rows.Sum(r => r.DownPaymentAmount ?? 0));

        Assert.Equal(376, rows.Count(r => r.BankName == "HBL"));
        Assert.Equal(126, rows.Count(r => r.BankName == "HBL" && r.Project == "Bahria Paradise"));
        Assert.Equal(191, rows.Count(r => r.CustomerName!.Contains("khan", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(16, rows.Count(r => r.Credit >= 10_000m && r.Credit <= 20_000m));
        Assert.Equal(378, rows.Count(r => r.ValueDate >= new DateTime(2024, 1, 1) && r.ValueDate <= new DateTime(2024, 3, 31)));

        var needle = Assert.Single(rows, r => r.PayOrderNo == "PO-Q-NEEDLE");
        Assert.Equal("Needle Customer", needle.CustomerName);
        Assert.Equal("421019999999" + "9", needle.Cnic);                  // dashes stripped on import
        Assert.Equal("NEEDLE-001", needle.ApplicationNo);
        Assert.Equal(123_456.78m, needle.Credit);
        Assert.Equal(new DateTime(2024, 2, 29), needle.ValueDate);

        Assert.Equal("O'Brien & Sons \"Traders\"", rows[500].CustomerName);
        Assert.Equal("100% Cotton_Mills", rows[501].CustomerName);
        Assert.Equal("000123", rows[508].ChequeInstNo);                 // leading zeros kept (text)
        Assert.Contains("\n", rows[507].Narration1);
    }
}
