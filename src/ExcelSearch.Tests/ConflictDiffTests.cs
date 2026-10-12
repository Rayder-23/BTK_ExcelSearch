using ExcelSearch.Core.Staging;

namespace ExcelSearch.Tests;

public class ConflictDiffTests
{
    private static StagedRow Row(decimal? debit = 100m, string? customer = "Ahmed", DateTime? date = null, string? narration = null) =>
        new()
        {
            PayOrderNo = "PO1", TxnRefSeqNo = "T1", Debit = debit, CustomerName = customer,
            ValueDate = date ?? new DateTime(2024, 4, 3), Narration1 = narration,
        };

    [Fact]
    public void Identical_values_have_no_changes()
    {
        Assert.Empty(ConflictDiff.Compare(Row(), Row()));
    }

    [Fact]
    public void Changed_amount_and_text_are_listed_with_both_values()
    {
        var changes = ConflictDiff.Compare(Row(debit: 1250.5m, customer: "Ahmed"), Row(debit: 1300m, customer: "Ahmed Khan"));

        Assert.Equal(new[] { "Customer name", "Debit" }, changes.Select(c => c.Label).OrderBy(x => x));
        var debit = changes.Single(c => c.Label == "Debit");
        Assert.Equal("1,250.50", debit.Existing);
        Assert.Equal("1,300.00", debit.Incoming);
        Assert.True(debit.IsAmount);
        Assert.Equal("Ahmed Khan", changes.Single(c => c.Label == "Customer name").Incoming);
    }

    [Fact]
    public void A_cleared_or_newly_filled_value_reads_as_empty()
    {
        var changes = ConflictDiff.Compare(Row(narration: "old note"), Row(narration: null));

        var change = Assert.Single(changes);
        Assert.Equal("Narration 1", change.Label);
        Assert.Equal("old note", change.ExistingText);
        Assert.Equal("(empty)", change.IncomingText);
    }

    [Fact]
    public void Date_change_is_shown_in_a_readable_format_and_letter_case_counts_as_a_change()
    {
        var changes = ConflictDiff.Compare(Row(date: new DateTime(2024, 4, 3), customer: "ahmed"),
            Row(date: new DateTime(2024, 4, 5), customer: "Ahmed"));

        Assert.Equal("03-Apr-2024", changes.Single(c => c.Label == "Value date").Existing);
        Assert.Equal("05-Apr-2024", changes.Single(c => c.Label == "Value date").Incoming);
        Assert.Contains(changes, c => c.Label == "Customer name");
    }

    [Fact]
    public void Summary_names_amount_changes_with_values_and_caps_the_list()
    {
        var one = new ConflictRow(Row(), new[] { new FieldChange("Debit", "1.00", "2.00", IsAmount: true) });
        Assert.Equal("Debit: 1.00 → 2.00", one.Summary);

        var many = new ConflictRow(Row(), Enumerable.Range(1, 6).Select(i => new FieldChange("F" + i, "a", "b")).ToList());
        Assert.EndsWith("+3 more", many.Summary);
        Assert.Equal(6, many.ChangeCount);

        var missing = new ConflictRow(Row(), Array.Empty<FieldChange>(), ExistingRowMissing: true);
        Assert.Contains("removed", missing.Summary);
    }
}
