/*
================================================================================
  007_rollback.sql   (SQL Server / T-SQL)

  Undoes 007_cash_matching.sql: drops IX_Transactions_Match and ImportStaging.MatchReason and
  makes BranchCode NVARCHAR(20) again.

  READ THIS FIRST
    * TAKE A BACKUP before running it.
    * It REFUSES to run (nothing is changed) while a BranchCode value is longer than 20
      characters. Shorten or clear those values first, for example:
          UPDATE dbo.Transactions SET BranchCode = LEFT(BranchCode, 20) WHERE LEN(BranchCode) > 20;
    * Batches still 'Staged' are marked 'Discarded' and the staging rows are deleted
      (staging is a work area; the file can be imported again).
    * One transaction; re-runnable.
================================================================================
*/

-- >>> Edit this line to your database name, or select it in your SQL tool <<<
-- USE [YourDatabaseName];

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;

IF EXISTS (SELECT 1 FROM dbo.Transactions WHERE LEN(BranchCode) > 20)
BEGIN
    DECLARE @n INT = (SELECT COUNT(*) FROM dbo.Transactions WHERE LEN(BranchCode) > 20);
    DECLARE @msg NVARCHAR(400) = CONCAT(N'Rollback refused: ', @n,
        N' row(s) in dbo.Transactions have a BranchCode longer than 20 characters. See the notes at the top of this script.');
    THROW 51000, @msg, 1;
END;

UPDATE dbo.ImportBatches SET Status = 'Discarded' WHERE Status = 'Staged';
DELETE FROM dbo.ImportStaging;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_Match' AND object_id = OBJECT_ID(N'dbo.Transactions'))
    DROP INDEX IX_Transactions_Match ON dbo.Transactions;

IF COL_LENGTH(N'dbo.ImportStaging', N'MatchReason') IS NOT NULL
    ALTER TABLE dbo.ImportStaging DROP COLUMN MatchReason;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Transactions') AND name = N'BranchCode' AND max_length > 40)
    ALTER TABLE dbo.Transactions ALTER COLUMN BranchCode NVARCHAR(20) NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ImportStaging') AND name = N'BranchCode' AND max_length > 40)
    ALTER TABLE dbo.ImportStaging ALTER COLUMN BranchCode NVARCHAR(20) NULL;

COMMIT TRANSACTION;
