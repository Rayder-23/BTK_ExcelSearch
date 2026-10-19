/*
================================================================================
  003_rollback_sqlite.sql   (SQLite)

  Undoes 003_cash_matching.sql and sets the schema version back to 2. Run it on a COPY of the
  .db file first (copy the -wal / -shm files too); an older app refuses a newer file.
  Staged batches are discarded (staging is a work area; the file can be imported again).
================================================================================
*/
BEGIN IMMEDIATE;

UPDATE ImportBatches SET Status = 'Discarded' WHERE Status = 'Staged';
DELETE FROM ImportStaging;

DROP INDEX IF EXISTS IX_Transactions_Match;
ALTER TABLE ImportStaging DROP COLUMN MatchReason;

PRAGMA user_version = 2;

COMMIT;
