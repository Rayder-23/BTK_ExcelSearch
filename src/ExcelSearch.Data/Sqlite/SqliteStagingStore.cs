using System.Diagnostics;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExcelSearch.Data.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IStagingStore"/>, with the same statuses, classification rules, guards, failure reasons
/// and messages as the SQL Server store. The EF-only methods are shared through <see cref="StagingStoreBase"/>; this class
/// holds what needs hand-written SQL. Every statement filters on ImportBatchId.
///
/// What is different inside, and why it is not visible outside:
///  * SQLite has ONE writer at a time (per database file). Instead of SQL Server's application lock and row locks, a commit or a
///    re-check starts with BEGIN IMMEDIATE, which takes the write lock up front; a second writer waits (busy_timeout) or, for a commit,
///    gives up with the same LockTimeout result. A discard's conditional UPDATE needs the write lock too, so it still waits for a
///    running commit and then sees 'Committed' - the same ordering guarantee as before.
///  * Because there is only one writer, nothing is split into chunks to avoid lock escalation: a classify, re-check or delete is one statement.
///  * Rows are written with plain ADO.NET in one transaction and one prepared INSERT, not through EF.
///  * Waiting for another connection's lock is SQLite's busy handler (PRAGMA busy_timeout, 30 s on every connection) followed by
///    Microsoft.Data.Sqlite retrying until the command's CommandTimeout (default 30 s); SQLite has no limit on how long a running
///    statement may take. So 30 s is the wait everywhere except the commit's BEGIN IMMEDIATE, which uses CommitLockTimeoutSeconds.
/// </summary>
public sealed partial class SqliteStagingStore : StagingStoreBase
{
    private const int ProgressEvery = 5_000;

    /// <summary>The SQL for "now" as ISO 8601 UTC text, the same format the EF timestamp converter reads and writes.</summary>
    internal const string NowSql = "strftime('%Y-%m-%dT%H:%M:%fZ', 'now')";

    private readonly ILogger<SqliteStagingStore>? _logger;
    private readonly StagingStoreOptions _options;

    public SqliteStagingStore(IDbContextFactory<AppDbContext> factory, ILogger<SqliteStagingStore>? logger = null,
        StagingStoreOptions? options = null) : base(factory)
    {
        _logger = logger;
        _options = options ?? new StagingStoreOptions();
    }

    private static readonly System.Reflection.MethodInfo CollateMethod = typeof(RelationalDbFunctionsExtensions).GetMethods()
        .Single(m => m.Name == nameof(RelationalDbFunctionsExtensions.Collate)).MakeGenericMethod(typeof(string));

    /// <summary>Preview sorting on a text column ignores case ("alice" next to "Alice"), as the SQL Server collation does.</summary>
    protected override IOrderedQueryable<Entities.ImportStaging> ByText(IQueryable<Entities.ImportStaging> q,
        System.Linq.Expressions.Expression<Func<Entities.ImportStaging, string?>> key, bool descending)
    {
        var collated = System.Linq.Expressions.Expression.Lambda<Func<Entities.ImportStaging, string?>>(
            System.Linq.Expressions.Expression.Call(CollateMethod, System.Linq.Expressions.Expression.Constant(EF.Functions), key.Body,
                System.Linq.Expressions.Expression.Constant("NOCASE")), key.Parameters);
        return descending ? q.OrderByDescending(collated) : q.OrderBy(collated);
    }

    // ---- load ------------------------------------------------------------------------------------------------

    public override Task<LoadResult> LoadRowsAsync(int batchId, IEnumerable<ImportRow> rows, IProgress<int>? progress,
        CancellationToken ct = default) => LoadAsync(batchId, rows, progress, ct);

    private async Task<LoadResult> LoadAsync(int batchId, IEnumerable<ImportRow> rows, IProgress<int>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int loaded;

        try
        {
            // Plain ADO.NET on the connection of a context from the factory (so the connection string and the per-connection
            // PRAGMAs stay in one place). The loop is synchronous (SQLite has no async I/O), so it runs on a pool thread.
            loaded = await Task.Run(async () =>
            {
                await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
                var conn = (SqliteConnection)db.Database.GetDbConnection();

                // One transaction for the whole file: it is either all in staging or (on failure/cancel) none of it.
                // BEGIN IMMEDIATE takes the write lock now, so a busy database fails or waits here, not half way through.
                Exec(conn, "BEGIN IMMEDIATE");
                var done = false;
                try
                {
                    int count;
                    using (var insert = BuildInsertCommand(conn))
                    using (var reader = new ImportRowDataReader(batchId, rows))
                    {
                        var parameters = insert.Parameters;
                        insert.Prepare();   // compiled once, reused for every row
                        while (reader.Read())
                        {
                            ct.ThrowIfCancellationRequested();
                            for (var i = 0; i < parameters.Count; i++)
                                parameters[i].Value = ToSqliteValue(reader.GetValue(i));
                            insert.ExecuteNonQuery();

                            if (reader.RowsRead % ProgressEvery == 0) progress?.Report(reader.RowsRead);
                        }
                        count = reader.RowsRead;
                    }

                    using (var total = conn.CreateCommand())
                    {
                        total.CommandText = "UPDATE ImportBatches SET TotalRows = $n WHERE Id = $b";
                        total.Parameters.AddWithValue("$n", count);
                        total.Parameters.AddWithValue("$b", batchId);
                        total.ExecuteNonQuery();
                    }

                    Exec(conn, "COMMIT");
                    done = true;
                    return count;
                }
                finally
                {
                    if (!done) TryRollback(conn);
                }
            }, CancellationToken.None).ConfigureAwait(false);

            progress?.Report(loaded);
        }
        catch
        {
            // The transaction is rolled back, so no staging rows exist; marking the batch Discarded keeps it from being committed.
            await DiscardAsync(batchId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        sw.Stop();
        return new LoadResult(loaded, sw.Elapsed);
    }

    private static SqliteCommand BuildInsertCommand(SqliteConnection conn)
    {
        var names = ImportRowDataReader.ColumnNames.ToArray();
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO ImportStaging ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((_, i) => "$p" + i))})";
        for (var i = 0; i < names.Length; i++) cmd.Parameters.Add(new SqliteParameter("$p" + i, null));
        return cmd;
    }

    /// <summary>The storage form of one staging value: dates as ISO text, money as cents, the status byte as a number.</summary>
    private static object ToSqliteValue(object value) => value switch
    {
        DBNull => DBNull.Value,
        DateTime d => SqliteValues.ToIsoDate(d),
        decimal m => SqliteValues.ToMinorUnits(m),
        byte b => (long)b,
        _ => value,
    };

    // ---- classify --------------------------------------------------------------------------------------------

    public override async Task ClassifyAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var b = new SqliteParameter("$b", batchId);

        // (a) Repeats of the same key inside this file, among Pending rows only (the same rules as SQL Server):
        //     identical group -> every row after the first (lowest ExcelRowNumber) is a FileDuplicate (4);
        //     differing group -> every row is Invalid (1), with the other row numbers in ErrorText.
        //     The window partitions compare the key columns with their own NOCASE collation, so 'abc' and 'ABC' are one key.
        //     MATERIALIZED makes the CTE a snapshot taken before the first row is updated (the statement reads the table it writes).
        //     (Column names are case-insensitive in SQLite: RowNo and Seq must differ by more than case.)
        await db.Database.ExecuteSqlRawAsync($"""
            WITH g AS MATERIALIZED (
                SELECT s.ExcelRowNumber AS RowNo, s.PayOrderNo AS Po, s.TxnRefSeqNo AS Tx,
                       COUNT(*)       OVER w AS Cnt,
                       MIN(s.RowHash) OVER w AS MinHash,
                       MAX(s.RowHash) OVER w AS MaxHash,
                       ROW_NUMBER()   OVER (PARTITION BY s.PayOrderNo, s.TxnRefSeqNo ORDER BY s.ExcelRowNumber) AS Seq
                FROM ImportStaging AS s
                WHERE s.ImportBatchId = $b AND s.Status = 0 AND s.PayOrderNo IS NOT NULL AND s.TxnRefSeqNo IS NOT NULL
                WINDOW w AS (PARTITION BY s.PayOrderNo, s.TxnRefSeqNo)
            )
            UPDATE ImportStaging
            SET Status = CASE WHEN g.MinHash = g.MaxHash THEN 4 ELSE 1 END,
                ErrorText = CASE WHEN g.MinHash = g.MaxHash THEN NULL ELSE substr(
                    'Same PayOrderNo + TxnRefSeqNo as row(s) ' || (
                        SELECT group_concat(o.ExcelRowNumber, ', ' ORDER BY o.ExcelRowNumber)
                        FROM ImportStaging AS o
                        WHERE o.ImportBatchId = $b AND o.PayOrderNo = g.Po AND o.TxnRefSeqNo = g.Tx
                          AND o.ExcelRowNumber <> g.RowNo)
                    || ' in this file, but the values differ', 1, {ErrorTextMax}) END
            FROM g
            WHERE ImportStaging.ImportBatchId = $b AND ImportStaging.ExcelRowNumber = g.RowNo
              AND g.Cnt > 1 AND (g.MinHash <> g.MaxHash OR g.Seq > 1);
            """, new[] { b }, ct).ConfigureAwait(false);

        // (a2) Repeats inside the file among rows WITHOUT a full key: identical content (same RowHash) repeats are FileDuplicates
        //      (the first copy stays Pending and is classified below). Flagged, not rejected: imported unless the user skips them.
        await db.Database.ExecuteSqlRawAsync("""
            WITH g AS MATERIALIZED (
                SELECT s.ExcelRowNumber AS RowNo,
                       ROW_NUMBER() OVER (PARTITION BY s.RowHash ORDER BY s.ExcelRowNumber) AS Seq
                FROM ImportStaging AS s
                WHERE s.ImportBatchId = $b AND s.Status = 0 AND (s.PayOrderNo IS NULL OR s.TxnRefSeqNo IS NULL)
            )
            UPDATE ImportStaging
            SET Status = 4
            FROM g
            WHERE ImportStaging.ImportBatchId = $b AND ImportStaging.ExcelRowNumber = g.RowNo AND g.Seq > 1;
            """, new[] { new SqliteParameter("$b", batchId) }, ct).ConfigureAwait(false);

        // (b) Rows still Pending: compare with Transactions (the rule is in SqliteStatusSql: by full key for keyed rows, by content and
        //     slip number for key-less ones). A keyed match remembers the hash seen so commit can detect a change.
        //     UPDATE ... FROM cannot LEFT JOIN its own target, so the lookup is a (materialized) subquery carrying the result.
        await db.Database.ExecuteSqlRawAsync(
            "WITH x AS MATERIALIZED (" +
            "SELECT s.ExcelRowNumber AS RowNo, " + SqliteStatusSql.NewStatus + " AS NewStatus, " + SqliteStatusSql.ExistingHash + " AS Hash, " +
            "CASE WHEN s.R IS NOT NULL AND (" + SqliteStatusSql.NewStatus + ") = 6 THEN s.R END AS Reason " +
            "FROM (" + SqliteStatusSql.WithReason("s.ImportBatchId = $b AND s.Status = 0") + ") AS s " +
            "LEFT JOIN Transactions AS t ON t.PayOrderNo = s.PayOrderNo AND t.TxnRefSeqNo = s.TxnRefSeqNo " +
            "WHERE s.ImportBatchId = $b AND s.Status = 0) " +
            "UPDATE ImportStaging SET ExistingRowHash = x.Hash, Status = x.NewStatus, MatchReason = x.Reason FROM x " +
            "WHERE ImportStaging.ImportBatchId = $b AND ImportStaging.ExcelRowNumber = x.RowNo;",
            new[] { new SqliteParameter("$b", batchId) }, ct).ConfigureAwait(false);
    }

    // ---- discard ---------------------------------------------------------------------------------------------

    public override async Task<DiscardResult> DiscardAsync(int batchId, CancellationToken ct = default)
    {
        await using var db = await Factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Step 1: claim the batch with the conditional UPDATE (see TryClaimForDiscardAsync). A running commit holds the write
        // lock, so this UPDATE waits (busy_timeout) for it to finish, then sees 'Committed', affects 0 rows and we refuse.
        var refused = await TryClaimForDiscardAsync(db, batchId, ct).ConfigureAwait(false);
        if (refused is not null) return refused;

        // Step 2: only now remove the rows. Once the status is 'Discarded' no commit can pick this batch up, and if we crash
        // here, CleanupStaleBatchesAsync deletes the leftover rows of Discarded batches.
        await DeleteStagingRowsAsync(db, batchId, ct).ConfigureAwait(false);
        return new DiscardResult(true);
    }

    /// <summary>One statement: with a single writer there are no lock-escalation limits to stay under, so no chunks.</summary>
    private static Task<int> DeleteStagingRowsAsync(AppDbContext db, int batchId, CancellationToken ct) =>
        db.ImportStagings.Where(s => s.ImportBatchId == batchId).ExecuteDeleteAsync(ct);

    // ---- small ADO.NET helpers -------------------------------------------------------------------------------

    private static SqliteCommand Cmd(SqliteConnection conn, string sql, int batchId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$b", batchId);
        return cmd;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Rolls back if a transaction is still open. A failed statement can already have rolled it back (then there is nothing to do).</summary>
    private static void TryRollback(SqliteConnection conn)
    {
        try { Exec(conn, "ROLLBACK"); }
        catch (SqliteException) { }
    }
}
