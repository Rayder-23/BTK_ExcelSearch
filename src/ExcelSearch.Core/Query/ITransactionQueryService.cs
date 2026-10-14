namespace ExcelSearch.Core.Query;

/// <summary>Limits shared by the service and the screen.</summary>
public static class QueryLimits
{
    /// <summary>An .xlsx sheet holds 1,048,576 rows; one is the header. Larger results can only be exported as CSV.</summary>
    public const int MaxExcelDataRows = 1_048_575;

    public const int DefaultPageSize = 100;
    public static readonly int[] PageSizes = { 50, 100, 200, 500 };
    public const int CommandTimeoutSeconds = 60;
    public const int ExportChunkSize = 10_000;
}

/// <summary>
/// Searching the imported transactions. Every method takes a filter as given (it is normalized inside), honours the
/// cancellation token, and reads without taking locks that block an import (READ_COMMITTED_SNAPSHOT is on).
/// </summary>
public interface ITransactionQueryService
{
    /// <summary>
    /// A page (1-based) of the filtered rows in the given order, plus the total number of matching rows. The page and
    /// the count are separate queries; use <see cref="GetPageAsync"/> and <see cref="CountAsync"/> directly when the
    /// screen wants to show the rows before the count arrives.
    /// </summary>
    Task<TransactionPage> SearchAsync(TransactionFilter filter, TransactionSort sort, int pageNumber, int pageSize,
        CancellationToken ct = default);

    /// <summary>Only the rows of one page (no count).</summary>
    Task<IReadOnlyList<TransactionRecord>> GetPageAsync(TransactionFilter filter, TransactionSort sort, int pageNumber,
        int pageSize, CancellationToken ct = default);

    /// <summary>How many rows match the filter.</summary>
    Task<int> CountAsync(TransactionFilter filter, CancellationToken ct = default);

    /// <summary>Sums of Credit, Debit and Down payment over every row that matches (not just one page).</summary>
    Task<TransactionTotals> GetTotalsAsync(TransactionFilter filter, CancellationToken ct = default);

    /// <summary>The sorted distinct non-blank values of a dropdown field. Cached; see <see cref="InvalidateDistinctValues"/>.</summary>
    Task<IReadOnlyList<string>> GetDistinctValuesAsync(DistinctField field, CancellationToken ct = default);

    /// <summary>Forgets the cached dropdown values so the next call re-reads them (call after an import commits).</summary>
    void InvalidateDistinctValues();

    /// <summary>One transaction with all its fields and where it was imported from; null if no transaction has this Id.</summary>
    Task<TransactionDetail?> GetDetailAsync(TransactionKey key, CancellationToken ct = default);

    /// <summary>
    /// Writes every row that matches the filter to a file, reading the database in Id-ordered chunks of 10,000 so memory
    /// stays flat however many rows there are. Xlsx is refused above <see cref="QueryLimits.MaxExcelDataRows"/> rows.
    /// <paramref name="columns"/> are TransactionRecord property names in the order to write them (null = all 30); a name that is
    /// not one of the 28 is rejected. The file is written next to the destination as ".part" and renamed when complete, so a cancelled or failed
    /// export never leaves a half-written file. Rows are in Id order.
    /// </summary>
    Task<QueryExportResult> ExportAsync(TransactionFilter filter, ExportFormat format, string destinationPath,
        IProgress<long>? progress = null, CancellationToken ct = default, IReadOnlyList<string>? columns = null);
}
