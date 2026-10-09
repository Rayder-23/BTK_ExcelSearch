namespace ExcelSearch.Core.Import;

/// <summary>One normalized data row from the worksheet. Property names mirror the Transactions columns.</summary>
public sealed class ImportRow
{
    /// <summary>1-based row number in the worksheet, as shown in Excel.</summary>
    public int ExcelRowNumber { get; set; }

    public DateTime? ValueDate { get; set; }
    public string? BranchCode { get; set; }
    public string? BranchName { get; set; }
    public string? RegNo { get; set; }
    public string? ApplicationNo { get; set; }
    public string? PlotNo { get; set; }
    public string? StreetNo { get; set; }
    public string? ChallanNo { get; set; }
    public string? ChequeInstNo { get; set; }
    public string? CustomerName { get; set; }
    public string? Cnic { get; set; }
    public string? PayOrderNo { get; set; }
    public string? TxnRefSeqNo { get; set; }
    public decimal? Discount { get; set; }
    public decimal? DownPaymentAmount { get; set; }
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
    public decimal? RunningBalance { get; set; }
    public string? BankName { get; set; }
    public string? AccountNo { get; set; }
    public string? Project { get; set; }
    public string? DealerName { get; set; }
    public string? DataSource { get; set; }
    public string? Narration1 { get; set; }
    public string? Narration2 { get; set; }
    public string? Narration3 { get; set; }
    public string? Narration4 { get; set; }
    public string? Narration5 { get; set; }

    /// <summary>SHA-256 hex of the normalized business fields (see RowHasher).</summary>
    public string RowHash { get; set; } = "";

    /// <summary>
    /// True when an earlier row in the same file has the same key and identical values.
    /// Not an error: the importer should simply skip this row.
    /// </summary>
    public bool IsFileDuplicate { get; set; }

    public List<RowError> Errors { get; } = new();

    public bool IsValid => Errors.Count == 0;
}
