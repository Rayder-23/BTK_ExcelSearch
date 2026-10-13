using System.Linq.Expressions;
using System.Reflection;
using ExcelSearch.Core.Query;
using ExcelSearch.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.Data.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="ITransactionQueryService"/>. Everything is in <see cref="TransactionQueryServiceBase"/>; what
/// differs here is only what SQLite does differently from SQL Server:
///  * Text matching: EF's Contains/StartsWith become <c>instr()</c>/<c>=</c>, which are case-SENSITIVE on SQLite. Text filters use
///    <c>LIKE</c> with an ESCAPE character instead (case-insensitive for ASCII letters, like the SQL Server collation), and the typed
///    text has % _ and the escape character escaped, so "50%" or "a_b" are matched literally.
///  * Text ordering: ORDER BY a text column uses COLLATE NOCASE, so "alice" sorts next to "Alice" as it does on SQL Server.
///  * Multi-select lists (bank, project, ...): the column is compared as <c>COLLATE NOCASE</c>, so "hbl" finds "HBL".
///  * Money: nothing special here. The model's value converter stores money as integer cents, so range filters, ORDER BY and SUM() run
///    in SQL on integers and the converter turns the SUM back into a decimal.
/// </summary>
public sealed class SqliteTransactionQueryService : TransactionQueryServiceBase
{
    private const string EscapeChar = "\\";
    private const int SqliteBusy = 5;

    private static readonly MethodInfo LikeMethod = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like),
        new[] { typeof(DbFunctions), typeof(string), typeof(string), typeof(string) })!;

    private static readonly MethodInfo CollateMethod = typeof(RelationalDbFunctionsExtensions).GetMethods()
        .Single(m => m.Name == nameof(RelationalDbFunctionsExtensions.Collate)).MakeGenericMethod(typeof(string));

    public SqliteTransactionQueryService(IDbContextFactory<AppDbContext> factory, ILogger<SqliteTransactionQueryService>? logger = null,
        TimeProvider? clock = null, TimeSpan? distinctCacheLifetime = null,
        int commandTimeoutSeconds = QueryLimits.CommandTimeoutSeconds, int maxExcelDataRows = QueryLimits.MaxExcelDataRows)
        : base(factory, logger, clock, distinctCacheLifetime, commandTimeoutSeconds, maxExcelDataRows)
    {
    }

    /// <summary>Escapes the LIKE wildcards (% and _) and the escape character itself, so the text is matched literally.</summary>
    internal static string EscapeLike(string value) =>
        value.Replace(EscapeChar, EscapeChar + EscapeChar).Replace("%", EscapeChar + "%").Replace("_", EscapeChar + "_");

    protected override Expression<Func<Transaction, bool>> TextMatch(Expression<Func<Transaction, string?>> column, string value, TextMatchMode mode)
    {
        var escaped = EscapeLike(value);
        var pattern = mode switch
        {
            TextMatchMode.Exact => escaped,            // no wildcard: LIKE then means "equal, ignoring case"
            TextMatchMode.Contains => "%" + escaped + "%",
            _ => escaped + "%",
        };
        var body = Expression.Call(LikeMethod, Expression.Constant(EF.Functions), column.Body, Param(pattern), Expression.Constant(EscapeChar));
        return Expression.Lambda<Func<Transaction, bool>>(body, column.Parameters);
    }

    protected override IOrderedQueryable<Transaction> ByText(IQueryable<Transaction> q, Expression<Func<Transaction, string?>> key, bool descending)
    {
        // EF.Functions.Collate(column, "NOCASE") renders as  column COLLATE NOCASE
        var collated = Expression.Lambda<Func<Transaction, string?>>(
            Expression.Call(CollateMethod, Expression.Constant(EF.Functions), key.Body, Expression.Constant("NOCASE")), key.Parameters);
        return descending ? q.OrderByDescending(collated) : q.OrderBy(collated);
    }

    protected override Expression<Func<Transaction, bool>> AnyOf(Expression<Func<Transaction, string?>> column, IReadOnlyList<string> values) =>
        AnyOfCore(Expression.Call(CollateMethod, Expression.Constant(EF.Functions), column.Body, Expression.Constant("NOCASE")), column.Parameters, values);

    protected override IOrderedQueryable<string> OrderValues(IQueryable<string> values) =>
        values.OrderBy(v => EF.Functions.Collate(v, "NOCASE")).ThenBy(v => v);

    // SQLite has no statement time limit; the only "too long" is waiting for another connection's lock (SQLITE_BUSY).
    protected override bool IsTimeout(Exception ex) => ex is SqliteException { SqliteErrorCode: SqliteBusy };
}
