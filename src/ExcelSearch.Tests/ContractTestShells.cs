using Xunit.Abstractions;

namespace ExcelSearch.Tests;

// The contract tests (StagingStoreContract, CommitContract, RefreshContract, ExportContract, ImportPreviewContract) are written once.
// Each shell below runs one of them against one provider. A test appears twice in the results: ..._SqlServer and ..._Sqlite.
//
//   SQL Server  the database in EXCELSEARCH_TEST_CONNECTION; skipped when it is unset or EXCELSEARCH_SKIP_DB_TESTS=1
//   SQLite      a fresh temp file per test class (SqliteDatabaseFixture), deleted afterwards; never skipped by that variable
//
// The 200,000-row tests (large: true) are skipped on both unless EXCELSEARCH_RUN_LARGE=1.

public static class ContractEnv
{
    private static void SkipUnlessLarge(bool large) =>
        Skip.If(large && Environment.GetEnvironmentVariable(LargeFactAttribute.Variable) != "1",
            $"Slow. Set {LargeFactAttribute.Variable}=1 to run.");

    public static StagingEnv SqlServer(bool large)
    {
        Skip.If(TestDatabase.SkipReason() is not null, TestDatabase.SkipReason());
        SkipUnlessLarge(large);
        return StagingEnv.Create();
    }

    public static StagingEnv Sqlite(SqliteDatabaseFixture db, bool large)
    {
        SkipUnlessLarge(large);
        return StagingEnv.CreateSqlite(db.Path);
    }
}

[Trait("Category", "Integration")]
public sealed class StagingStoreTests_SqlServer : StagingStoreContract
{
    public StagingStoreTests_SqlServer(ITestOutputHelper output) : base(output) { }
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.SqlServer(large);
}

public sealed class StagingStoreTests_Sqlite : StagingStoreContract, IClassFixture<SqliteDatabaseFixture>
{
    private readonly SqliteDatabaseFixture _db;
    public StagingStoreTests_Sqlite(ITestOutputHelper output, SqliteDatabaseFixture db) : base(output) => _db = db;
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.Sqlite(_db, large);
}

[Trait("Category", "Integration")]
public sealed class ImportPreviewTests_SqlServer : ImportPreviewContract
{
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.SqlServer(large);
}

public sealed class ImportPreviewTests_Sqlite : ImportPreviewContract, IClassFixture<SqliteDatabaseFixture>
{
    private readonly SqliteDatabaseFixture _db;
    public ImportPreviewTests_Sqlite(SqliteDatabaseFixture db) => _db = db;
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.Sqlite(_db, large);
}

[Trait("Category", "Integration")]
public sealed class CommitTests_SqlServer : CommitContract
{
    public CommitTests_SqlServer(ITestOutputHelper output) : base(output) { }
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.SqlServer(large);
}

public sealed class CommitTests_Sqlite : CommitContract, IClassFixture<SqliteDatabaseFixture>
{
    private readonly SqliteDatabaseFixture _db;
    public CommitTests_Sqlite(ITestOutputHelper output, SqliteDatabaseFixture db) : base(output) => _db = db;
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.Sqlite(_db, large);
}

[Trait("Category", "Integration")]
public sealed class RefreshTests_SqlServer : RefreshContract
{
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.SqlServer(large);
}

public sealed class RefreshTests_Sqlite : RefreshContract, IClassFixture<SqliteDatabaseFixture>
{
    private readonly SqliteDatabaseFixture _db;
    public RefreshTests_Sqlite(SqliteDatabaseFixture db) => _db = db;
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.Sqlite(_db, large);
}

[Trait("Category", "Integration")]
public sealed class ExportDbTests_SqlServer : ExportContract
{
    public ExportDbTests_SqlServer(ITestOutputHelper output) : base(output) { }
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.SqlServer(large);
}

public sealed class ExportDbTests_Sqlite : ExportContract, IClassFixture<SqliteDatabaseFixture>
{
    private readonly SqliteDatabaseFixture _db;
    public ExportDbTests_Sqlite(ITestOutputHelper output, SqliteDatabaseFixture db) : base(output) => _db = db;
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.Sqlite(_db, large);
}

[Trait("Category", "Integration")]
public sealed class QueryServiceTests_SqlServer : QueryServiceContract
{
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.SqlServer(large);
}

public sealed class QueryServiceTests_Sqlite : QueryServiceContract, IClassFixture<SqliteDatabaseFixture>
{
    private readonly SqliteDatabaseFixture _db;
    public QueryServiceTests_Sqlite(SqliteDatabaseFixture db) => _db = db;
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.Sqlite(_db, large);
}

[Trait("Category", "Integration")]
public sealed class QueryLargeTests_SqlServer : QueryLargeContract
{
    public QueryLargeTests_SqlServer(ITestOutputHelper output) : base(output) { }
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.SqlServer(large);
}

public sealed class QueryLargeTests_Sqlite : QueryLargeContract, IClassFixture<SqliteDatabaseFixture>
{
    private readonly SqliteDatabaseFixture _db;
    public QueryLargeTests_Sqlite(ITestOutputHelper output, SqliteDatabaseFixture db) : base(output) => _db = db;
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.Sqlite(_db, large);
}

[Trait("Category", "Integration")]
public sealed class KeylessTests_SqlServer : KeylessContract
{
    public KeylessTests_SqlServer(ITestOutputHelper output) : base(output) { }
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.SqlServer(large);
}

public sealed class KeylessTests_Sqlite : KeylessContract, IClassFixture<SqliteDatabaseFixture>
{
    private readonly SqliteDatabaseFixture _db;
    public KeylessTests_Sqlite(ITestOutputHelper output, SqliteDatabaseFixture db) : base(output) => _db = db;
    protected override StagingEnv NewEnv(bool large = false) => ContractEnv.Sqlite(_db, large);
}
