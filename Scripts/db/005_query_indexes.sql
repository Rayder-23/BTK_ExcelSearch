/*
  005_query_indexes.sql   (SQL Server / T-SQL)      *** DRAFT FOR REVIEW - NOT APPLIED, NOTHING IN HERE RUNS ***

  Every statement below is commented out on purpose. Nothing has been created in any database. Read the measurements,
  decide which (if any) you want, then un-comment those statements and run them yourself.

  Measured on 2026-10-12 with 501,090 rows in dbo.Transactions (500,000 of them synthetic TEST_ rows), the Query screen's
  searches, first page of 100 + COUNT + SUMs (see Docs/Performance.md section 8 and src/ExcelSearch.Tests/QueryLargeTests.cs):

    Search                                          page     count    totals   verdict
    ----------------------------------------------  -------  -------  -------  --------------------------------------------
    date range (one month, 7,750 rows)              0.24 s   0.01 s   0.11 s   fast (IX_Transactions_ValueDate)
    exact CNIC                                      1.74 s*  0.01 s   0.01 s   fast; * first call only (plan compile), 0.00 s warm
    application no starts with (10,000 rows)        0.72 s   0.03 s   0.85 s   ok; the totals are the slow part (see 2)
    customer name CONTAINS (8,424 rows)             1.36 s   1.09 s   1.23 s   slowest everyday filter: a scan, see 3
    bank + project (1,667 rows)                     0.46 s   0.36 s   0.40 s   ok
    credit between (20,010 rows)                    0.37 s   0.21 s   0.22 s   ok
    date range + bank + customer contains           1.44 s   1.23 s   0.94 s   borderline, dominated by the CONTAINS scan
    unfiltered, ValueDate newest first              2.36 s   0.18 s   0.44 s   was SLOW; 0.01 s after the tie-break fix, see 1
    unfiltered, CustomerName                        0.01 s   0.18 s   0.49 s   fast
    unfiltered, Credit (extra)                      2.29 s   0.14 s   0.40 s   slow: no index on Credit, see 4

  1. "Unfiltered, ValueDate newest first" was slow because the ORDER BY was ValueDate DESC, PayOrderNo ASC, TxnRefSeqNo ASC
     while IX_Transactions_ValueDate stores (ValueDate, PayOrderNo, TxnRefSeqNo) all ascending: read backwards it gives
     PayOrderNo DESC, so SQL Server had to sort all 500,000 rows. FIXED IN CODE, NO INDEX NEEDED: the tie-break now runs in
     the same direction as the sort. Re-measured: page 1 went from 2.36 s to 0.01 s (parallel total 1.99 s to 0.40 s), so this
     index is NOT needed; it is listed only in case that ever changes:

       -- CREATE NONCLUSTERED INDEX IX_Transactions_ValueDate_Desc
       --     ON dbo.Transactions (ValueDate DESC, PayOrderNo DESC, TxnRefSeqNo DESC);

  2. Totals (SUM of Credit, Debit, DownPaymentAmount) over a few thousand rows spend their time fetching the three amount
     columns from the clustered index. Adding them as INCLUDE columns to the filter indexes makes each of those
     searches a pure index read, at the cost of about 27 bytes per row per index (13 MB per index at 500,000 rows) and
     slower imports. Worth it only for the filters people use most; candidates:

       -- CREATE NONCLUSTERED INDEX IX_Transactions_ApplicationNo_Amounts
       --     ON dbo.Transactions (ApplicationNo) INCLUDE (Credit, Debit, DownPaymentAmount);
       -- (and drop IX_Transactions_ApplicationNo, which it replaces)

  3. Customer name CONTAINS ('%khan%') cannot use a B-tree index: it reads every row. 1.1-1.4 s at 500,000 rows grows linearly,
     so about 6 s at 2.5 million. Options, in order of effort:
       a. do nothing now, revisit at about 2 million rows;
       b. a full-text index. That changes the meaning (it matches whole words/word prefixes, not any substring, so
          "ahm" would not find "Ahmed" unless written "ahm*") and needs a full-text catalog, so it is NOT a drop-in;
       c. offer "starts with" for names too (uses IX_Transactions_CustomerName).

  4. Sorting by an unindexed column (Credit, Debit, Down payment, Reg no ...) over a large result sorts every matching row.
     Acceptable on a filtered result. If unfiltered sorts by amount matter:

       -- CREATE NONCLUSTERED INDEX IX_Transactions_Credit ON dbo.Transactions (Credit DESC, PayOrderNo DESC, TxnRefSeqNo DESC);

  5. NOT MEASURED (the benchmark did not time them): the filters on RegNo, ChequeInstNo and AccountNo have no index of their
     own (AccountNo is only the second column of IX_Transactions_Bank_Account), so a "starts with" search on them scans.
     Likely fine at today's size; add these if they become slow:

       -- CREATE NONCLUSTERED INDEX IX_Transactions_RegNo        ON dbo.Transactions (RegNo);
       -- CREATE NONCLUSTERED INDEX IX_Transactions_ChequeInstNo ON dbo.Transactions (ChequeInstNo);
       -- CREATE NONCLUSTERED INDEX IX_Transactions_AccountNo    ON dbo.Transactions (AccountNo);

  6. The dropdown lists (distinct values) took 0.2-0.5 s each at 500,000 rows and are cached, so they are not a problem.
     BranchName and DataSource are the slowest (no index); an index only helps them a little:

       -- CREATE NONCLUSTERED INDEX IX_Transactions_BranchName ON dbo.Transactions (BranchName);
       -- CREATE NONCLUSTERED INDEX IX_Transactions_DataSource ON dbo.Transactions (DataSource);

  Before running any of these on the real database: indexes slow every import and commit a little (each one is maintained
  on every INSERT/UPDATE), and building one on a large table takes a while and, without ONLINE = ON (Enterprise/Developer
  edition only; not on Express), blocks writes to the table while it builds. If you add one, also add it to
  001_create_tables.sql, the scaffolded model (AppDbContext) and db.md, as AGENTS.md requires.
*/

PRINT 'This is a draft. Nothing was changed. Un-comment the statements you want after reading the notes above.';
