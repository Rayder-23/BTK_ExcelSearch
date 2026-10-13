using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ExcelSearch.Data.Sqlite;

/// <summary>
/// Runs the per-connection PRAGMAs every time EF opens a SQLite connection. These settings belong to the connection, not
/// to the database file, so a pooled or new connection that skips them would silently run with the SQLite defaults:
///   foreign_keys = ON      SQLite ignores FOREIGN KEY clauses unless this is on, per connection
///   busy_timeout = 30000   wait up to 30 s for another writer instead of failing at once with "database is locked"
///   synchronous = NORMAL   with WAL: still safe against corruption, and far fewer fsyncs than FULL
/// (journal_mode = WAL, the one database-wide setting, is applied once by <see cref="SqliteSchemaInitializer"/>.
/// synchronous is per connection too, which is why it is here and not there.)
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public const string ConnectionPragmas = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000; PRAGMA synchronous = NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ConnectionPragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = ConnectionPragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
