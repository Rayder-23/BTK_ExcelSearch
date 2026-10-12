using System.Globalization;

namespace ExcelSearch.Core.Staging;

/// <summary>One column that differs between the row already in the database and the row in the file.</summary>
public sealed record FieldChange(string Label, string? Existing, string? Incoming, bool IsAmount = false)
{
    /// <summary>"(empty)" stands in for null so a cleared or newly filled value is visible.</summary>
    public string ExistingText => Existing ?? "(empty)";
    public string IncomingText => Incoming ?? "(empty)";
}

/// <summary>
/// A Conflict row together with what is different: the incoming (file) row, and the columns whose values
/// differ from the Transactions row with the same key.
/// </summary>
public sealed record ConflictRow(StagedRow Incoming, IReadOnlyList<FieldChange> Changes, bool ExistingRowMissing = false)
{
    public int ChangeCount => Changes.Count;

    /// <summary>"Debit 12,500.00 → 13,000.00 · Customer name" style one-liner for the grid.</summary>
    public string Summary
    {
        get
        {
            if (ExistingRowMissing) return "The row in the database was removed after this file was checked.";
            if (Changes.Count == 0) return "Values differ (no single column stands out).";
            return string.Join("  ·  ", Changes.Take(3).Select(c => c.IsAmount
                       ? $"{c.Label}: {c.ExistingText} → {c.IncomingText}"
                       : c.Label)) + (Changes.Count > 3 ? $"  ·  +{Changes.Count - 3} more" : "");
        }
    }
}

public sealed record PagedConflicts(IReadOnlyList<ConflictRow> Rows, int Total);

/// <summary>Compares the staged (incoming) row with the existing Transactions row, column by column.</summary>
public static class ConflictDiff
{
    public static IReadOnlyList<FieldChange> Compare(StagedRow existing, StagedRow incoming)
    {
        var changes = new List<FieldChange>();

        void Text(string label, string? a, string? b)
        {
            // Ordinal, like the row hash: a change of letter case is a real change.
            if (!string.Equals(a, b, StringComparison.Ordinal)) changes.Add(new FieldChange(label, a, b));
        }
        void Amount(string label, decimal? a, decimal? b)
        {
            if (a != b) changes.Add(new FieldChange(label, a?.ToString("N2", CultureInfo.InvariantCulture),
                b?.ToString("N2", CultureInfo.InvariantCulture), IsAmount: true));
        }

        Text("Value date", Date(existing.ValueDate), Date(incoming.ValueDate));
        Text("Customer name", existing.CustomerName, incoming.CustomerName);
        Text("CNIC", existing.Cnic, incoming.Cnic);
        Amount("Debit", existing.Debit, incoming.Debit);
        Amount("Credit", existing.Credit, incoming.Credit);
        Amount("Running balance", existing.RunningBalance, incoming.RunningBalance);
        Amount("Discount", existing.Discount, incoming.Discount);
        Amount("Down payment", existing.DownPaymentAmount, incoming.DownPaymentAmount);
        Text("Bank", existing.BankName, incoming.BankName);
        Text("Account no", existing.AccountNo, incoming.AccountNo);
        Text("Project", existing.Project, incoming.Project);
        Text("Dealer", existing.DealerName, incoming.DealerName);
        Text("Branch code", existing.BranchCode, incoming.BranchCode);
        Text("Branch name", existing.BranchName, incoming.BranchName);
        Text("Reg no", existing.RegNo, incoming.RegNo);
        Text("Application no", existing.ApplicationNo, incoming.ApplicationNo);
        Text("Plot no", existing.PlotNo, incoming.PlotNo);
        Text("Street no", existing.StreetNo, incoming.StreetNo);
        Text("Challan no", existing.ChallanNo, incoming.ChallanNo);
        Text("Cheque / inst no", existing.ChequeInstNo, incoming.ChequeInstNo);
        Text("Data source", existing.DataSource, incoming.DataSource);
        Text("Narration 1", existing.Narration1, incoming.Narration1);
        Text("Narration 2", existing.Narration2, incoming.Narration2);
        Text("Narration 3", existing.Narration3, incoming.Narration3);
        Text("Narration 4", existing.Narration4, incoming.Narration4);
        Text("Narration 5", existing.Narration5, incoming.Narration5);

        return changes;
    }

    private static string? Date(DateTime? d) => d?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
}
