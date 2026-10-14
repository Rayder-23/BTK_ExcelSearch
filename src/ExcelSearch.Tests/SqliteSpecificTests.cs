using System.Text.RegularExpressions;
using ExcelSearch.Core.Query;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data;
using ExcelSearch.Data.Entities;
using ExcelSearch.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using static ExcelSearch.Tests.StagingEnv;

namespace ExcelSearch.Tests;

/// <summary>
/// What only SQLite has to prove: how values are stored (money as cents, ISO text), that comparisons and sorting happen in SQL, that
/// keys are case-insensitive, that the schema initializer is safe, and that the per-connection settings really are applied.
/// A temp database file per class (SqliteDatabaseFixture); every test uses its own TEST_ keys and removes only what it created.
/// </summary>
public class SqliteSpecificTests : IClassFixture<SqliteDatabaseFixture>
{
    private readonly SqliteDatabaseFixture _db;
    private readonly ITestOutputHelper _output;

    public SqliteSpecificTests(SqliteDatabaseFixture db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
    }

    private StagingEnv NewEnv(StagingStoreOptions? options = null) => StagingEnv.CreateSqlite(_db.Path, options);

    private static async Task<T> ScalarAsync<T>(StagingEnv env, string sql)
    {
        await using var db = await env.Factory.CreateDbContextAsync();
        return await db.Database.SqlQueryRaw<T>(sql).SingleAsync();
    }

    private static Transaction Tx(StagingEnv env, int batch, string key, decimal? debit = null, decimal? credit = null) => new()
    {
        PayOrderNo = env.Key(key),
        TxnRefSeqNo = "T1",
        ImportBatchId = batch,
        RowHash = new string('a', 64),
        ValueDate = new DateOnly(2024, 5, 6),
        Debit = debit,
        Credit = credit,
    };

    // ---- money ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Money_round_trips_through_cents_including_negatives_nulls_and_half_cent_rounding()
    {
        await using var env = NewEnv();
        var batch = await env.NewBatchAsync();

        // (stored value in the table is the exact value, rounded half away from zero to 2 places)
        var cases = new (string Key, decimal? Input, decimal? Expected, long? Cents)[]
        {
            ("tenth", 0.1m, 0.10m, 10),
            ("big", 1234567.89m, 1234567.89m, 123456789),
            ("half-up", 0.005m, 0.01m, 1),
            ("half-neg", -0.005m, -0.01m, -1),
            ("neg", -12.34m, -12.34m, -1234),
            ("zero", 0m, 0m, 0),
            ("round-down", 0.004m, 0m, 0),
            ("max", 9_999_999_999_999.99m, 9_999_999_999_999.99m, 999_999_999_999_999),
            ("null", null, null, null),
        };

        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            db.Transactions.AddRange(cases.Select(c => Tx(env, batch, c.Key, debit: c.Input, credit: c.Input)));
            await db.SaveChangesAsync();
        }

        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            foreach (var c in cases)
            {
                var po = env.Key(c.Key);
                var back = await db.Transactions.AsNoTracking().SingleAsync(t => t.PayOrderNo == po);
                Assert.Equal(c.Expected, back.Debit);
                Assert.Equal(c.Expected, back.Credit);
                if (c.Expected is not null) Assert.Equal(2, back.Debit!.Value.Scale);   // like SQL Server's decimal(18,2): 1 comes back as 1.00

                // What is really in the file: an INTEGER number of cents, not text and not a float.
                var raw = await db.Database.SqlQueryRaw<string>(
                    "SELECT typeof(Debit) || ':' || IFNULL(CAST(Debit AS TEXT), 'null') AS Value FROM Transactions WHERE PayOrderNo = {0}", po).SingleAsync();
                Assert.Equal(c.Cents is null ? "null:null" : $"integer:{c.Cents}", raw);
            }
        }
    }

    [Fact]
    public async Task Range_filters_ordering_and_sums_on_money_run_in_SQL_and_compare_numbers_not_text()
    {
        await using var env = NewEnv();
        var batch = await env.NewBatchAsync();
        // As TEXT these would sort 10 < 100 < 1000.50 < 9 < -5 style (lexically); as numbers they must not.
        var amounts = new[] { 9m, 10m, 100m, 1000.50m, -5m, 25.25m };
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            db.Transactions.AddRange(amounts.Select((a, i) => Tx(env, batch, "r" + i, debit: a)));
            await db.SaveChangesAsync();
        }

        await using var q = await env.Factory.CreateDbContextAsync();
        var mine = q.Transactions.AsNoTracking().Where(t => t.ImportBatchId == batch);

        // Range: 10 <= Debit < 1000. Text comparison would drop 100 and 25.25 ('9' > '10' but '25.25' > '1000.5' ...) or keep 9.
        var inRange = await mine.Where(t => t.Debit >= 10m && t.Debit < 1000m).OrderBy(t => t.Debit).Select(t => t.Debit).ToListAsync();
        Assert.Equal(new decimal?[] { 10m, 25.25m, 100m }, inRange);

        // The comparison is part of the SQL (a server-side WHERE on the integer column), not done in memory afterwards.
        var sql = mine.Where(t => t.Debit >= 10m && t.Debit < 1000m).OrderBy(t => t.Debit).ToQueryString();
        _output.WriteLine(sql);
        Assert.Contains("WHERE", sql);
        Assert.Contains("\"Debit\" >=", sql);
        Assert.Contains("ORDER BY", sql);

        var ascending = await mine.OrderBy(t => t.Debit).Select(t => t.Debit).ToListAsync();
        Assert.Equal(new decimal?[] { -5m, 9m, 10m, 25.25m, 100m, 1000.50m }, ascending);
        var descending = await mine.OrderByDescending(t => t.Debit).Select(t => t.Debit).ToListAsync();
        Assert.Equal(ascending.AsEnumerable().Reverse(), descending);

        // SUM in SQL over the integer column is exact (cents), where a TEXT column would be coerced and lose or mangle digits.
        var sumCents = await ScalarAsync<long>(env, $"SELECT SUM(Debit) AS Value FROM Transactions WHERE ImportBatchId = {batch}");
        Assert.Equal(113975L, sumCents);    // 9 + 10 + 100 + 1000.50 - 5 + 25.25 = 1139.75
    }

    // ---- keys -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Keys_differing_only_by_case_are_one_key_in_the_table_and_in_lookups()
    {
        await using var env = NewEnv();
        var batch = await env.NewBatchAsync();
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            db.Transactions.Add(new Transaction
            {
                PayOrderNo = env.Key("abc"), TxnRefSeqNo = "tx-1", ImportBatchId = batch,
                RowHash = new string('a', 64), ValueDate = new DateOnly(2024, 1, 1),
            });
            await db.SaveChangesAsync();
        }

        // A lookup in any case finds it ...
        Assert.NotNull(await env.GetTransactionAsync(env.Key("ABC"), "TX-1"));

        // ... and inserting the same key in another case is a duplicate (unique violation, SQLite error 19), like SQL Server's 2627.
        await using var db2 = await env.Factory.CreateDbContextAsync();
        db2.Transactions.Add(new Transaction
        {
            PayOrderNo = env.Key("ABC"), TxnRefSeqNo = "TX-1", ImportBatchId = batch,
            RowHash = new string('b', 64), ValueDate = new DateOnly(2024, 1, 1),
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        Assert.Equal(19, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
    }

    [Fact]
    public async Task A_staged_row_whose_key_differs_only_by_case_from_a_stored_row_is_an_exact_duplicate_not_New()
    {
        await using var env = NewEnv();
        var seedBatch = await env.NewBatchAsync();
        var shouted = Row(2, env.Key("ABC"), "T1", 5m);              // the key in capitals
        await env.SeedTransactionAsync(seedBatch, env.Key("abc"), "T1", shouted.RowHash);   // stored in lower case, same hash

        var id = await env.StageAsync(new[] { shouted, Row(3, env.Key("other"), "T1", 5m) });

        var counts = await env.Store.GetCountsAsync(id);
        Assert.Equal(1, counts[RowStatus.ExactDuplicate]);
        Assert.Equal(1, counts[RowStatus.New]);
        Assert.Equal(2, Assert.Single((await env.Store.GetPageAsync(id, RowStatus.ExactDuplicate, 1, 10)).Rows).ExcelRowNumber);
    }

    [Fact]
    public async Task Text_search_matches_percent_underscore_and_backslash_literally_and_ignores_ascii_case()
    {
        await using var env = NewEnv();
        var batch = await env.NewBatchAsync();
        var names = new[] { "100% Owner_X", "Owner X", "50 percent", @"C:\Tempile", "ALICE Khan" };
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            db.Transactions.AddRange(names.Select((n, i) => { var t = Tx(env, batch, "n" + i); t.CustomerName = n; return t; }));
            await db.SaveChangesAsync();
        }
        var svc = new ExcelSearch.Data.Sqlite.SqliteTransactionQueryService(env.Factory);
        async Task<string[]> Find(string text, TextMatchMode mode = TextMatchMode.Contains) =>
            (await svc.GetPageAsync(new TransactionFilter { ImportBatchId = batch, CustomerName = text, CustomerNameMode = mode },
                TransactionSort.Default, 1, 50)).Select(r => r.CustomerName!).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "100% Owner_X" }, await Find("%"));                 // % is not "match anything"
        Assert.Equal(new[] { "100% Owner_X" }, await Find("r_X"));               // _ is not "any one character" (Owner X must not match)
        Assert.Equal(new[] { @"C:\Tempile" }, await Find(@"\"));             // the escape character itself
        Assert.Equal(new[] { "ALICE Khan" }, await Find("alice", TextMatchMode.StartsWith));   // case-insensitive
        Assert.Equal(new[] { "ALICE Khan" }, await Find("khan"));
        Assert.Empty(await Find("lice", TextMatchMode.StartsWith));
    }

    // ---- stored forms of dates, timestamps and ids ----------------------------------------------------------------

    [Fact]
    public async Task Dates_timestamps_and_ids_are_stored_as_ISO_text_and_come_back_as_UTC()
    {
        await using var env = NewEnv();
        var id = await env.StageAsync(new[] { Row(2, env.Key("a"), "T1", 1m) });
        Assert.True((await env.Store.CommitAsync(id)).Success);

        const string iso = @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$";
        Assert.Matches(iso, await ScalarAsync<string>(env, $"SELECT CommittedAt AS Value FROM ImportBatches WHERE Id = {id}"));
        Assert.Matches(iso, await ScalarAsync<string>(env, $"SELECT ImportedAt AS Value FROM ImportBatches WHERE Id = {id}"));
        Assert.Matches(iso, await ScalarAsync<string>(env, $"SELECT CreatedAt AS Value FROM Transactions WHERE ImportBatchId = {id}"));
        Assert.Equal("2024-04-03", await ScalarAsync<string>(env, $"SELECT ValueDate AS Value FROM Transactions WHERE ImportBatchId = {id}"));

        var guid = await ScalarAsync<string>(env, $"SELECT Id AS Value FROM Transactions WHERE ImportBatchId = {id}");
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-4[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$", guid);
        Assert.Equal(guid, (await env.GetTransactionAsync(env.Key("a"), "T1"))!.Id.ToString().ToUpperInvariant());

        var batch = await env.GetBatchAsync(id);
        Assert.Equal(DateTimeKind.Utc, batch.CommittedAt!.Value.Kind);
        Assert.InRange(batch.CommittedAt.Value, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(1));
    }

    // ---- the connection settings ----------------------------------------------------------------------------------

    [Fact]
    public async Task Every_connection_has_foreign_keys_on_a_30_second_busy_timeout_and_synchronous_NORMAL_and_the_file_is_in_WAL_mode()
    {
        await using var env = NewEnv();

        Assert.Equal(1L, await ScalarAsync<long>(env, "SELECT foreign_keys AS Value FROM pragma_foreign_keys"));
        Assert.Equal(30000L, await ScalarAsync<long>(env, "SELECT timeout AS Value FROM pragma_busy_timeout"));
        Assert.Equal(1L, await ScalarAsync<long>(env, "SELECT synchronous AS Value FROM pragma_synchronous"));   // 1 = NORMAL
        Assert.Equal("wal", await ScalarAsync<string>(env, "SELECT journal_mode AS Value FROM pragma_journal_mode"));
    }

    [Fact]
    public async Task Foreign_keys_are_enforced_a_transaction_for_a_missing_batch_is_rejected()
    {
        await using var env = NewEnv();
        await using var db = await env.Factory.CreateDbContextAsync();
        db.Transactions.Add(Tx(env, batch: int.MaxValue, "orphan", debit: 1m));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(19, Assert.IsType<SqliteException>(ex.InnerException).SqliteErrorCode);
        Assert.Contains("FOREIGN KEY", ex.InnerException!.Message);
    }

    [Fact]
    public async Task A_commit_gives_up_with_LockTimeout_when_another_connection_holds_the_write_lock()
    {
        await using var env = NewEnv(new StagingStoreOptions { CommitLockTimeoutSeconds = 1 });
        var id = await env.StageAsync(new[] { Row(2, env.Key("a"), "T1") });

        // Another writer (a different connection) takes the database's write lock and keeps it.
        using var blocker = new SqliteConnection(SqliteSchemaInitializer.BuildConnectionString(_db.Path));
        blocker.Open();
        Exec(blocker, "BEGIN IMMEDIATE");
        try
        {
            var started = DateTime.UtcNow;
            var result = await env.Store.CommitAsync(id);
            var waited = DateTime.UtcNow - started;
            _output.WriteLine($"Commit gave up after {waited.TotalSeconds:F1}s: {result.Message}");

            Assert.False(result.Success);
            Assert.Equal(CommitFailure.LockTimeout, result.FailureReason);
            Assert.Equal("Another import is committing, please try again in a moment.", result.Message);
            Assert.InRange(waited.TotalSeconds, 0.8, 10);
        }
        finally
        {
            Exec(blocker, "ROLLBACK");
        }

        // Once the lock is free the same batch commits normally (nothing was half-done).
        Assert.True((await env.Store.CommitAsync(id)).Success);
    }

    // ---- the schema initializer -----------------------------------------------------------------------------------

    [Fact]
    public void Upgrading_a_version_1_database_keeps_its_rows_and_lets_rows_without_a_key_in()
    {
        var path = TempDbPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string v1;
            using (var s = typeof(SqliteSchemaInitializer).Assembly.GetManifestResourceStream("ExcelSearch.Sqlite.001_create_schema.sql")!)
            using (var reader = new StreamReader(s)) v1 = reader.ReadToEnd();

            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                Exec(c, v1);                                                     // a database as the first release made it
                Exec(c, "INSERT INTO ImportBatches (FileName, FileHash) VALUES ('old.xlsx', 'h')");
                Exec(c, "INSERT INTO Transactions (PayOrderNo, TxnRefSeqNo, ImportBatchId, RowHash, ValueDate, Credit) VALUES ('PO-1', 'T1', 1, 'x', '2024-01-01', 12550)");
                Assert.Equal(1L, (long)Scalar(c, "PRAGMA user_version")!);
            }

            Assert.Equal(1, SqliteSchemaInitializer.Initialize(path));           // it returns the version it found
            Assert.Equal(SqliteSchemaInitializer.LatestVersion, UserVersion(path));

            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                Assert.Equal("PO-1", (string)Scalar(c, "SELECT PayOrderNo FROM Transactions")!);   // the old row kept its key and its money
                Assert.Equal(12550L, (long)Scalar(c, "SELECT Credit FROM Transactions")!);
                Assert.Equal(36, ((string)Scalar(c, "SELECT Id FROM Transactions")!).Length);       // and has an Id

                // Rows without a key: any number of them, even identical.
                for (var i = 0; i < 2; i++)
                    Exec(c, "INSERT INTO Transactions (ImportBatchId, RowHash, ValueDate, SlipNo) VALUES (1, 'same', '2024-01-02', '12345678')");
                Assert.Equal(2L, (long)Scalar(c, "SELECT COUNT(*) FROM Transactions WHERE PayOrderNo IS NULL")!);

                // A full key is still unique, ignoring case.
                var ex = Assert.Throws<SqliteException>(() =>
                    Exec(c, "INSERT INTO Transactions (PayOrderNo, TxnRefSeqNo, ImportBatchId, RowHash, ValueDate) VALUES ('po-1', 't1', 1, 'y', '2024-01-01')"));
                Assert.Equal(19, ex.SqliteErrorCode);                            // SQLITE_CONSTRAINT
            }
        }
        finally { Cleanup(path); }
    }

    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"excelsearch-init-{Guid.NewGuid():N}", "nested", "x.db");

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static long UserVersion(string path)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        return (long)Scalar(c, "PRAGMA user_version")!;
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Initializer_creates_the_folder_the_file_the_tables_and_sets_the_version_and_WAL()
    {
        var path = TempDbPath();
        try
        {
            Assert.False(File.Exists(path));
            Assert.Equal(0, SqliteSchemaInitializer.Initialize(path));      // 0 = it was a new database

            Assert.True(File.Exists(path));
            Assert.Equal(SqliteSchemaInitializer.LatestVersion, UserVersion(path));
            using var c = new SqliteConnection($"Data Source={path};Pooling=False");
            c.Open();
            Assert.Equal("wal", (string)Scalar(c, "PRAGMA journal_mode")!);
            var tables = new List<string>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            Assert.Equal(new[] { "ImportBatches", "ImportStaging", "Transactions" }, tables);
            Assert.Equal(18L, (long)Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX_%'")!);   // 2 batches + 14 transactions + 2 staging (UX_Transactions_Key is the unique one)
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Initializer_is_idempotent_a_second_run_changes_nothing_and_keeps_the_data()
    {
        var path = TempDbPath();
        try
        {
            SqliteSchemaInitializer.Initialize(path);
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                Exec(c, "INSERT INTO ImportBatches (FileName, FileHash, Status) VALUES ('keep.xlsx', 'h', 'Committed')");
            }

            Assert.Equal(SqliteSchemaInitializer.LatestVersion, SqliteSchemaInitializer.Initialize(path));   // found = current version
            Assert.Equal(SqliteSchemaInitializer.LatestVersion, UserVersion(path));
            using var c2 = new SqliteConnection($"Data Source={path};Pooling=False");
            c2.Open();
            Assert.Equal(1L, (long)Scalar(c2, "SELECT COUNT(*) FROM ImportBatches WHERE FileName = 'keep.xlsx'")!);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Initializer_refuses_a_database_with_a_newer_user_version_and_leaves_it_untouched()
    {
        var path = TempDbPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var newer = SqliteSchemaInitializer.LatestVersion + 1;
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                Exec(c, $"CREATE TABLE FromTheFuture (x INTEGER); PRAGMA user_version = {newer};");
            }

            var ex = Assert.Throws<InvalidOperationException>(() => SqliteSchemaInitializer.Initialize(path));
            Assert.Contains($"schema version {newer}", ex.Message);
            Assert.Contains("newer", ex.Message);

            Assert.Equal(newer, UserVersion(path));                        // version untouched
            using var c2 = new SqliteConnection($"Data Source={path};Pooling=False");
            c2.Open();
            Assert.Equal("FromTheFuture", (string)Scalar(c2, "SELECT name FROM sqlite_master WHERE type = 'table'")!);   // no ExcelSearch tables added
            Assert.NotEqual("wal", (string)Scalar(c2, "PRAGMA journal_mode")!);                                         // journal mode not changed either
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Initializer_refuses_a_non_empty_database_that_is_not_an_ExcelSearch_one()
    {
        var path = TempDbPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                Exec(c, "CREATE TABLE somebody_elses (x INTEGER)");
            }

            var ex = Assert.Throws<InvalidOperationException>(() => SqliteSchemaInitializer.Initialize(path));
            Assert.Contains("not an ExcelSearch database", ex.Message);
            Assert.Equal(0L, UserVersion(path));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Provider_setting_is_parsed_and_the_sqlite_path_default_and_environment_variables_work()
    {
        Assert.Equal(DatabaseProvider.SqlServer, DatabaseOptions.ParseProvider(null));
        Assert.Equal(DatabaseProvider.SqlServer, DatabaseOptions.ParseProvider(""));
        Assert.Equal(DatabaseProvider.Sqlite, DatabaseOptions.ParseProvider("sqlite"));
        Assert.Equal(DatabaseProvider.SqlServer, DatabaseOptions.ParseProvider(" SqlServer "));
        Assert.Throws<InvalidOperationException>(() => DatabaseOptions.ParseProvider("postgres"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "ExcelSearch", "excelsearch.db"), new DatabaseOptions().ResolveSqlitePath());
        Assert.Equal(Path.Combine(local, "x", "y.db"),
            new DatabaseOptions { SqlitePath = @"%LocalAppData%\x\y.db" }.ResolveSqlitePath());
        Assert.True(Path.IsPathRooted(new DatabaseOptions { SqlitePath = "relative.db" }.ResolveSqlitePath()));
    }
}
