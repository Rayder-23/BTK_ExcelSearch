namespace ExcelSearch.Data.SqlServer;

/// <summary>
/// The classification rule as T-SQL, shared by ClassifyAsync and the re-check so they cannot drift apart.
/// Aliases: <c>s</c> = dbo.ImportStaging, <c>t</c> = the dbo.Transactions row with the SAME KEY (LEFT JOIN on PayOrderNo and
/// TxnRefSeqNo; it never matches when either is NULL).
/// <list type="bullet">
/// <item>Row with a full key: no match -> New (2); same hash -> ExactDuplicate (3); different hash -> Conflict (5).</item>
/// <item>Key-less row: a Transactions row with the same RowHash -> ExactDuplicate (3); otherwise one with the same slip number,
/// value date, account and amounts -> LikelyDuplicate (6); otherwise New (2).</item>
/// </list>
/// </summary>
internal static class StatusSql
{
    public const string NewStatus = """
        CASE WHEN s.PayOrderNo IS NOT NULL AND s.TxnRefSeqNo IS NOT NULL
                  THEN CASE WHEN t.Id IS NULL THEN 2 WHEN t.RowHash = s.RowHash THEN 3 ELSE 5 END
             WHEN EXISTS (SELECT 1 FROM dbo.Transactions AS x WHERE x.RowHash = s.RowHash) THEN 3
             WHEN s.SlipNo IS NOT NULL AND EXISTS (
                      SELECT 1 FROM dbo.Transactions AS y
                      WHERE y.SlipNo = s.SlipNo AND y.ValueDate = s.ValueDate
                        AND (y.AccountNo = s.AccountNo OR (y.AccountNo IS NULL AND s.AccountNo IS NULL))
                        AND (y.Debit  = s.Debit  OR (y.Debit  IS NULL AND s.Debit  IS NULL))
                        AND (y.Credit = s.Credit OR (y.Credit IS NULL AND s.Credit IS NULL))) THEN 6
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
