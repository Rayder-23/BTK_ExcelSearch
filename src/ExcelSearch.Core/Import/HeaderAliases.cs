using System.Text;

namespace ExcelSearch.Core.Import;

/// <summary>
/// Maps worksheet headers to ImportRow property names. A header is normalized (lowercase, letters and digits only)
/// and looked up in the alias table, so "Txn Ref/ Seq No" and "txn_ref_seq_no" are the same header.
/// </summary>
internal static class HeaderAliases
{
    /// <summary>Required fields, with the header text shown to the user when one is missing.</summary>
    public static readonly (string Field, string Display)[] Required =
    {
        (nameof(ImportRow.PayOrderNo), "payorder no"),
        (nameof(ImportRow.TxnRefSeqNo), "Txn Ref/ Seq No"),
        (nameof(ImportRow.ValueDate), "Value Date"),
    };

    private static readonly Dictionary<string, string> Map = new()
    {
        ["valuedate"] = nameof(ImportRow.ValueDate),
        ["brcode"] = nameof(ImportRow.BranchCode),
        ["branchname"] = nameof(ImportRow.BranchName),
        ["regno"] = nameof(ImportRow.RegNo),
        ["applicationno"] = nameof(ImportRow.ApplicationNo),
        ["plotno"] = nameof(ImportRow.PlotNo),
        ["streetno"] = nameof(ImportRow.StreetNo),
        ["challanno"] = nameof(ImportRow.ChallanNo),
        ["customername"] = nameof(ImportRow.CustomerName),
        ["chequeinstno"] = nameof(ImportRow.ChequeInstNo),
        ["cnicno"] = nameof(ImportRow.Cnic),
        ["payorderno"] = nameof(ImportRow.PayOrderNo),
        ["txnrefseqno"] = nameof(ImportRow.TxnRefSeqNo),
        ["discount"] = nameof(ImportRow.Discount),
        ["downpaymentamount"] = nameof(ImportRow.DownPaymentAmount),
        ["debit"] = nameof(ImportRow.Debit),
        ["credit"] = nameof(ImportRow.Credit),
        ["runbal"] = nameof(ImportRow.RunningBalance),
        ["bankname"] = nameof(ImportRow.BankName),
        ["acctno"] = nameof(ImportRow.AccountNo),
        ["project"] = nameof(ImportRow.Project),
        ["dealername"] = nameof(ImportRow.DealerName),
        ["delearname"] = nameof(ImportRow.DealerName), // typo in the source files
        ["nameofdata"] = nameof(ImportRow.DataSource),
        ["narration1"] = nameof(ImportRow.Narration1),
        ["narraction1"] = nameof(ImportRow.Narration1), // typo in the source files
        ["narration2"] = nameof(ImportRow.Narration2),
        ["narration3"] = nameof(ImportRow.Narration3),
        ["narration4"] = nameof(ImportRow.Narration4),
        ["narration5"] = nameof(ImportRow.Narration5),
    };

    public static string Normalize(string? header)
    {
        if (string.IsNullOrEmpty(header)) return "";
        var sb = new StringBuilder(header.Length);
        foreach (var c in header)
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>The ImportRow property a header belongs to, or null for unknown headers (which are ignored).</summary>
    public static string? Resolve(string? header) =>
        Map.TryGetValue(Normalize(header), out var field) ? field : null;
}
