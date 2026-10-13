using ExcelSearch.Core.Query;
using ExcelSearch.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExcelSearch.Tests;

/// <summary>
/// The parts of the query service that only make sense on SQL Server: the generated SQL text, and a statement that runs long
/// (WAITFOR DELAY) to prove cancellation in flight and the 60 second limit.
/// </summary>
[Trait("Category", "Integration")]
public class QueryServiceSqlServerTests
{
    [DbFact]
    public async Task Filter_values_travel_as_sql_parameters_and_cannot_inject()
    {
        await using var env = StagingEnv.Create();
        var seed = await QuerySeed.SeedStandardAsync(env, 6);
        var svc = QuerySeed.Service(env);
        const string attack = "x'; DROP TABLE dbo.Transactions; --";

        var filter = new TransactionFilter
        {
            ApplicationNo = TextCriterion.StartsWith(attack),
            PayOrderNo = TextCriterion.Exact(attack),
            CustomerName = attack,
            BankNames = new[] { attack },
        }.Normalized();

        // The SQL text EF would send: values are @parameters, never part of the text.
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            var sql = svc.Build(db.Transactions.AsNoTracking(), filter, TransactionSort.Default).ToQueryString();
            // Everything except the "DECLARE @p = value;" lines at the top (those do carry the values).
            var body = string.Join("\n", sql.Split('\n').Where(l => !l.TrimStart().StartsWith("DECLARE ", StringComparison.Ordinal)));
            Console.WriteLine(sql);
            Assert.DoesNotContain("DROP TABLE", body);
            Assert.DoesNotContain(attack, body);
            Assert.Contains("@", body);
            Assert.Contains("LIKE", body);                // "starts with" stays a LIKE (it can use an index), not LEFT()/CHARINDEX()
        }

        var result = await svc.SearchAsync(filter with { ImportBatchId = seed.BatchId }, TransactionSort.Default, 1, 10);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(6, (await svc.CountAsync(new TransactionFilter { ImportBatchId = seed.BatchId })));   // the table is still there
    }

    [DbFact]
    public async Task A_running_query_is_cancelled_in_flight()
    {
        await using var env = StagingEnv.Create();
        var svc = QuerySeed.Service(env);
        using var cts = new CancellationTokenSource();

        // A statement that would take 20 seconds; the token fires after a quarter of a second.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var running = svc.Run(db => db.Database.ExecuteSqlRawAsync("WAITFOR DELAY '00:00:20'", cts.Token), cts.Token);
        cts.CancelAfter(300);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {started.Elapsed.TotalSeconds:0.0}s.");
    }

    [DbFact]
    public async Task A_query_over_the_time_limit_becomes_a_query_timeout()
    {
        await using var env = StagingEnv.Create();
        var svc = QuerySeed.Create(env, commandTimeoutSeconds: 1);

        var ex = await Assert.ThrowsAsync<QueryTimeoutException>(() =>
            svc.Run(db => db.Database.ExecuteSqlRawAsync("WAITFOR DELAY '00:00:10'"), CancellationToken.None));
        Assert.IsAssignableFrom<Microsoft.Data.SqlClient.SqlException>(ex.InnerException);
    }
}
