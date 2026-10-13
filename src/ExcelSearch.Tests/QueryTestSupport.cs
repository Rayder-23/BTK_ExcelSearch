using ExcelSearch.Core.Query;
using ExcelSearch.Data;
using ExcelSearch.Data.Sqlite;
using ExcelSearch.Data.SqlServer;
using ExcelSearch.Data.Entities;

namespace ExcelSearch.Tests;

/// <summary>
/// Seeds Transactions rows for the query tests. Every key starts with the test's own TEST_ prefix (so StagingEnv cleans
/// them up) and every test scopes its searches to its own import batch, so rows from real data never leak into a result.
/// </summary>
internal static class QuerySeed
{
    public sealed record Seeded(int BatchId, IReadOnlyList<Transaction> Rows);

    /// <summary>
    /// 30 predictable rows (i = 1..30): three rows share each value date (2024-01-01 .. 2024-01-10), banks cycle through
    /// three values, credit is null for every tenth row, down payment is set for every fifth, and a few names are awkward
    /// (a LIKE wildcard, a comma, a quote and a line break).
    /// </summary>
    public static async Task<Seeded> SeedStandardAsync(StagingEnv env, int count = 30)
    {
        var batch = await env.NewBatchAsync();
        var rows = Enumerable.Range(1, count).Select(i => Standard(env.Prefix, batch, i)).ToList();
        await InsertAsync(env, rows);
        return new Seeded(batch, rows);
    }

    public static Transaction Standard(string prefix, int batchId, int i) => new()
    {
        PayOrderNo = $"{prefix}-PO{i:000}",
        TxnRefSeqNo = $"TX{i:000}",
        ImportBatchId = batchId,
        SourceRowNumber = i + 1,
        RowHash = new string('a', 64),
        CreatedAt = DateTime.UtcNow,
        ValueDate = new DateOnly(2024, 1, 1).AddDays((i - 1) / 3),
        BranchCode = $"BC{i % 5}",
        BranchName = $"{prefix} Br{i % 5}",
        RegNo = $"REG{i:000}",
        ApplicationNo = $"APP-{i:000}",
        PlotNo = $"P-{i}",
        StreetNo = $"S-{i}",
        ChallanNo = $"CH{i:000}",
        ChequeInstNo = $"CQ{i:000}",
        CustomerName = i switch
        {
            7 => "100% Owner_X",
            8 => "Khan, \"Ali\"\nJr",
            _ when i % 2 == 1 => $"Ali Khan {i}",
            _ => $"Sara Ahmed {i}",
        },
        Cnic = $"4210{i:D9}",
        Discount = i % 4 == 0 ? 5m : null,
        DownPaymentAmount = i % 5 == 0 ? i * 1000m : null,
        Debit = (30 - i) * 10m,
        Credit = i % 10 == 0 ? null : i * 100m,
        RunningBalance = i * 1.5m,
        BankName = $"{prefix} {new[] { "HBL", "UBL", "Meezan" }[i % 3]}",
        AccountNo = $"ACC{i:000}",
        Project = $"{prefix} {(i % 2 == 0 ? "Alpha" : "Beta")}",
        DealerName = $"{prefix} D{i % 4}",
        DataSource = $"{prefix} Src{(i % 2 == 0 ? "A" : "B")}",
        Narration1 = $"Narration one {i}",
        Narration2 = $"Narration two {i}",
        Narration3 = i % 3 == 0 ? $"Narration three {i}" : null,
    };

    public static async Task InsertAsync(StagingEnv env, IEnumerable<Transaction> rows)
    {
        await using var db = await env.Factory.CreateDbContextAsync();
        db.Transactions.AddRange(rows);
        await db.SaveChangesAsync();
    }

    /// <summary>The query service of the env's provider (SQL Server or SQLite), with the normal limits.</summary>
    public static TransactionQueryServiceBase Service(StagingEnv env) => Create(env);

    /// <summary>The query service of the env's provider, with the knobs the tests turn (clock, cache lifetime, time limit, sheet size).</summary>
    public static TransactionQueryServiceBase Create(StagingEnv env, TimeProvider? clock = null, TimeSpan? distinctCacheLifetime = null,
        int commandTimeoutSeconds = QueryLimits.CommandTimeoutSeconds, int maxExcelDataRows = QueryLimits.MaxExcelDataRows) =>
        env.IsSqlite
            ? new SqliteTransactionQueryService(env.Factory, null, clock, distinctCacheLifetime, commandTimeoutSeconds, maxExcelDataRows)
            : new SqlTransactionQueryService(env.Factory, null, clock, distinctCacheLifetime, commandTimeoutSeconds, maxExcelDataRows);
}

/// <summary>A clock the test can move forward, to prove the dropdown cache expires.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
