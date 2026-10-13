using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ExcelSearch.Data.Sqlite;

/// <summary>
/// Creates the SQLite database file when it is missing and applies the schema scripts (Scripts/db/sqlite/NNN_*.sql, embedded in
/// this assembly). The schema version is the file's <c>PRAGMA user_version</c>; each script ends by setting it to its own number.
/// A file that is empty gets every script; a file at an older version gets only the newer ones; a file that is NEWER than the
/// newest embedded script (it was written by a later ExcelSearch) is refused, so an old build cannot damage it.
/// </summary>
public static class SqliteSchemaInitializer
{
    private const string ResourcePrefix = "ExcelSearch.Sqlite.";

    /// <summary>The newest schema version this build knows.</summary>
    public static int LatestVersion => Scripts().Max(s => s.Version);

    /// <summary>The connection string every SQLite connection of the app uses (the EF context and the initializer).</summary>
    public static string BuildConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Default,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    /// <summary>
    /// Makes the database at <paramref name="path"/> ready: creates the folder and file, switches the file to WAL, applies the scripts.
    /// Returns the schema version found before this call (0 for a new database). Throws if the file is newer than this build,
    /// or is a non-empty database that is not an ExcelSearch one.
    /// </summary>
    public static int Initialize(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Pooling off: this connection is closed for real when we finish, so the file is not held open.
        var cs = new SqliteConnectionStringBuilder(BuildConnectionString(path)) { Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 30000;");

        var found = Scalar(connection, "PRAGMA user_version;");
        var latest = LatestVersion;
        if (found > latest)
            throw new InvalidOperationException(
                $"The database '{path}' has schema version {found}, but this version of ExcelSearch only understands up to {latest}. " +
                "It was created by a newer ExcelSearch. Update the application; the file was not changed.");

        if (found == 0 && Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';") > 0)
            throw new InvalidOperationException(
                $"The file '{path}' already contains tables but has no ExcelSearch schema version, so it is not an ExcelSearch database. " +
                "Choose another Database:SqlitePath. The file was not changed.");

        // The one database-wide setting: it is stored in the file, so setting it again is harmless.
        Scalar(connection, "PRAGMA journal_mode = WAL;");

        foreach (var script in Scripts().Where(s => s.Version > found).OrderBy(s => s.Version))
        {
            Execute(connection, script.Sql);   // each script runs its own BEGIN IMMEDIATE ... COMMIT and sets user_version
            var now = Scalar(connection, "PRAGMA user_version;");
            if (now != script.Version)
                throw new InvalidOperationException($"Schema script {script.Name} did not set user_version to {script.Version} (it is {now}).");
        }

        return (int)found;
    }

    private sealed record Script(int Version, string Name, string Sql);

    private static List<Script> Scripts()
    {
        var assembly = typeof(SqliteSchemaInitializer).Assembly;
        var list = new List<Script>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var m = Regex.Match(name[ResourcePrefix.Length..], @"^(\d+)_");
            if (!m.Success) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add(new Script(int.Parse(m.Groups[1].Value), name[ResourcePrefix.Length..], reader.ReadToEnd()));
        }
        if (list.Count == 0) throw new InvalidOperationException("No embedded SQLite schema scripts were found.");
        return list;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is long l ? l : 0;   // PRAGMA journal_mode returns text ("wal"), which is not used
    }
}
