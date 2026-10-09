using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ExcelSearch.Core.Import;

/// <summary>
/// Computes ImportRow.RowHash: SHA-256 (lowercase hex) of all business fields in a fixed order,
/// invariant culture, null as empty, joined with U+001F (a control character that does not occur in cell text).
/// </summary>
internal static class RowHasher
{
    private const char Separator = '\u001F';

    public static string Compute(ImportRow r)
    {
        string?[] parts =
        {
            Date(r.ValueDate), r.BranchCode, r.BranchName, r.RegNo, r.ApplicationNo, r.PlotNo, r.StreetNo,
            r.ChallanNo, r.ChequeInstNo, r.CustomerName, r.Cnic, r.PayOrderNo, r.TxnRefSeqNo,
            Amount(r.Discount), Amount(r.DownPaymentAmount), Amount(r.Debit), Amount(r.Credit), Amount(r.RunningBalance),
            r.BankName, r.AccountNo, r.Project, r.DealerName, r.DataSource,
            r.Narration1, r.Narration2, r.Narration3, r.Narration4, r.Narration5,
        };

        var text = string.Join(Separator, parts.Select(p => p ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string? Date(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Amount(decimal? d) => d?.ToString("F2", CultureInfo.InvariantCulture);
}
