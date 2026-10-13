namespace ExcelSearch.Data;

/// <summary>Which database engine the app runs on. Chosen once at startup from configuration; a restart switches it.</summary>
public enum DatabaseProvider
{
    SqlServer = 0,
    Sqlite = 1,
}

/// <summary>
/// The "Database" section of appsettings: <c>Database:Provider</c> ("SqlServer" or "Sqlite", SQL Server when missing) and
/// <c>Database:SqlitePath</c> (the local file; default %LocalAppData%\ExcelSearch\excelsearch.db).
/// </summary>
public sealed class DatabaseOptions
{
    public const string DefaultSqliteRelativePath = @"ExcelSearch\excelsearch.db";

    public DatabaseProvider Provider { get; init; } = DatabaseProvider.SqlServer;

    /// <summary>As configured; may contain environment variables such as %LocalAppData%. Null/empty means the default.</summary>
    public string? SqlitePath { get; init; }

    public bool IsSqlite => Provider == DatabaseProvider.Sqlite;

    /// <summary>Parses the provider name (case-insensitive). Null or empty is SQL Server; anything else unknown is an error.</summary>
    public static DatabaseProvider ParseProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DatabaseProvider.SqlServer;
        if (Enum.TryParse<DatabaseProvider>(value.Trim(), ignoreCase: true, out var p) && Enum.IsDefined(p)) return p;
        throw new InvalidOperationException(
            $"Database:Provider is '{value}'. Use \"SqlServer\" or \"Sqlite\".");
    }

    /// <summary>The absolute path of the SQLite file: environment variables expanded, default applied, folder NOT created yet.</summary>
    public string ResolveSqlitePath()
    {
        var raw = string.IsNullOrWhiteSpace(SqlitePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultSqliteRelativePath)
            : Environment.ExpandEnvironmentVariables(SqlitePath.Trim());
        return Path.GetFullPath(raw);
    }
}
