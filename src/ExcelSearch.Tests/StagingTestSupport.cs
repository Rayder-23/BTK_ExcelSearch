using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data;
using ExcelSearch.Data.Entities;
using ExcelSearch.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExcelSearch.Tests;

/// <summary>
/// [Fact] for tests that use the development database. Skipped when EXCELSEARCH_SKIP_DB_TESTS=1 or EXCELSEARCH_TEST_CONNECTION is unset.
/// These tests create and delete rows (only their own, keys prefixed TEST_).
/// </summary>
public sealed class DbFactAttribute : FactAttribute
{
    public const string SkipVariable = "EXCELSEARCH_SKIP_DB_TESTS";

    public DbFactAttribute() => Skip = TestDatabase.SkipReason();
}

/// <summary>
/// Where the SQL Server (Integration) tests run. The ONLY source is the environment variable EXCELSEARCH_TEST_CONNECTION: the app's
/// configuration (appsettings.*.json, which will point at production) is never read. A database whose name contains "prod" is refused.
/// </summary>
public static class TestDatabase
{
    public const string ConnectionVariable = "EXCELSEARCH_TEST_CONNECTION";

    /// <summary>Why the SQL Server tests should be skipped, or null when they can run.</summary>
    public static string? SkipReason()
    {
        if (Environment.GetEnvironmentVariable(DbFactAttribute.SkipVariable) == "1")
            return $"{DbFactAttribute.SkipVariable}=1: database tests skipped.";
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            return $"{ConnectionVariable} is not set: SQL Server tests skipped.";
        return null;
    }

    /// <summary>The test connection string. Throws (the test fails, loudly) if the database looks like production.</summary>
    public static string ConnectionString()
    {
        var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException($"Set {ConnectionVariable} to the development database connection string, or set {DbFactAttribute.SkipVariable}=1.");

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        if (builder.InitialCatalog.Contains("prod", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to run: the database '{builder.InitialCatalog}' in {ConnectionVariable} looks like production. " +
                "The tests insert and delete rows; point the variable at a development database.");
        return cs;
    }
}

/// <summary>Database test that is also slow: skipped unless EXCELSEARCH_RUN_LARGE=1 (and not skipped by SKIP_DB_TESTS).</summary>
public sealed class LargeDbFactAttribute : FactAttribute
{
    public LargeDbFactAttribute()
    {
        if (TestDatabase.SkipReason() is { } reason) Skip = reason;
        else if (Environment.GetEnvironmentVariable(LargeFactAttribute.Variable) != "1")
            Skip = $"Slow. Set {LargeFactAttribute.Variable}=1 to run.";
    }
}

/// <summary>
/// A throw-away SQLite database for one test class: a fresh file in the temp folder, created with the real schema script, deleted
/// when the class is done. (Use as an xUnit class fixture.) The development SQL Server database is never involved.
/// </summary>
public sealed class SqliteDatabaseFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"excelsearch-test-{Guid.NewGuid():N}.db");

    public SqliteDatabaseFixture() => SqliteSchemaInitializer.Initialize(Path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // pooled connections keep the file (and its -wal/-shm files) open
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(Path + suffix); }
            catch (IOException) { }
        }
    }
}

/// <summary>What differs between the providers for the test helpers: how to clean up in bulk, and how to prove readers are not blocked.</summary>
public interface IProviderOps
{
    string Name { get; }
    Task<int> CountTransactionsWithPrefixAsync(AppDbContext db, string prefix);
    Task DeleteStagingAsync(AppDbContext db, int batchId);
    Task DeleteTransactionsWithPrefixAsync(AppDbContext db, string prefix);

    /// <summary>The mechanism that lets readers carry on while a commit writes (RCSI on SQL Server, WAL on SQLite), and whether it is on.</summary>
    Task<(bool Enabled, string Description)> ReaderIsolationAsync(AppDbContext db);
}

public sealed class SqlServerOps : IProviderOps
{
    public string Name => "SQL Server";

    public Task<int> CountTransactionsWithPrefixAsync(AppDbContext db, string prefix)
    {
        var pattern = prefix.Replace("_", "[_]") + "%";
        return db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.Transactions WHERE PayOrderNo LIKE {pattern}").SingleAsync();
    }

    public async Task DeleteStagingAsync(AppDbContext db, int batchId)
    {
        int n;
        do
        {
            n = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE TOP (50000) FROM dbo.ImportStaging WHERE ImportBatchId = {batchId}");
        } while (n > 0);
    }

    public async Task DeleteTransactionsWithPrefixAsync(AppDbContext db, string prefix)
    {
        // LIKE 'TEST[_]<guid>%' is a prefix match on the clustered key, with the underscore escaped (chunks: the large test commits 200,000).
        var pattern = prefix.Replace("_", "[_]") + "%";
        int n;
        do
        {
            n = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE TOP (50000) FROM dbo.Transactions WHERE PayOrderNo LIKE {pattern}");
        } while (n > 0);
    }

    public async Task<(bool, string)> ReaderIsolationAsync(AppDbContext db)
    {
        var on = await db.Database.SqlQuery<bool>(
            $"SELECT is_read_committed_snapshot_on AS Value FROM sys.databases WHERE database_id = DB_ID()").SingleAsync();
        return (on, $"is_read_committed_snapshot_on = {on}");
    }
}

public sealed class SqliteOps : IProviderOps
{
    public string Name => "SQLite";

    public Task<int> CountTransactionsWithPrefixAsync(AppDbContext db, string prefix) =>
        db.Transactions.CountAsync(t => t.PayOrderNo.StartsWith(prefix));

    public Task DeleteStagingAsync(AppDbContext db, int batchId) =>
        db.ImportStagings.Where(s => s.ImportBatchId == batchId).ExecuteDeleteAsync();

    public Task DeleteTransactionsWithPrefixAsync(AppDbContext db, string prefix) =>
        db.Transactions.Where(t => t.PayOrderNo.StartsWith(prefix)).ExecuteDeleteAsync();

    public async Task<(bool, string)> ReaderIsolationAsync(AppDbContext db)
    {
        var mode = await db.Database.SqlQueryRaw<string>("SELECT journal_mode AS Value FROM pragma_journal_mode").SingleAsync();
        return (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase), $"journal_mode = {mode}");
    }
}

/// <summary>
/// One test's view of a database (the development SQL Server database, or a temp SQLite file). It remembers every batch the
/// test creates and, when disposed (the finally of `await using`), deletes ONLY what the test created: its staging rows first,
/// then the Transactions rows whose keys carry this test's TEST_ prefix, then its batches.
/// </summary>
public sealed class StagingEnv : IAsyncDisposable
{
    private readonly List<int> _batches = new();
    private readonly object _lock = new();
    private readonly ServiceProvider _services;

    public IDbContextFactory<AppDbContext> Factory { get; }
    public IStagingStore Store { get; }
    public IProviderOps Ops { get; }
    public string ProviderName => Ops.Name;
    public bool IsSqlite => Ops is SqliteOps;

    /// <summary>Unique per test (TEST_ + GUID), so keys never collide with real data or parallel tests.</summary>
    public string Prefix { get; } = "TEST_" + Guid.NewGuid().ToString("N");

    private StagingEnv(ServiceProvider services, IProviderOps ops)
    {
        _services = services;
        Ops = ops;
        Factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        Store = services.GetRequiredService<IStagingStore>();
    }

    /// <summary>The SQL Server database named by EXCELSEARCH_TEST_CONNECTION (never the app configuration), wired like the app.</summary>
    public static StagingEnv Create(StagingStoreOptions? options = null) =>
        Build(new DatabaseOptions { Provider = DatabaseProvider.SqlServer }, LoadConnectionString(), options, new SqlServerOps());

    /// <summary>A SQLite database file that already exists (see <see cref="SqliteDatabaseFixture"/>), wired exactly like the app.</summary>
    public static StagingEnv CreateSqlite(string path, StagingStoreOptions? options = null) =>
        Build(new DatabaseOptions { Provider = DatabaseProvider.Sqlite, SqlitePath = path }, null, options, new SqliteOps());

    private static StagingEnv Build(DatabaseOptions database, string? sqlServerConnectionString, StagingStoreOptions? options, IProviderOps ops)
    {
        var services = new ServiceCollection();
        services.AddExcelSearchDatabase(database, sqlServerConnectionString, options ?? new StagingStoreOptions());
        return new StagingEnv(services.BuildServiceProvider(), ops);
    }

    private static string LoadConnectionString() => TestDatabase.ConnectionString();

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
        db.Transactions.Add(new Transaction
        {
            PayOrderNo = po,
            TxnRefSeqNo = tx,
            ImportBatchId = ownerBatchId,
            RowHash = rowHash,
            ValueDate = new DateOnly(2024, 1, 1),
        });
        await db.SaveChangesAsync();
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

    public async Task<Transaction?> GetTransactionAsync(string po, string tx)
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.PayOrderNo == po && t.TxnRefSeqNo == tx);
    }

    public async Task<int> StagingCountAsync(int batchId)
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.ImportStagings.CountAsync(s => s.ImportBatchId == batchId);
    }

    public async Task<ImportStaging> StagingRowAsync(int batchId, int excelRow)
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.ImportStagings.AsNoTracking().SingleAsync(s => s.ImportBatchId == batchId && s.ExcelRowNumber == excelRow);
    }

    public async Task<ImportBatch> GetBatchAsync(int batchId)
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.ImportBatches.AsNoTracking().SingleAsync(b => b.Id == batchId);
    }

    // ---- simulating "something else changed the database" (provider-neutral EF updates, always scoped by the caller) ----

    public async Task SetBatchStatusAsync(int batchId, string status)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.ImportBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, status));
    }

    public async Task SetTotalRowsAsync(int batchId, int totalRows)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.ImportBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.TotalRows, totalRows));
    }

    /// <summary>Makes the batch look <paramref name="hours"/> hours old.</summary>
    public async Task BackdateBatchAsync(int batchId, int hours)
    {
        var then = DateTime.UtcNow.AddHours(-hours);
        await using var db = await Factory.CreateDbContextAsync();
        await db.ImportBatches.Where(b => b.Id == batchId).ExecuteUpdateAsync(s => s.SetProperty(b => b.ImportedAt, then));
    }

    public async Task SetStagingOverwriteAsync(int batchId, int excelRow, bool overwrite)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.ImportStagings.Where(s => s.ImportBatchId == batchId && s.ExcelRowNumber == excelRow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Overwrite, overwrite));
    }

    /// <summary>Changes the RowHash of every Transactions row whose PayOrderNo is one of <paramref name="payOrderNos"/>.</summary>
    public async Task SetTransactionHashAsync(string rowHash, params string[] payOrderNos)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Transactions.Where(t => payOrderNos.Contains(t.PayOrderNo))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RowHash, rowHash));
    }

    public async Task DeleteTransactionAsync(string po)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Transactions.Where(t => t.PayOrderNo == po).ExecuteDeleteAsync();
    }

    public async Task<int> CountTransactionsWithPrefixAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await Ops.CountTransactionsWithPrefixAsync(db, Prefix);
    }

    /// <summary>Plain count of the whole table (what another user's screen would run), on its own connection.</summary>
    public async Task<int> CountAllTransactionsAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await db.Transactions.CountAsync();
    }

    public async Task<(bool Enabled, string Description)> ReaderIsolationAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        return await Ops.ReaderIsolationAsync(db);
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
        try
        {
            int[] ids;
            lock (_lock) ids = _batches.ToArray();
            if (ids.Length == 0) return;

            await using var db = await Factory.CreateDbContextAsync();
            db.Database.SetCommandTimeout(600);

            // 1. staging rows of this test's batches
            foreach (var id in ids) await Ops.DeleteStagingAsync(db, id);

            // 2. Transactions rows whose keys carry this test's unique prefix
            await Ops.DeleteTransactionsWithPrefixAsync(db, Prefix);

            // 3. the batches themselves
            await db.ImportBatches.Where(b => ids.Contains(b.Id)).ExecuteDeleteAsync();
        }
        finally
        {
            await _services.DisposeAsync();
        }
    }
}
