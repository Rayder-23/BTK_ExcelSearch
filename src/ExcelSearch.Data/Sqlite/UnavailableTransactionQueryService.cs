using ExcelSearch.Core.Query;

namespace ExcelSearch.Data.Sqlite;

/// <summary>
/// Placeholder registered for SQLite until the Query screen is ported (next step). The app never navigates to the Query screen
/// in SQLite mode, so these members are not reached; if one ever is, it fails with a clear message instead of a confusing SQL error.
/// </summary>
public sealed class UnavailableTransactionQueryService : ITransactionQueryService
{
    public const string Message = "The Query screen is not yet available for the local SQLite database.";

    private static NotSupportedException Unavailable() => new(Message);

    public Task<TransactionPage> SearchAsync(TransactionFilter filter, TransactionSort sort, int pageNumber, int pageSize,
        CancellationToken ct = default) => throw Unavailable();

    public Task<IReadOnlyList<TransactionRecord>> GetPageAsync(TransactionFilter filter, TransactionSort sort, int pageNumber,
        int pageSize, CancellationToken ct = default) => throw Unavailable();

    public Task<int> CountAsync(TransactionFilter filter, CancellationToken ct = default) => throw Unavailable();

    public Task<TransactionTotals> GetTotalsAsync(TransactionFilter filter, CancellationToken ct = default) => throw Unavailable();

    public Task<IReadOnlyList<string>> GetDistinctValuesAsync(DistinctField field, CancellationToken ct = default) => throw Unavailable();

    public void InvalidateDistinctValues() { }   // called after every import; harmless

    public Task<TransactionDetail?> GetDetailAsync(TransactionKey key, CancellationToken ct = default) => throw Unavailable();

    public Task<QueryExportResult> ExportAsync(TransactionFilter filter, ExportFormat format, string destinationPath,
        IProgress<long>? progress = null, CancellationToken ct = default, IReadOnlyList<string>? columns = null) => throw Unavailable();
}
