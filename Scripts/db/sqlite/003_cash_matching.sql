/*
================================================================================
  003_cash_matching.sql   (SQLite)   -- schema version 3

  The SQLite twin of Scripts/db/007_cash_matching.sql.

    * ImportStaging.MatchReason (INTEGER, NULL): why a row was flagged "Possible duplicate"
        1 = same slip no. + date + account + amounts
        2 = same value date + amount + branch code (+ bank)  -- a hint only
        3 = same Txn Ref (+ bank + account) but other values differ
    * IX_Transactions_Match (ValueDate, BranchCode) for the date + amount + branch lookup.
    * BranchCode needs no change: SQLite does not limit TEXT length.

  Applied by SqliteSchemaInitializer. Undo: Scripts/db/rollback/003_rollback_sqlite.sql
================================================================================
*/
BEGIN IMMEDIATE;

ALTER TABLE ImportStaging ADD COLUMN MatchReason INTEGER NULL;

CREATE INDEX IF NOT EXISTS IX_Transactions_Match ON Transactions (ValueDate, BranchCode);

PRAGMA user_version = 3;

COMMIT;
