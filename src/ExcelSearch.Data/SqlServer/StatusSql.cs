namespace ExcelSearch.Data.SqlServer;

/// <summary>
/// The classification rule as T-SQL, shared by ClassifyAsync and the re-check so they cannot drift apart.
/// Aliases: <c>s</c> = dbo.ImportStaging, <c>t</c> = the dbo.Transactions row with the SAME KEY (LEFT JOIN on PayOrderNo and
/// TxnRefSeqNo; it never matches when either is NULL), <c>m</c> = the one-row result of <see cref="Reason"/> (CROSS APPLY).
/// <list type="bullet">
/// <item>Row with a full key: no match -> New (2); same hash -> ExactDuplicate (3); different hash -> Conflict (5).</item>
/// <item>Any other row: a Transactions row with the same RowHash -> ExactDuplicate (3); otherwise, when <see cref="Reason"/> finds
/// something -> LikelyDuplicate (6); otherwise New (2).</item>
/// </list>
/// <see cref="Reason"/> (stored in ImportStaging.MatchReason): 3 = a row with the same Txn Ref (bank, account) but other values,
/// 1 = same slip no. + date + account + amounts, 2 = same date + amount + branch code (+ bank), a hint only. A row that has a Txn Ref
/// is only compared by that Txn Ref; a row without one (cash) by slip no., then by date + amount + branch, unless both rows carry
/// different slip numbers (then they are different deposits).
/// </summary>
internal static class StatusSql
{
    /// <summary>SQL for the match reason of a staged row <c>s</c>; NULL when there is none. Same hash and full key rows give NULL.</summary>
    public const string Reason = """
        CASE WHEN s.PayOrderNo IS NOT NULL AND s.TxnRefSeqNo IS NOT NULL THEN NULL
             WHEN EXISTS (SELECT 1 FROM dbo.Transactions AS x WHERE x.RowHash = s.RowHash) THEN NULL
             WHEN s.TxnRefSeqNo IS NOT NULL THEN
                  CASE WHEN EXISTS (SELECT 1 FROM dbo.Transactions AS z
                                    WHERE z.TxnRefSeqNo = s.TxnRefSeqNo
                                      AND (z.BankName = s.BankName OR (z.BankName IS NULL AND s.BankName IS NULL))
                                      AND (z.AccountNo = s.AccountNo OR (z.AccountNo IS NULL AND s.AccountNo IS NULL))) THEN 3 END
             WHEN s.SlipNo IS NOT NULL AND EXISTS (
                      SELECT 1 FROM dbo.Transactions AS y
                      WHERE y.SlipNo = s.SlipNo AND y.ValueDate = s.ValueDate
                        AND (y.AccountNo = s.AccountNo OR (y.AccountNo IS NULL AND s.AccountNo IS NULL))
                        AND (y.Debit  = s.Debit  OR (y.Debit  IS NULL AND s.Debit  IS NULL))
                        AND (y.Credit = s.Credit OR (y.Credit IS NULL AND s.Credit IS NULL))) THEN 1
             WHEN s.BranchCode IS NOT NULL AND EXISTS (
                      SELECT 1 FROM dbo.Transactions AS w
                      WHERE w.ValueDate = s.ValueDate AND w.BranchCode = s.BranchCode AND w.TxnRefSeqNo IS NULL
                        AND (w.BankName = s.BankName OR (w.BankName IS NULL AND s.BankName IS NULL))
                        AND (w.Debit  = s.Debit  OR (w.Debit  IS NULL AND s.Debit  IS NULL))
                        AND (w.Credit = s.Credit OR (w.Credit IS NULL AND s.Credit IS NULL))
                        AND NOT (s.SlipNo IS NOT NULL AND w.SlipNo IS NOT NULL AND w.SlipNo <> s.SlipNo)) THEN 2
        END
        """;

    /// <summary>Use after <c>CROSS APPLY (SELECT </c><see cref="Reason"/><c> AS R) AS m</c>.</summary>
    public const string NewStatus = """
        CASE WHEN s.PayOrderNo IS NOT NULL AND s.TxnRefSeqNo IS NOT NULL
                  THEN CASE WHEN t.Id IS NULL THEN 2 WHEN t.RowHash = s.RowHash THEN 3 ELSE 5 END
             WHEN EXISTS (SELECT 1 FROM dbo.Transactions AS x WHERE x.RowHash = s.RowHash) THEN 3
             WHEN m.R IS NOT NULL THEN 6
             ELSE 2 END
        """;

    /// <summary>The hash of the same-key Transactions row; only a row with a full key has one.</summary>
    public const string ExistingHash = """
        CASE WHEN s.PayOrderNo IS NOT NULL AND s.TxnRefSeqNo IS NOT NULL AND t.Id IS NOT NULL
             THEN t.RowHash ELSE CAST(NULL AS varchar(64)) END
        """;

    /// <summary>True for a staged row without a full key (the only rows that can be flagged and still imported).</summary>
    public const string KeyLess = "(s.PayOrderNo IS NULL OR s.TxnRefSeqNo IS NULL)";
}
