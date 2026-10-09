using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExcelSearch.Tests;

/// <summary>
/// [Fact] for tests that use the development database. Skipped when EXCELSEARCH_SKIP_DB_TESTS=1.
/// These tests create and delete rows (only their own, keys prefixed TEST_).
/// </summary>
public sealed class DbFactAttribute : FactAttribute
{
    public const string SkipVariable = "EXCELSEARCH_SKIP_DB_TESTS";

    public DbFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(SkipVariable) == "1")
            Skip = $"{SkipVariable}=1: database tests skipped.";
    }
}

/// <summary>Database test that is also slow: skipped unless EXCELSEARCH_RUN_LARGE=1 (and not skipped by SKIP_DB_TESTS).</summary>
public sealed class LargeDbFactAttribute : FactAttribute
{
    public LargeDbFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(DbFactAttribute.SkipVariable) == "1")
            Skip = $"{DbFactAttribute.SkipVariable}=1: database tests skipped.";
        else if (Environment.GetEnvironmentVariable(LargeFactAttribute.Variable) != "1")
            Skip = $"Slow. Set {LargeFactAttribute.Variable}=1 to run.";
    }
}

/// <summary>
/// One test's view of the development database. It remembers every batch the test creates and, when disposed
/// (the finally of `await using`), deletes ONLY what the test created: its staging rows first, then the
/// Transactions rows whose keys carry this test's TEST_ prefix, then its batches.
/// </summary>
internal sealed class StagingEnv : IAsyncDisposable
{
    private readonly List<int> _batches = new();
    private readonly object _lock = new();

    public IDbContextFactory<AppDbContext> Factory { get; }
    public SqlServerStagingStore Store { get; }

    /// <summary>Unique per test (TEST_ + GUID), so keys never collide with real data or parallel tests.</summary>
    public string Prefix { get; } = "TEST_" + Guid.NewGuid().ToString("N");

    private StagingEnv(IDbContextFactory<AppDbContext> factory)
    {
        Factory = factory;
        Store = new SqlServerStagingStore(factory);
    }

    public static StagingEnv Create()
    {
        var cs = LoadConnectionString();
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(cs));
        var factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<AppDbContext>>();
        return new StagingEnv(factory);
    }

    /// <summary>
    /// Same configuration as the app: src/ExcelSearch/appsettings.json, then appsettings.Development.json
    /// (gitignored), then environment variables (ConnectionStrings__DefaultConnection). Nothing is hard-coded.
    /// </summary>
    private static string LoadConnectionString()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ExcelSearch.sln"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Could not find the repository root (ExcelSearch.sln).");

        var appDir = Path.Combine(dir.FullName, "src", "ExcelSearch");
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(appDir, "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(appDir, "appsettings.Development.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "No connection string. Create src/ExcelSearch/appsettings.Development.json (see the .example file) " +
                "or set ConnectionStrings__DefaultConnection, or set EXCELSEARCH_SKIP_DB_TESTS=1 to skip database tests.");
        return cs;
    }

    public string Key(string suffix) => $"{Prefix}-{suffix}";

    public async Task<int> NewBatchAsync()
    {
        var id = await Store.CreateBatchAsync(Prefix + ".xlsx", "Sheet1", new string('a', 64), "TEST\\user");
        TrackBatch(id);
        return id;
    }

    /// <summary>Register a batch created elsewhere (e.g. by ImportPreviewService) for cleanup.</summary>
    public void TrackBatch(int id)
    {
        lock (_lock) _batches.Add(id);
    }

    /// <summary>Inserts a row straight into Transactions, as if an earlier import had committed it.</summary>
    public async Task SeedTransactionAsync(int ownerBatchId, string po, string tx, string rowHash)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT dbo.Transactions (PayOrderNo, TxnRefSeqNo, ImportBatchId, RowHash, ValueDate)
            VALUES ({po}, {tx}, {ownerBatchId}, {rowHash}, '2024-01-01')
            """);
    }

    public async Task<int> CountTransactionsAsync(int ownerBatchId)
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.Transactions.CountAsync(t => t.ImportBatchId == ownerBatchId);
    }

    public async Task<string?> BatchStatusAsync(int batchId)
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.ImportBatches.Where(b => b.Id == batchId).Select(b => b.Status).SingleOrDefaultAsync();
    }

    /// <summary>Builds a valid row with a real RowHash; <paramref name="debit"/> is what makes two rows differ.</summary>
    public static ImportRow Row(int excelRow, string po, string tx, decimal debit = 0m, bool invalid = false)
    {
        var row = new ImportRow
        {
            ExcelRowNumber = excelRow,
            PayOrderNo = po,
            TxnRefSeqNo = tx,
            ValueDate = new DateTime(2024, 4, 3),
            Debit = debit,
            CustomerName = "Customer",
        };
        if (invalid) row.Errors.Add(new RowError("Debit", "Debit is not a valid amount"));
        row.RowHash = RowHasher.Compute(row);
        return row;
    }

    /// <summary>Stage in-memory rows exactly as the preview service would: load, then classify.</summary>
    public async Task<int> StageAsync(IEnumerable<ImportRow> rows)
    {
        var id = await NewBatchAsync();
        await Store.LoadRowsAsync(id, rows, null);
        await Store.ClassifyAsync(id);
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        int[] ids;
        lock (_lock) ids = _batches.ToArray();
        if (ids.Length == 0) return;

        await using var db = await Factory.CreateDbContextAsync();
        db.Database.SetCommandTimeout(600);
        var prefix = Prefix;

        // 1. staging rows of this test's batches (in chunks, like DiscardAsync)
        foreach (var id in ids)
        {
            int n;
            do
            {
                n = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE TOP (50000) FROM dbo.ImportStaging WHERE ImportBatchId = {id}");
            } while (n > 0);
        }

        // 2. Transactions rows this test seeded: its own batches AND keys carrying its unique prefix
        await db.Transactions
            .Where(t => ids.Contains(t.ImportBatchId) && t.PayOrderNo.StartsWith(prefix))
            .ExecuteDeleteAsync();

        // 3. the batches themselves
        await db.ImportBatches.Where(b => ids.Contains(b.Id)).ExecuteDeleteAsync();
    }
}
