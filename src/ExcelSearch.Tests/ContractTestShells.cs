using Xunit.Abstractions;

namespace ExcelSearch.Tests;

// The contract tests (StagingStoreContract, CommitContract, RefreshContract, ExportContract, ImportPreviewContract) are written once.
// Each shell below runs one of them against one provider. A test appears twice in the results: ..._SqlServer and ..._Sqlite.
//
//   SQL Server  the development database, skipped when EXCELSEARCH_SKIP_DB_TESTS=1
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
        Skip.If(Environment.GetEnvironmentVariable(DbFactAttribute.SkipVariable) == "1",
            $"{DbFactAttribute.SkipVariable}=1: database tests skipped.");
        SkipUnlessLarge(large);
        return StagingEnv.Create();
    }

    public static StagingEnv Sqlite(SqliteDatabaseFixture db, bool large)
    {
        SkipUnlessLarge(large);
        return StagingEnv.CreateSqlite(db.Path);
    }
}

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
