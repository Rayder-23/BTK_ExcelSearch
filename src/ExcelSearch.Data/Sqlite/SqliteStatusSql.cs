namespace ExcelSearch.Data.Sqlite;

/// <summary>
/// The classification rule as SQLite SQL, shared by ClassifyAsync and the re-check (the SQL Server twin is StatusSql; change both together).
/// Aliases: <c>s</c> = ImportStaging with the extra column <c>R</c> (see <see cref="WithReason"/>), <c>t</c> = the Transactions row with
/// the SAME KEY (LEFT JOIN on PayOrderNo and TxnRefSeqNo; it never matches when either is NULL).
/// <list type="bullet">
/// <item>Row with a full key: no match -> New (2); same hash -> ExactDuplicate (3); different hash -> Conflict (5).</item>
/// <item>Any other row: a Transactions row with the same RowHash -> ExactDuplicate (3); otherwise, when <c>R</c> (the match reason)
/// is set -> LikelyDuplicate (6); otherwise New (2).</item>
/// </list>
/// Reason: 3 = a row with the same Txn Ref (bank, account) but other values, 1 = same slip no. + date + account + amounts,
/// 2 = same date + amount + branch code (+ bank), a hint only. A row that has a Txn Ref is only compared by that Txn Ref; a row
/// without one (cash) by slip no., then by date + amount + branch, unless both rows carry different slip numbers.
/// SQLite's IS compares NULLs as equal. Money is integer cents on both sides, so the amounts compare exactly.
/// </summary>
internal static class SqliteStatusSql
{
    /// <summary>The SQL for the match reason of a staged row <c>s</c>; NULL when there is none.</summary>
    private const string Reason = """
        CASE WHEN s.PayOrderNo IS NOT NULL AND s.TxnRefSeqNo IS NOT NULL THEN NULL
             WHEN EXISTS (SELECT 1 FROM Transactions AS x WHERE x.RowHash = s.RowHash) THEN NULL
             WHEN s.TxnRefSeqNo IS NOT NULL THEN
                  CASE WHEN EXISTS (SELECT 1 FROM Transactions AS z
                                    WHERE z.TxnRefSeqNo = s.TxnRefSeqNo AND z.BankName COLLATE NOCASE IS s.BankName AND z.AccountNo IS s.AccountNo) THEN 3 END
             WHEN s.SlipNo IS NOT NULL AND EXISTS (
                      SELECT 1 FROM Transactions AS y
                      WHERE y.SlipNo = s.SlipNo AND y.ValueDate = s.ValueDate
                        AND y.AccountNo IS s.AccountNo AND y.Debit IS s.Debit AND y.Credit IS s.Credit) THEN 1
             WHEN s.BranchCode IS NOT NULL AND EXISTS (
                      SELECT 1 FROM Transactions AS w
                      WHERE w.ValueDate = s.ValueDate AND w.BranchCode = s.BranchCode COLLATE NOCASE AND w.TxnRefSeqNo IS NULL
                        AND w.BankName COLLATE NOCASE IS s.BankName AND w.Debit IS s.Debit AND w.Credit IS s.Credit
                        AND NOT (s.SlipNo IS NOT NULL AND w.SlipNo IS NOT NULL AND w.SlipNo <> s.SlipNo)) THEN 2
        END
        """;

    /// <summary>
    /// The staged rows of a batch (optionally only some statuses) with the match reason computed once as column <c>R</c>.
    /// Use as the FROM source aliased <c>s</c>: <c>FROM (</c><see cref="WithReason"/><c>) AS s</c>.
    /// </summary>
    public static string WithReason(string where) =>
        "SELECT s.*, " + Reason + " AS R FROM ImportStaging AS s WHERE " + where;

    public const string NewStatus = """
        CASE WHEN s.PayOrderNo IS NOT NULL AND s.TxnRefSeqNo IS NOT NULL
                  THEN CASE WHEN t.Id IS NULL THEN 2 WHEN t.RowHash = s.RowHash THEN 3 ELSE 5 END
             WHEN EXISTS (SELECT 1 FROM Transactions AS x WHERE x.RowHash = s.RowHash) THEN 3
             WHEN s.R IS NOT NULL THEN 6
             ELSE 2 END
        """;

    /// <summary>The hash of the same-key Transactions row; only a row with a full key has one.</summary>
    public const string ExistingHash = """
        CASE WHEN s.PayOrderNo IS NOT NULL AND s.TxnRefSeqNo IS NOT NULL AND t.Id IS NOT NULL THEN t.RowHash ELSE NULL END
        """;

    /// <summary>True for a staged row without a full key (the only rows that can be flagged and still imported).</summary>
    public const string KeyLess = "(s.PayOrderNo IS NULL OR s.TxnRefSeqNo IS NULL)";
}
