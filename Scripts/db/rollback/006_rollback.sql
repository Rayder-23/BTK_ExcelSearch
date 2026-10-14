/*
================================================================================
  006_rollback.sql   (SQL Server / T-SQL)

  Undoes 006_surrogate_key.sql: the primary key goes back to (PayOrderNo, TxnRefSeqNo),
  both become NOT NULL again, and SlipNo / NarrationCnic and the indexes added by 006
  are dropped. Use it together with a git revert of the key-less import change
  (Docs/KeylessImport.md), because the old app cannot run on the new schema.

  READ THIS FIRST
    * TAKE A BACKUP before running it.
    * It REFUSES to run (nothing is changed) while any row in dbo.Transactions has no
      PayOrderNo or no TxnRefSeqNo: the old primary key cannot hold them. Delete or fix
      those rows first, for example:
          DELETE FROM dbo.Transactions WHERE PayOrderNo IS NULL OR TxnRefSeqNo IS NULL;
      (that loses them: there is no other place for rows without a key in the old model.)
    * The values of SlipNo / NarrationCnic are dropped with the columns.
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

IF EXISTS (SELECT 1 FROM dbo.Transactions WHERE PayOrderNo IS NULL OR TxnRefSeqNo IS NULL)
BEGIN
    DECLARE @n INT = (SELECT COUNT(*) FROM dbo.Transactions WHERE PayOrderNo IS NULL OR TxnRefSeqNo IS NULL);
    DECLARE @msg NVARCHAR(400) = CONCAT(N'Rollback refused: ', @n,
        N' row(s) in dbo.Transactions have no PayOrderNo or TxnRefSeqNo. See the notes at the top of this script.');
    THROW 51000, @msg, 1;
END;

UPDATE dbo.ImportBatches SET Status = 'Discarded' WHERE Status = 'Staged';
DELETE FROM dbo.ImportStaging;

-- Indexes added by 006 (and the one recreated by it)
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Transactions_Key'          AND object_id = OBJECT_ID(N'dbo.Transactions')) DROP INDEX UX_Transactions_Key          ON dbo.Transactions;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_PayOrderNo'    AND object_id = OBJECT_ID(N'dbo.Transactions')) DROP INDEX IX_Transactions_PayOrderNo    ON dbo.Transactions;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_RowHash'       AND object_id = OBJECT_ID(N'dbo.Transactions')) DROP INDEX IX_Transactions_RowHash       ON dbo.Transactions;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_SlipNo'        AND object_id = OBJECT_ID(N'dbo.Transactions')) DROP INDEX IX_Transactions_SlipNo        ON dbo.Transactions;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_NarrationCnic' AND object_id = OBJECT_ID(N'dbo.Transactions')) DROP INDEX IX_Transactions_NarrationCnic ON dbo.Transactions;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_TxnRefSeqNo'   AND object_id = OBJECT_ID(N'dbo.Transactions')) DROP INDEX IX_Transactions_TxnRefSeqNo   ON dbo.Transactions;

-- Derived columns
IF COL_LENGTH(N'dbo.Transactions', N'SlipNo')          IS NOT NULL ALTER TABLE dbo.Transactions  DROP COLUMN SlipNo;
IF COL_LENGTH(N'dbo.Transactions', N'NarrationCnic')   IS NOT NULL ALTER TABLE dbo.Transactions  DROP COLUMN NarrationCnic;
IF COL_LENGTH(N'dbo.ImportStaging', N'SlipNo')         IS NOT NULL ALTER TABLE dbo.ImportStaging DROP COLUMN SlipNo;
IF COL_LENGTH(N'dbo.ImportStaging', N'NarrationCnic')  IS NOT NULL ALTER TABLE dbo.ImportStaging DROP COLUMN NarrationCnic;

-- Primary key back to the business key
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.Transactions') AND name = N'PayOrderNo' AND is_nullable = 1)
BEGIN
    ALTER TABLE dbo.Transactions DROP CONSTRAINT PK_Transactions;
    ALTER TABLE dbo.Transactions ALTER COLUMN PayOrderNo  NVARCHAR(50) NOT NULL;
    ALTER TABLE dbo.Transactions ALTER COLUMN TxnRefSeqNo NVARCHAR(50) NOT NULL;
    ALTER TABLE dbo.Transactions ADD CONSTRAINT PK_Transactions PRIMARY KEY CLUSTERED (PayOrderNo, TxnRefSeqNo);
    ALTER TABLE dbo.Transactions ADD CONSTRAINT UQ_Transactions_Id UNIQUE NONCLUSTERED (Id);
    CREATE NONCLUSTERED INDEX IX_Transactions_TxnRefSeqNo ON dbo.Transactions (TxnRefSeqNo);
END;

COMMIT TRANSACTION;

SELECT c.name AS ColumnName, c.is_nullable
FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(N'dbo.Transactions') AND c.name IN (N'PayOrderNo', N'TxnRefSeqNo', N'SlipNo', N'NarrationCnic');
