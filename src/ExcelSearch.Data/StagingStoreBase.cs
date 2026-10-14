using ExcelSearch.Core.Staging;
using ExcelSearch.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExcelSearch.Data;

/// <summary>
/// The part of <see cref="IStagingStore"/> that is plain EF Core and therefore identical on every database:
/// creating a batch, paged reads, counts, the Overwrite flags, the conflict page and the Invalid-row reads for the export.
/// Each provider's store derives from this and adds what needs hand-written SQL (load, classify, commit, discard,
/// re-check, cleanup), so the two cannot drift apart in the simple methods. Every query is scoped by ImportBatchId.
/// </summary>
public abstract class StagingStoreBase : IStagingStore
{
    protected const int CommandTimeoutSeconds = 600;
    protected const int ErrorTextMax = ImportRowDataReader.ErrorTextMax;

    protected StagingStoreBase(IDbContextFactory<AppDbContext> factory) => Factory = factory;

    protected IDbContextFactory<AppDbContext> Factory { get; }

    // ---- provider-specific operations ----------------------------------------------------------------------

    public abstract Task<LoadResult> LoadRowsAsync(int batchId, IEnumerable<Core.Import.ImportRow> rows, IProgress<int>? progress,
        CancellationToken ct = default);

    public abstract Task ClassifyAsync(int batchId, CancellationToken ct = default);
    public abstract Task<DiscardResult> DiscardAsync(int batchId, CancellationToken ct = default);
    public abstract Task<CommitResult> CommitAsync(int batchId, bool skipFlaggedDuplicates = false, CancellationToken ct = default);
    public abstract Task<CleanupResult> CleanupStaleBatchesAsync(CancellationToken ct = default);
    public abstract Task<RefreshResult> RefreshAgainstDatabaseAsync(int batchId, CancellationToken ct = default);

    // ---- shared, EF-only operations ------------------------------------------------------------------------

    public async Task<int> CreateBatchAsync(string fileName, string? sheetName, string fileHash, string? importedBy,
        CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var batch = new ImportBatch
        {
            FileName = Cut(fileName, 260)!,
            SheetName = Cut(sheetName, 100),
            FileHash = fileHash,
            ImportedBy = Cut(importedBy, 256),
            Status = "Staged",
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return batch.Id;
    }

    public async Task<IReadOnlyDictionary<RowStatus, int>> GetCountsAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var grouped = await db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId)
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = Enum.GetValues<RowStatus>().ToDictionary(s => s, _ => 0);
        foreach (var g in grouped) result[(RowStatus)g.Status] = g.Count;
        return result;
    }

    public async Task<PagedRows> GetPageAsync(int batchId, RowStatus status, int pageNumber, int pageSize,
        CancellationToken ct = default)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber), "Pages are 1-based.");
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)status);

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var entities = await query.OrderBy(s => s.ExcelRowNumber)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return new PagedRows(entities.Select(ToStagedRow).ToList(), total);
    }

    public async Task<int> SetConflictOverwriteAsync(int batchId, bool overwrite,
        IReadOnlyCollection<int>? excelRowNumbers, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Database.SetCommandTimeout(CommandTimeoutSeconds);

        var query = db.ImportStagings.Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Conflict);
        if (excelRowNumbers is not null)
        {
            var rows = excelRowNumbers.ToArray();
            query = query.Where(s => rows.Contains(s.ExcelRowNumber));
        }
        return await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.Overwrite, overwrite), ct).ConfigureAwait(false);
    }

    public async Task<PagedConflicts> GetConflictPageAsync(int batchId, int pageNumber, int pageSize,
        CancellationToken ct = default)
    {
        // The staged side is the same paged query the other tabs use.
        var page = await GetPageAsync(batchId, RowStatus.Conflict, pageNumber, pageSize, ct).ConfigureAwait(false);
        if (page.Rows.Count == 0) return new PagedConflicts(Array.Empty<ConflictRow>(), page.Total);

        // One query for the whole page: by PayOrderNo (an index-friendly IN list of at most pageSize keys),
        // then matched on the full key in memory. The database collation is case-insensitive, so match that way too.
        var orders = page.Rows.Select(r => r.PayOrderNo!).Distinct().ToList();
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Transactions.AsNoTracking()
            .Where(t => orders.Contains(t.PayOrderNo!))
            .ToListAsync(ct).ConfigureAwait(false);
        var byKey = existing.ToDictionary(
            t => (t.PayOrderNo!.ToUpperInvariant(), t.TxnRefSeqNo!.ToUpperInvariant()), t => t);

        var rows = page.Rows.Select(incoming =>
        {
            var key = (incoming.PayOrderNo!.ToUpperInvariant(), incoming.TxnRefSeqNo!.ToUpperInvariant());
            return byKey.TryGetValue(key, out var t)
                ? new ConflictRow(incoming, ConflictDiff.Compare(ToStagedRow(t), incoming))
                : new ConflictRow(incoming, Array.Empty<FieldChange>(), ExistingRowMissing: true);
        }).ToList();

        return new PagedConflicts(rows, page.Total);
    }

    public async Task<ConflictCounts> GetConflictCountsAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var conflicts = db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Conflict);
        var total = await conflicts.CountAsync(ct).ConfigureAwait(false);
        var overwrite = await conflicts.CountAsync(s => s.Overwrite, ct).ConfigureAwait(false);
        return new ConflictCounts(total, overwrite);
    }

    public async Task<int> GetFlaggedKeylessCountAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ImportStagings.AsNoTracking()
            .CountAsync(s => s.ImportBatchId == batchId
                             && (s.Status == (byte)RowStatus.ExactDuplicate || s.Status == (byte)RowStatus.FileDuplicate
                                 || s.Status == (byte)RowStatus.LikelyDuplicate)
                             && (s.PayOrderNo == null || s.TxnRefSeqNo == null), ct).ConfigureAwait(false);
    }

    // ---- Invalid rows for the export, in keyset pages (WHERE ExcelRowNumber > last ORDER BY ExcelRowNumber) ----

    public async Task<BatchInfo?> GetBatchAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ImportBatches.AsNoTracking().Where(b => b.Id == batchId)
            .Select(b => new BatchInfo(b.Id, b.FileName, b.SheetName, b.FileHash, b.Status))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InvalidRowError>> GetInvalidErrorsAfterAsync(int batchId, int afterRow, int take,
        CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Invalid && s.ExcelRowNumber > afterRow)
            .OrderBy(s => s.ExcelRowNumber).Take(take)
            .Select(s => new InvalidRowError(s.ExcelRowNumber, s.ErrorText))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StagedRow>> GetInvalidRowsAfterAsync(int batchId, int afterRow, int take,
        CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.ImportStagings.AsNoTracking()
            .Where(s => s.ImportBatchId == batchId && s.Status == (byte)RowStatus.Invalid && s.ExcelRowNumber > afterRow)
            .OrderBy(s => s.ExcelRowNumber).Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
        return entities.Select(ToStagedRow).ToList();
    }

    /// <summary>
    /// Step 1 of every discard: flips a 'Staged' batch to 'Discarded' with one conditional UPDATE, which succeeds for exactly one
    /// caller and only while the batch is 'Staged'. Returns null when this caller won (go on and delete the staging rows) or the
    /// refusal to hand back (batch missing, already committed, already discarded). Nothing is deleted here.
    /// </summary>
    protected static async Task<DiscardResult?> TryClaimForDiscardAsync(AppDbContext db, int batchId, CancellationToken ct)
    {
        var claimed = await db.ImportBatches.Where(b => b.Id == batchId && b.Status == "Staged")
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, "Discarded"), ct).ConfigureAwait(false);
        if (claimed > 0) return null;

        var status = await db.ImportBatches.AsNoTracking().Where(b => b.Id == batchId)
            .Select(b => b.Status).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return new DiscardResult(false, status switch
        {
            null => $"Batch {batchId} does not exist.",
            "Committed" => $"Batch {batchId} was already committed, so it cannot be discarded.",
            "Discarded" => $"Batch {batchId} was already discarded.",
            _ => $"Batch {batchId} is '{status}', not 'Staged', so it cannot be discarded.",
        });
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    protected static string? Cut(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static StagedRow ToStagedRow(ImportStaging s) => new()
    {
        ExcelRowNumber = s.ExcelRowNumber,
        Status = (RowStatus)s.Status,
        Overwrite = s.Overwrite,
        ErrorText = s.ErrorText,
        RowHash = s.RowHash,
        ExistingRowHash = s.ExistingRowHash,
        ValueDate = s.ValueDate?.ToDateTime(TimeOnly.MinValue),
        BranchCode = s.BranchCode,
        BranchName = s.BranchName,
        RegNo = s.RegNo,
        ApplicationNo = s.ApplicationNo,
        PlotNo = s.PlotNo,
        StreetNo = s.StreetNo,
        ChallanNo = s.ChallanNo,
        ChequeInstNo = s.ChequeInstNo,
        CustomerName = s.CustomerName,
        Cnic = s.Cnic,
        PayOrderNo = s.PayOrderNo,
        TxnRefSeqNo = s.TxnRefSeqNo,
        Discount = s.Discount,
        DownPaymentAmount = s.DownPaymentAmount,
        Debit = s.Debit,
        Credit = s.Credit,
        RunningBalance = s.RunningBalance,
        BankName = s.BankName,
        AccountNo = s.AccountNo,
        Project = s.Project,
        DealerName = s.DealerName,
        DataSource = s.DataSource,
        Narration1 = s.Narration1,
        Narration2 = s.Narration2,
        Narration3 = s.Narration3,
        Narration4 = s.Narration4,
        Narration5 = s.Narration5,
        SlipNo = s.SlipNo,
        NarrationCnic = s.NarrationCnic,
    };

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
        SlipNo = t.SlipNo,
        NarrationCnic = t.NarrationCnic,
    };
}
