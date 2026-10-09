namespace ExcelSearch.Core.Import;

/// <summary>Maximum lengths of the text columns in dbo.Transactions (and dbo.ImportStaging).</summary>
internal static class FieldLimits
{
    private static readonly Dictionary<string, int> Max = new()
    {
        [nameof(ImportRow.PayOrderNo)] = 50,
        [nameof(ImportRow.TxnRefSeqNo)] = 50,
        [nameof(ImportRow.BranchCode)] = 20,
        [nameof(ImportRow.BranchName)] = 100,
        [nameof(ImportRow.RegNo)] = 50,
        [nameof(ImportRow.ApplicationNo)] = 50,
        [nameof(ImportRow.PlotNo)] = 50,
        [nameof(ImportRow.StreetNo)] = 50,
        [nameof(ImportRow.ChallanNo)] = 50,
        [nameof(ImportRow.ChequeInstNo)] = 50,
        [nameof(ImportRow.AccountNo)] = 50,
        [nameof(ImportRow.CustomerName)] = 200,
        [nameof(ImportRow.Cnic)] = 20,
        [nameof(ImportRow.BankName)] = 100,
        [nameof(ImportRow.Project)] = 100,
        [nameof(ImportRow.DealerName)] = 100,
        [nameof(ImportRow.DataSource)] = 100,
        [nameof(ImportRow.Narration1)] = 500,
        [nameof(ImportRow.Narration2)] = 500,
        [nameof(ImportRow.Narration3)] = 500,
        [nameof(ImportRow.Narration4)] = 500,
        [nameof(ImportRow.Narration5)] = 500,
    };

    public static int For(string field) => Max[field];

    /// <summary>
    /// Returns the value cut to the column size. When it had to be cut, <paramref name="error"/> says so
    /// (the row is then invalid, but the truncated value can still be written to staging).
    /// </summary>
    public static string? Apply(string field, string? value, out string? error)
    {
        error = null;
        if (value is null) return null;
        var max = For(field);
        if (value.Length <= max) return value;
        error = $"{field} is {value.Length} characters; the maximum is {max}";
        return value[..max];
    }
}
