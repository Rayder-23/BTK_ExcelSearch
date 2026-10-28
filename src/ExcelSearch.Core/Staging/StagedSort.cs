namespace ExcelSearch.Core.Staging;

/// <summary>
/// The columns the import preview can be sorted by. A closed list: callers can never pass a raw column name into SQL.
/// The first value is the default (the order of the Excel file).
/// </summary>
public enum StagedColumn
{
    ExcelRowNumber, Overwrite, ErrorText, MatchReason,
    ValueDate, PayOrderNo, TxnRefSeqNo, CustomerName, Cnic, ApplicationNo, RegNo, ChallanNo, ChequeInstNo,
    BankName, AccountNo, Project, DealerName, BranchName, DataSource, Debit, Credit, DownPaymentAmount, RunningBalance,
    SlipNo, Discount, BranchCode, PlotNo, StreetNo, Narration1, Narration2, Narration3, Narration4, Narration5, NarrationCnic,
}

/// <summary>
/// Sort order of a preview page. Ties are broken by the Excel row number, so paging is stable. <c>default</c> is
/// "file order": ExcelRowNumber ascending.
/// </summary>
public readonly record struct StagedSort(StagedColumn Column, bool Descending);
