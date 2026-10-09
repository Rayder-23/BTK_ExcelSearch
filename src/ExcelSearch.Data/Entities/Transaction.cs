using System;
using System.Collections.Generic;

namespace ExcelSearch.Data.Entities;

public partial class Transaction
{
    public string PayOrderNo { get; set; } = null!;

    public string TxnRefSeqNo { get; set; } = null!;

    public Guid Id { get; set; }

    public int ImportBatchId { get; set; }

    public int? SourceRowNumber { get; set; }

    public string RowHash { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public DateOnly ValueDate { get; set; }

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

    public virtual ImportBatch ImportBatch { get; set; } = null!;
}
