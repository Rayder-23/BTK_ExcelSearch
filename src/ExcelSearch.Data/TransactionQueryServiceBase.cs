using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using ExcelSearch.Core.Query;
using ExcelSearch.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.Data;

/// <summary>
/// The Query screen's search, written once for every provider. The filter is turned into one LINQ query that EF translates to a
/// parameterized SELECT (no SQL text is ever built from user input). Reads are AsNoTracking, use a fresh short-lived context each,
/// wait at most 60 seconds, and stop when the cancellation token fires. Money filters, sorting and totals are plain LINQ on the
/// decimal properties; on SQLite EF turns them into integer-cents SQL through the model's value converter.
/// A provider overrides only what SQL does differently: how text is matched, how text is ordered, and what a "timeout" error is.
/// </summary>
public abstract class TransactionQueryServiceBase : ITransactionQueryService
{
    private static readonly TimeSpan DefaultDistinctCacheLifetime = TimeSpan.FromMinutes(10);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger? _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _distinctLifetime;
    private readonly int _commandTimeoutSeconds;
    private readonly int _maxExcelDataRows;

    // Dropdown values: one cached load per field. A new load replaces a stale/failed one; Invalidate empties the cache.
    private readonly object _cacheLock = new();
    private readonly Dictionary<DistinctField, CacheEntry> _distinctCache = new();

    private sealed record CacheEntry(Task<IReadOnlyList<string>> Values, DateTimeOffset LoadedAt);

    protected TransactionQueryServiceBase(IDbContextFactory<AppDbContext> factory, ILogger? logger, TimeProvider? clock,
        TimeSpan? distinctCacheLifetime, int commandTimeoutSeconds, int maxExcelDataRows)
    {
        _factory = factory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _distinctLifetime = distinctCacheLifetime ?? DefaultDistinctCacheLifetime;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _maxExcelDataRows = maxExcelDataRows;
    }

    // ---- what a provider decides -------------------------------------------------------------------------------

    /// <summary>
    /// column matches <paramref name="value"/> by <paramref name="mode"/>, case-insensitively like the SQL Server database, with the value
    /// as a query parameter (use <see cref="Param"/>). The value is already trimmed and never empty.
    /// </summary>
    protected abstract Expression<Func<Transaction, bool>> TextMatch(Expression<Func<Transaction, string?>> column, string value, TextMatchMode mode);

    /// <summary>The column's value is one of <paramref name="values"/> (an IN list of parameters), ignoring case like SQL Server.</summary>
    protected virtual Expression<Func<Transaction, bool>> AnyOf(Expression<Func<Transaction, string?>> column, IReadOnlyList<string> values) =>
        AnyOfCore(column.Body, column.Parameters, values);

    /// <summary>Builds <c>values.Contains(operand)</c> with the array as a query parameter.</summary>
    protected static Expression<Func<Transaction, bool>> AnyOfCore(Expression operand, IReadOnlyList<ParameterExpression> parameters, IReadOnlyList<string> values)
    {
        var array = Expression.Field(Expression.Constant(new Box<string[]>(values.ToArray())), nameof(Box<string[]>.Value));
        var contains = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), new[] { typeof(string) }, array, operand);
        return Expression.Lambda<Func<Transaction, bool>>(contains, parameters);
    }

    /// <summary>ORDER BY on a text column in the same (case-insensitive) order SQL Server gives.</summary>
    protected virtual IOrderedQueryable<Transaction> ByText(IQueryable<Transaction> q, Expression<Func<Transaction, string?>> key, bool descending) =>
        descending ? q.OrderByDescending(key) : q.OrderBy(key);

    /// <summary>The dropdown values in alphabetical, case-insensitive order.</summary>
    protected virtual IOrderedQueryable<string> OrderValues(IQueryable<string> values) => values.OrderBy(v => v);

    /// <summary>True when the database error means "the query took too long / the database is busy".</summary>
    protected abstract bool IsTimeout(Exception ex);

    // ---- search ----------------------------------------------------------------------------------------------

    public async Task<TransactionPage> SearchAsync(TransactionFilter filter, TransactionSort sort, int pageNumber, int pageSize,
        CancellationToken ct = default)
    {
        // Rows first, then the count: two separate statements (the screen runs them separately so rows show sooner).
        var rows = await GetPageAsync(filter, sort, pageNumber, pageSize, ct).ConfigureAwait(false);
        var total = await CountAsync(filter, ct).ConfigureAwait(false);
        return new TransactionPage(rows, total, Math.Max(1, pageNumber), pageSize);
    }

    public Task<IReadOnlyList<TransactionRecord>> GetPageAsync(TransactionFilter filter, TransactionSort sort, int pageNumber,
        int pageSize, CancellationToken ct = default)
    {
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var page = Math.Max(1, pageNumber);
        var f = filter.Normalized();
        return Run<IReadOnlyList<TransactionRecord>>(async db =>
            await Build(db.Transactions.AsNoTracking(), f, sort)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(ToRecord)
                .ToListAsync(ct).ConfigureAwait(false), ct);
    }

    public Task<int> CountAsync(TransactionFilter filter, CancellationToken ct = default)
    {
        var f = filter.Normalized();
        return Run(db => Apply(db.Transactions.AsNoTracking(), f).CountAsync(ct), ct);
    }

    public Task<TransactionTotals> GetTotalsAsync(TransactionFilter filter, CancellationToken ct = default)
    {
        var f = filter.Normalized();
        return Run(async db =>
        {
            // One pass over the matching rows computes all three sums. Grouping by a constant gives no row at all
            // when nothing matches, which means "all zero".
            var sums = await Apply(db.Transactions.AsNoTracking(), f)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Credit = g.Sum(t => t.Credit),
                    Debit = g.Sum(t => t.Debit),
                    Down = g.Sum(t => t.DownPaymentAmount),
                })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return sums is null
                ? new TransactionTotals(0m, 0m, 0m)
                : new TransactionTotals(sums.Credit ?? 0m, sums.Debit ?? 0m, sums.Down ?? 0m);
        }, ct);
    }

    // ---- dropdown values -------------------------------------------------------------------------------------

    public Task<IReadOnlyList<string>> GetDistinctValuesAsync(DistinctField field, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(field)) throw new ArgumentOutOfRangeException(nameof(field));
        Task<IReadOnlyList<string>> task;
        lock (_cacheLock)
        {
            var now = _clock.GetUtcNow();
            if (!_distinctCache.TryGetValue(field, out var entry) || now - entry.LoadedAt > _distinctLifetime
                || entry.Values.IsFaulted || entry.Values.IsCanceled)
            {
                // The shared load is not tied to this caller's token: if this caller gives up, others still get the list.
                entry = new CacheEntry(LoadDistinctAsync(field), now);
                _distinctCache[field] = entry;
            }
            task = entry.Values;
        }
        return task.WaitAsync(ct);
    }

    public void InvalidateDistinctValues()
    {
        lock (_cacheLock) _distinctCache.Clear();
    }

    private Task<IReadOnlyList<string>> LoadDistinctAsync(DistinctField field) =>
        Run<IReadOnlyList<string>>(async db =>
        {
            var q = db.Transactions.AsNoTracking();
            var values = field switch
            {
                DistinctField.BankName => q.Where(t => t.BankName != null && t.BankName != "").Select(t => t.BankName!),
                DistinctField.Project => q.Where(t => t.Project != null && t.Project != "").Select(t => t.Project!),
                DistinctField.DealerName => q.Where(t => t.DealerName != null && t.DealerName != "").Select(t => t.DealerName!),
                DistinctField.BranchName => q.Where(t => t.BranchName != null && t.BranchName != "").Select(t => t.BranchName!),
                DistinctField.DataSource => q.Where(t => t.DataSource != null && t.DataSource != "").Select(t => t.DataSource!),
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
            return await OrderValues(values.Distinct()).ToListAsync().ConfigureAwait(false);
        }, CancellationToken.None);

    // ---- one transaction -------------------------------------------------------------------------------------

    public Task<TransactionDetail?> GetDetailAsync(TransactionKey key, CancellationToken ct = default) =>
        Run(async db =>
        {
            var id = key.Id;
            return await db.Transactions.AsNoTracking()
                .Where(t => t.Id == id)
                .Select(t => new TransactionDetail(
                    new TransactionRecord(t.Id, t.PayOrderNo, t.TxnRefSeqNo, t.ValueDate, t.BranchCode, t.BranchName, t.RegNo,
                        t.ApplicationNo, t.PlotNo, t.StreetNo, t.ChallanNo, t.ChequeInstNo, t.CustomerName, t.Cnic, t.Discount,
                        t.DownPaymentAmount, t.Debit, t.Credit, t.RunningBalance, t.BankName, t.AccountNo, t.Project,
                        t.DealerName, t.DataSource, t.Narration1, t.Narration2, t.Narration3, t.Narration4, t.Narration5,
                        t.SlipNo, t.NarrationCnic),
                    new ImportProvenance(t.ImportBatchId, t.ImportBatch.FileName, t.ImportBatch.SheetName,
                        t.ImportBatch.ImportedAt, t.ImportBatch.ImportedBy, t.SourceRowNumber, t.CreatedAt, t.UpdatedAt)))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }, ct);

    // ---- export ----------------------------------------------------------------------------------------------

    public async Task<QueryExportResult> ExportAsync(TransactionFilter filter, ExportFormat format, string destinationPath,
        IProgress<long>? progress = null, CancellationToken ct = default, IReadOnlyList<string>? columns = null)
    {
        if (columns is not null && columns.Count == 0) throw new ArgumentException("Choose at least one column to export.", nameof(columns));
        if (format == ExportFormat.Xlsx)
        {
            var total = await CountAsync(filter, ct).ConfigureAwait(false);
            if (total > _maxExcelDataRows)
                return new QueryExportResult(false, 0, format, null,
                    $"{total:N0} rows match, but an Excel sheet holds at most {_maxExcelDataRows:N0} data rows. Export as CSV instead.");
        }

        var written = await TransactionExportWriter.WriteAsync(StreamChunksAsync(filter, QueryLimits.ExportChunkSize, ct),
            format, destinationPath, progress, ct, columns).ConfigureAwait(false);
        return new QueryExportResult(true, written, format, destinationPath, null);
    }

    /// <summary>
    /// Every matching row in Id order, <paramref name="chunkSize"/> at a time. Each chunk asks for the rows after
    /// the last Id already seen (keyset paging), so the cost per chunk is the same on the first and the thousandth chunk
    /// and nothing is held between chunks.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<TransactionRecord>> StreamChunksAsync(TransactionFilter filter, int chunkSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var f = filter.Normalized();
        TransactionKey? last = null;
        while (true)
        {
            var after = last;
            var rows = await Run<IReadOnlyList<TransactionRecord>>(async db =>
            {
                var q = Apply(db.Transactions.AsNoTracking(), f);
                if (after is { } k)
                {
                    var id = k.Id;
                    q = q.Where(t => t.Id.CompareTo(id) > 0);
                }
                return await q.OrderBy(t => t.Id).Take(chunkSize)
                    .Select(ToRecord).ToListAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (rows.Count == 0) yield break;
            yield return rows;
            if (rows.Count < chunkSize) yield break;
            last = rows[^1].Key;
        }
    }

    // ---- building the query ----------------------------------------------------------------------------------

    private static readonly Expression<Func<Transaction, TransactionRecord>> ToRecord = t => new TransactionRecord(
        t.Id, t.PayOrderNo, t.TxnRefSeqNo, t.ValueDate, t.BranchCode, t.BranchName, t.RegNo, t.ApplicationNo, t.PlotNo, t.StreetNo,
        t.ChallanNo, t.ChequeInstNo, t.CustomerName, t.Cnic, t.Discount, t.DownPaymentAmount, t.Debit, t.Credit,
        t.RunningBalance, t.BankName, t.AccountNo, t.Project, t.DealerName, t.DataSource,
        t.Narration1, t.Narration2, t.Narration3, t.Narration4, t.Narration5, t.SlipNo, t.NarrationCnic);

    /// <summary>The filtered, ordered query (what a page reads). Exposed so tests can inspect the SQL.</summary>
    internal IQueryable<Transaction> Build(IQueryable<Transaction> q, TransactionFilter normalizedFilter, TransactionSort sort) =>
        Order(Apply(q, normalizedFilter), sort);

    /// <summary>Adds one Where per filter that is set. Expects a normalized filter. Values reach SQL only as parameters.</summary>
    protected IQueryable<Transaction> Apply(IQueryable<Transaction> q, TransactionFilter f)
    {
        if (f.ValueDateFrom is { } from) q = q.Where(t => t.ValueDate >= from);
        if (f.ValueDateTo is { } to) q = q.Where(t => t.ValueDate <= to);

        // The CNIC filter finds a row by its CNIC column OR by the CNIC found in the narrations at import.
        if (!f.Cnic.IsEmpty)
            q = q.Where(Or(TextMatch(t => t.Cnic, f.Cnic.Value!, f.Cnic.Mode), TextMatch(t => t.NarrationCnic, f.Cnic.Value!, f.Cnic.Mode)));
        q = Text(q, f.ApplicationNo, t => t.ApplicationNo);
        q = Text(q, f.RegNo, t => t.RegNo);
        q = Text(q, f.PayOrderNo, t => t.PayOrderNo);
        q = Text(q, f.TxnRefSeqNo, t => t.TxnRefSeqNo);
        q = Text(q, f.SlipNo, t => t.SlipNo);
        q = Text(q, f.ChallanNo, t => t.ChallanNo);
        q = Text(q, f.ChequeInstNo, t => t.ChequeInstNo);
        q = Text(q, f.AccountNo, t => t.AccountNo);

        if (f.CustomerName is { } name) q = q.Where(TextMatch(t => t.CustomerName, name, f.CustomerNameMode));

        if (f.BankNames.Count > 0) q = q.Where(AnyOf(t => t.BankName, f.BankNames));
        if (f.Projects.Count > 0) q = q.Where(AnyOf(t => t.Project, f.Projects));
        if (f.DealerNames.Count > 0) q = q.Where(AnyOf(t => t.DealerName, f.DealerNames));
        if (f.BranchNames.Count > 0) q = q.Where(AnyOf(t => t.BranchName, f.BranchNames));
        if (f.DataSources.Count > 0) q = q.Where(AnyOf(t => t.DataSource, f.DataSources));

        if (f.CreditMin is { } cmin) q = q.Where(t => t.Credit >= cmin);
        if (f.CreditMax is { } cmax) q = q.Where(t => t.Credit <= cmax);
        if (f.DebitMin is { } dmin) q = q.Where(t => t.Debit >= dmin);
        if (f.DebitMax is { } dmax) q = q.Where(t => t.Debit <= dmax);
        if (f.DownPaymentMin is { } pmin) q = q.Where(t => t.DownPaymentAmount >= pmin);
        if (f.DownPaymentMax is { } pmax) q = q.Where(t => t.DownPaymentAmount <= pmax);

        if (f.ImportBatchId is { } batch) q = q.Where(t => t.ImportBatchId == batch);
        return q;
    }

    // The value is wrapped in a small object and read from its field, which is how EF recognises "a parameter" in an
    // expression it did not see as a C# closure. Without it the text would be baked into the SQL as a literal.
    private sealed class Box<T>
    {
        public Box(T value) => Value = value;
        public T Value;
    }

    /// <summary>The expression that makes <paramref name="value"/> a query parameter (see the note on the box above).</summary>
    protected static Expression Param(string value) =>
        Expression.Field(Expression.Constant(new Box<string>(value)), nameof(Box<string>.Value));

    private IQueryable<Transaction> Text(IQueryable<Transaction> q, TextCriterion c, Expression<Func<Transaction, string?>> column) =>
        c.IsEmpty ? q : q.Where(TextMatch(column, c.Value!, c.Mode));

    /// <summary>
    /// The sort whitelist: each allowed column maps to a fixed expression; nothing from the caller reaches the SQL text.
    /// Ties fall back to the row's Id (in the same direction as the sort) so the order is total and paging is stable.
    /// </summary>
    protected IQueryable<Transaction> Order(IQueryable<Transaction> q, TransactionSort sort)
    {
        var d = sort.Descending;
        IOrderedQueryable<Transaction> o = sort.Column switch
        {
            TransactionColumn.ValueDate => By(q, t => t.ValueDate, d),
            TransactionColumn.PayOrderNo => By(q, t => t.PayOrderNo, d),
            TransactionColumn.TxnRefSeqNo => By(q, t => t.TxnRefSeqNo, d),
            TransactionColumn.CustomerName => ByText(q, t => t.CustomerName, d),
            TransactionColumn.Cnic => ByText(q, t => t.Cnic, d),
            TransactionColumn.ApplicationNo => ByText(q, t => t.ApplicationNo, d),
            TransactionColumn.RegNo => ByText(q, t => t.RegNo, d),
            TransactionColumn.ChallanNo => ByText(q, t => t.ChallanNo, d),
            TransactionColumn.ChequeInstNo => ByText(q, t => t.ChequeInstNo, d),
            TransactionColumn.BankName => ByText(q, t => t.BankName, d),
            TransactionColumn.AccountNo => ByText(q, t => t.AccountNo, d),
            TransactionColumn.Project => ByText(q, t => t.Project, d),
            TransactionColumn.DealerName => ByText(q, t => t.DealerName, d),
            TransactionColumn.BranchName => ByText(q, t => t.BranchName, d),
            TransactionColumn.DataSource => ByText(q, t => t.DataSource, d),
            TransactionColumn.Debit => By(q, t => t.Debit, d),
            TransactionColumn.Credit => By(q, t => t.Credit, d),
            TransactionColumn.DownPaymentAmount => By(q, t => t.DownPaymentAmount, d),
            TransactionColumn.RunningBalance => By(q, t => t.RunningBalance, d),
            TransactionColumn.SlipNo => ByText(q, t => t.SlipNo, d),
            TransactionColumn.Discount => By(q, t => t.Discount, d),
            TransactionColumn.BranchCode => ByText(q, t => t.BranchCode, d),
            TransactionColumn.PlotNo => ByText(q, t => t.PlotNo, d),
            TransactionColumn.StreetNo => ByText(q, t => t.StreetNo, d),
            TransactionColumn.Narration1 => ByText(q, t => t.Narration1, d),
            TransactionColumn.Narration2 => ByText(q, t => t.Narration2, d),
            TransactionColumn.Narration3 => ByText(q, t => t.Narration3, d),
            TransactionColumn.Narration4 => ByText(q, t => t.Narration4, d),
            TransactionColumn.Narration5 => ByText(q, t => t.Narration5, d),
            TransactionColumn.NarrationCnic => ByText(q, t => t.NarrationCnic, d),
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort.Column, "Not a sortable column."),
        };

        // Tie-breaker: Id, the primary key (rows have no other unique value: PayOrderNo / TxnRefSeqNo are optional).
        // It follows the main direction on purpose: an index on (ValueDate) also holds the clustered key (Id), so it can be read
        // backwards for "newest first" only if the tie-break runs backwards too. (Measured with the old key: 2.4 s with an
        // ascending tie-break on 500,000 rows because everything had to be sorted; see Docs/Performance.md.)
        return d ? o.ThenByDescending(t => t.Id) : o.ThenBy(t => t.Id);
    }

    /// <summary>a OR b over the same row, as one expression EF can translate.</summary>
    private static Expression<Func<Transaction, bool>> Or(Expression<Func<Transaction, bool>> a, Expression<Func<Transaction, bool>> b)
    {
        var p = Expression.Parameter(typeof(Transaction), "t");
        var body = Expression.OrElse(new ParameterReplacer(a.Parameters[0], p).Visit(a.Body)!,
                                     new ParameterReplacer(b.Parameters[0], p).Visit(b.Body)!);
        return Expression.Lambda<Func<Transaction, bool>>(body, p);
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _from, _to;
        public ParameterReplacer(ParameterExpression from, ParameterExpression to) { _from = from; _to = to; }
        protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : base.VisitParameter(node);
    }

    private static IOrderedQueryable<Transaction> By<TKey>(IQueryable<Transaction> q, Expression<Func<Transaction, TKey>> key, bool descending) =>
        descending ? q.OrderByDescending(key) : q.OrderBy(key);

    // ---- running a query -------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one query on its own context with the 60 second limit. A database timeout becomes a
    /// <see cref="QueryTimeoutException"/> (the screen words it for the user); a cancelled query always surfaces as
    /// <see cref="OperationCanceledException"/> even when the driver reports it as a database exception.
    /// </summary>
    internal async Task<T> Run<T>(Func<AppDbContext, Task<T>> query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.Database.SetCommandTimeout(_commandTimeoutSeconds);
            return await query(db).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            _logger?.LogWarning(ex, "A transaction query exceeded {Seconds} seconds.", _commandTimeoutSeconds);
            throw new QueryTimeoutException(ex);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException(ct);
        }
    }
}
