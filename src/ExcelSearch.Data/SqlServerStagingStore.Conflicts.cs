using ExcelSearch.Core.Staging;
using ExcelSearch.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExcelSearch.Data;

// Conflict preview: for each Conflict row of a page, the existing Transactions row and what differs.
public sealed partial class SqlServerStagingStore
{
    public async Task<PagedConflicts> GetConflictPageAsync(int batchId, int pageNumber, int pageSize,
        CancellationToken ct = default)
    {
        // The staged side is the same paged query the other tabs use.
        var page = await GetPageAsync(batchId, RowStatus.Conflict, pageNumber, pageSize, ct).ConfigureAwait(false);
        if (page.Rows.Count == 0) return new PagedConflicts(Array.Empty<ConflictRow>(), page.Total);

        // One query for the whole page: by PayOrderNo (an index-friendly IN list of at most pageSize keys),
        // then matched on the full key in memory. The database collation is case-insensitive, so match that way too.
        var orders = page.Rows.Select(r => r.PayOrderNo!).Distinct().ToList();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Transactions.AsNoTracking()
            .Where(t => orders.Contains(t.PayOrderNo))
            .ToListAsync(ct).ConfigureAwait(false);
        var byKey = existing.ToDictionary(
            t => (t.PayOrderNo.ToUpperInvariant(), t.TxnRefSeqNo.ToUpperInvariant()), t => t);

        var rows = page.Rows.Select(incoming =>
        {
            var key = (incoming.PayOrderNo!.ToUpperInvariant(), incoming.TxnRefSeqNo!.ToUpperInvariant());
            return byKey.TryGetValue(key, out var t)
                ? new ConflictRow(incoming, ConflictDiff.Compare(ToStagedRow(t), incoming))
                : new ConflictRow(incoming, Array.Empty<FieldChange>(), ExistingRowMissing: true);
        }).ToList();

        return new PagedConflicts(rows, page.Total);
    }

    private static StagedRow ToStagedRow(Transaction t) => new()
    {
        Status = RowStatus.ExactDuplicate,   // not meaningful for an existing row; only the values are compared
        ValueDate = t.ValueDate.ToDateTime(TimeOnly.MinValue),
        BranchCode = t.BranchCode,
        BranchName = t.BranchName,
        RegNo = t.RegNo,
        ApplicationNo = t.ApplicationNo,
        PlotNo = t.PlotNo,
        StreetNo = t.StreetNo,
        ChallanNo = t.ChallanNo,
        ChequeInstNo = t.ChequeInstNo,
        CustomerName = t.CustomerName,
        Cnic = t.Cnic,
        PayOrderNo = t.PayOrderNo,
        TxnRefSeqNo = t.TxnRefSeqNo,
        Discount = t.Discount,
        DownPaymentAmount = t.DownPaymentAmount,
        Debit = t.Debit,
        Credit = t.Credit,
        RunningBalance = t.RunningBalance,
        BankName = t.BankName,
        AccountNo = t.AccountNo,
        Project = t.Project,
        DealerName = t.DealerName,
        DataSource = t.DataSource,
        Narration1 = t.Narration1,
        Narration2 = t.Narration2,
        Narration3 = t.Narration3,
        Narration4 = t.Narration4,
        Narration5 = t.Narration5,
    };
}
