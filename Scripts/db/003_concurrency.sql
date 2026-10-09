/*
================================================================================
  003_concurrency.sql   (SQL Server / T-SQL)   -- run AFTER 001 and 002

  Prepares the schema for several people importing at the same time.

  Adds:
    * dbo.ImportBatches.ImportedBy        - who created the batch (Windows user name),
                                            so the app can list "my imports", let a
                                            user discard only their own, and clean up
                                            stale batches safely
    * dbo.ImportStaging.ExistingRowHash   - the RowHash of the matching row in
                                            dbo.Transactions AS SEEN AT PREVIEW TIME
                                            (set for 'Conflict' rows). Commit compares
                                            it with the current hash and aborts if the
                                            existing row changed since the preview, so
                                            nobody's changes are silently overwritten.
    * IX_ImportBatches_Status_ImportedAt  - fast lookup of old 'Staged' batches for
                                            startup cleanup

  Already concurrency-safe from 002: every staging row carries ImportBatchId and every
  staging index starts with it, so two batches never interfere with each other.

  Application-side rules this script supports (implemented in the app, not in SQL):
    * Commits are serialized with sp_getapplock inside the commit transaction.
    * Inside that lock the commit re-checks keys/hashes and aborts if rows changed.
    * Staging cleanup deletes in chunks (DELETE TOP (50000) in a loop) to avoid lock
      escalation that would block other users' staging inserts.
    * Startup cleanup deletes only 'Staged' batches older than 24 hours.

  Safe to re-run.
================================================================================
*/

-- >>> Edit this line to your database name, or select it in your SQL tool <<<
-- USE [YourDatabaseName];

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

------------------------------------------------------------------------------
-- 1. ImportBatches.ImportedBy
--    Existing batches (if any) keep NULL = unknown owner.
------------------------------------------------------------------------------
IF COL_LENGTH(N'dbo.ImportBatches', N'ImportedBy') IS NULL
BEGIN
    ALTER TABLE dbo.ImportBatches
        ADD ImportedBy NVARCHAR(256) NULL;
END;

------------------------------------------------------------------------------
-- 2. ImportStaging.ExistingRowHash
------------------------------------------------------------------------------
IF COL_LENGTH(N'dbo.ImportStaging', N'ExistingRowHash') IS NULL
BEGIN
    ALTER TABLE dbo.ImportStaging
        ADD ExistingRowHash CHAR(64) NULL;
END;

------------------------------------------------------------------------------
-- 3. Index for finding stale 'Staged' batches
------------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_ImportBatches_Status_ImportedAt'
      AND object_id = OBJECT_ID(N'dbo.ImportBatches')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_ImportBatches_Status_ImportedAt
        ON dbo.ImportBatches (Status, ImportedAt);
END;

COMMIT TRANSACTION;

------------------------------------------------------------------------------
-- 4. OPTIONAL: row versioning so readers don't wait on a big commit
--
--    While a large commit inserts into dbo.Transactions, other users running
--    queries on that table can be blocked until it finishes. READ_COMMITTED_SNAPSHOT
--    lets readers see the last committed data instead of waiting.
--
--    It is OFF by default and it changes behavior for EVERYTHING that uses this
--    database, so it is commented out. Only enable it if no other application
--    depends on the default locking behavior. It cannot run inside a transaction
--    and WITH ROLLBACK IMMEDIATE disconnects other sessions on this database.
------------------------------------------------------------------------------
-- ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;

------------------------------------------------------------------------------
-- 5. Quick verification (read-only)
------------------------------------------------------------------------------
SELECT OBJECT_NAME(c.object_id) AS TableName, c.name AS ColumnName,
       TYPE_NAME(c.user_type_id) AS DataType, c.max_length, c.is_nullable
FROM sys.columns AS c
WHERE (c.object_id = OBJECT_ID(N'dbo.ImportBatches') AND c.name = N'ImportedBy')
   OR (c.object_id = OBJECT_ID(N'dbo.ImportStaging')  AND c.name = N'ExistingRowHash');

SELECT name AS IndexName, type_desc
FROM sys.indexes
WHERE object_id = OBJECT_ID(N'dbo.ImportBatches')
  AND name = N'IX_ImportBatches_Status_ImportedAt';

SELECT name AS DatabaseName, is_read_committed_snapshot_on
FROM sys.databases
WHERE database_id = DB_ID();

/*
-- ROLLBACK (development only)
-- DROP INDEX IF EXISTS IX_ImportBatches_Status_ImportedAt ON dbo.ImportBatches;
-- ALTER TABLE dbo.ImportStaging DROP COLUMN IF EXISTS ExistingRowHash;
-- ALTER TABLE dbo.ImportBatches DROP COLUMN IF EXISTS ImportedBy;
*/
