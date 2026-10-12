namespace ExcelSearch.Core.Query;

/// <summary>The columns a result can be sorted by. A closed list: callers can never pass a raw column name into SQL.</summary>
public enum TransactionColumn
{
    ValueDate, PayOrderNo, TxnRefSeqNo, CustomerName, Cnic, ApplicationNo, RegNo, ChallanNo, ChequeInstNo,
    BankName, AccountNo, Project, DealerName, BranchName, DataSource, Debit, Credit, DownPaymentAmount, RunningBalance,
}

/// <summary>
/// Sort order for a search. Ties are always broken by (PayOrderNo, TxnRefSeqNo) so paging is stable: the same row can
/// never appear on two pages or be skipped, even when thousands of rows share a value.
/// </summary>
public readonly record struct TransactionSort(TransactionColumn Column, bool Descending)
{
    /// <summary>Newest first.</summary>
    public static TransactionSort Default { get; } = new(TransactionColumn.ValueDate, true);
}

/// <summary>A transaction's business key (the primary key of dbo.Transactions).</summary>
public readonly record struct TransactionKey(string PayOrderNo, string TxnRefSeqNo);

/// <summary>One transaction with all 28 business fields (the two key columns plus 26 more).</summary>
public sealed record TransactionRecord(
    string PayOrderNo,
    string TxnRefSeqNo,
    DateOnly ValueDate,
    string? BranchCode,
    string? BranchName,
    string? RegNo,
    string? ApplicationNo,
    string? PlotNo,
    string? StreetNo,
    string? ChallanNo,
    string? ChequeInstNo,
    string? CustomerName,
    string? Cnic,
    decimal? Discount,
    decimal? DownPaymentAmount,
    decimal? Debit,
    decimal? Credit,
    decimal? RunningBalance,
    string? BankName,
    string? AccountNo,
    string? Project,
    string? DealerName,
    string? DataSource,
    string? Narration1,
    string? Narration2,
    string? Narration3,
    string? Narration4,
    string? Narration5)
{
    public TransactionKey Key => new(PayOrderNo, TxnRefSeqNo);
}

/// <summary>Where a transaction came from: the import batch and the row in the source file.</summary>
public sealed record ImportProvenance(
    int ImportBatchId,
    string FileName,
    string? SheetName,
    DateTime ImportedAt,
    string? ImportedBy,
    int? SourceRowNumber,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>A transaction plus its import provenance (what the details panel shows).</summary>
public sealed record TransactionDetail(TransactionRecord Record, ImportProvenance Import);

/// <summary>One page of results plus how many rows match in all.</summary>
public sealed record TransactionPage(IReadOnlyList<TransactionRecord> Rows, int TotalCount, int PageNumber, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Sums over the whole filtered set (not just one page). A column with no values sums to 0.</summary>
public sealed record TransactionTotals(decimal Credit, decimal Debit, decimal DownPayment);

/// <summary>The dropdown fields whose distinct values can be listed.</summary>
public enum DistinctField { BankName, Project, DealerName, BranchName, DataSource }

public enum ExportFormat { Xlsx, Csv }

public sealed record QueryExportResult(bool Success, long RowsWritten, ExportFormat Format, string? DestinationPath, string? Message);

/// <summary>The search ran longer than the allowed 60 seconds. Raised instead of a raw database timeout.</summary>
public sealed class QueryTimeoutException : Exception
{
    public QueryTimeoutException(Exception inner) : base("The search took too long and was stopped.", inner) { }
}
