using System.Linq.Expressions;
using System.Reflection;
using ExcelSearch.Core.Query;
using ExcelSearch.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.Data.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="ITransactionQueryService"/>. Everything is in <see cref="TransactionQueryServiceBase"/>;
/// this class only says how SQL Server matches text (plain StartsWith / Contains / equality, which the case-insensitive database
/// collation already makes case-insensitive, and StartsWith can use an index) and which error is a timeout.
/// </summary>
public sealed class SqlTransactionQueryService : TransactionQueryServiceBase
{
    private const int SqlTimeoutErrorNumber = -2;

    private static readonly MethodInfo StartsWithMethod = typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) })!;
    private static readonly MethodInfo ContainsMethod = typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!;

    public SqlTransactionQueryService(IDbContextFactory<AppDbContext> factory, ILogger<SqlTransactionQueryService>? logger = null,
        TimeProvider? clock = null, TimeSpan? distinctCacheLifetime = null,
        int commandTimeoutSeconds = QueryLimits.CommandTimeoutSeconds, int maxExcelDataRows = QueryLimits.MaxExcelDataRows)
        : base(factory, logger, clock, distinctCacheLifetime, commandTimeoutSeconds, maxExcelDataRows)
    {
    }

    protected override Expression<Func<Transaction, bool>> TextMatch(Expression<Func<Transaction, string?>> column, string value, TextMatchMode mode)
    {
        var parameter = Param(value);
        Expression body = mode switch
        {
            TextMatchMode.Exact => Expression.Equal(column.Body, parameter),
            TextMatchMode.Contains => Expression.Call(column.Body, ContainsMethod, parameter),
            _ => Expression.Call(column.Body, StartsWithMethod, parameter),
        };
        return Expression.Lambda<Func<Transaction, bool>>(body, column.Parameters);
    }

    protected override bool IsTimeout(Exception ex) => ex is SqlException { Number: SqlTimeoutErrorNumber };
}
