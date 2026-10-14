/*
================================================================================
  006_surrogate_key.sql   (SQL Server / T-SQL)   -- run AFTER 001-004

  Lets bank-statement sheets that have NO PayOrderNo / TxnRefSeqNo be imported.

    * dbo.Transactions.Id (GUID, already there) becomes the PRIMARY KEY (clustered).
    * PayOrderNo and TxnRefSeqNo become NULLable.
    * Rows that DO have both keep being unique: a filtered unique index
      UX_Transactions_Key (PayOrderNo, TxnRefSeqNo) WHERE both are NOT NULL.
      Key-less rows are not restricted, so duplicates are possible by design.
    * New nullable columns on dbo.Transactions and dbo.ImportStaging, derived at import
      from the narrations (never part of RowHash):
        SlipNo         the slip number found in Narration 1   (NVARCHAR(30))
        NarrationCnic  a 13-digit CNIC found in Narrations 2-5 (VARCHAR(20), digits only)
    * New indexes: IX_Transactions_PayOrderNo, IX_Transactions_RowHash,
      IX_Transactions_SlipNo and IX_Transactions_NarrationCnic (the last two filtered
      to rows where the column is not null).
    * ImportStaging.Status gains code 6 (LikelyDuplicate). The column is a TINYINT with
      no CHECK constraint, so nothing changes in the table for that.

  Existing rows keep their keys; their SlipNo / NarrationCnic stay NULL (no backfill).

  Safe to re-run: every step is guarded. The whole upgrade is ONE transaction.
  To undo it: Scripts/db/rollback/006_rollback.sql
  Documented in Docs/KeylessImport.md and db.md.
================================================================================
*/

-- >>> Edit this line to your database name, or select it in your SQL tool <<<
-- USE [YourDatabaseName];

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;   -- required for filtered indexes (sqlcmd defaults to OFF)

BEGIN TRANSACTION;

------------------------------------------------------------------------------
-- 1. Primary key moves from (PayOrderNo, TxnRefSeqNo) to Id; the key columns become NULLable
--    (only while the old key columns are still NOT NULL, so a re-run skips this).
------------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.Transactions') AND name = N'PayOrderNo' AND is_nullable = 0)
BEGIN
    -- The index on TxnRefSeqNo blocks the nullability change; it is recreated below.
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_TxnRefSeqNo' AND object_id = OBJECT_ID(N'dbo.Transactions'))
        DROP INDEX IX_Transactions_TxnRefSeqNo ON dbo.Transactions;

    ALTER TABLE dbo.Transactions DROP CONSTRAINT PK_Transactions;
    ALTER TABLE dbo.Transactions DROP CONSTRAINT UQ_Transactions_Id;

    ALTER TABLE dbo.Transactions ALTER COLUMN PayOrderNo  NVARCHAR(50) NULL;
    ALTER TABLE dbo.Transactions ALTER COLUMN TxnRefSeqNo NVARCHAR(50) NULL;

    ALTER TABLE dbo.Transactions ADD CONSTRAINT PK_Transactions PRIMARY KEY CLUSTERED (Id);
END;

------------------------------------------------------------------------------
-- 2. New derived columns
------------------------------------------------------------------------------
IF COL_LENGTH(N'dbo.Transactions', N'SlipNo') IS NULL
    ALTER TABLE dbo.Transactions ADD SlipNo NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.Transactions', N'NarrationCnic') IS NULL
    ALTER TABLE dbo.Transactions ADD NarrationCnic VARCHAR(20) NULL;
IF COL_LENGTH(N'dbo.ImportStaging', N'SlipNo') IS NULL
    ALTER TABLE dbo.ImportStaging ADD SlipNo NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.ImportStaging', N'NarrationCnic') IS NULL
    ALTER TABLE dbo.ImportStaging ADD NarrationCnic VARCHAR(20) NULL;

------------------------------------------------------------------------------
-- 3. Indexes (EXEC so the batch compiles before the new columns exist)
------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Transactions_Key' AND object_id = OBJECT_ID(N'dbo.Transactions'))
    EXEC (N'CREATE UNIQUE NONCLUSTERED INDEX UX_Transactions_Key
              ON dbo.Transactions (PayOrderNo, TxnRefSeqNo)
              WHERE PayOrderNo IS NOT NULL AND TxnRefSeqNo IS NOT NULL');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_TxnRefSeqNo' AND object_id = OBJECT_ID(N'dbo.Transactions'))
    EXEC (N'CREATE NONCLUSTERED INDEX IX_Transactions_TxnRefSeqNo ON dbo.Transactions (TxnRefSeqNo)');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_PayOrderNo' AND object_id = OBJECT_ID(N'dbo.Transactions'))
    EXEC (N'CREATE NONCLUSTERED INDEX IX_Transactions_PayOrderNo ON dbo.Transactions (PayOrderNo)');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_RowHash' AND object_id = OBJECT_ID(N'dbo.Transactions'))
    EXEC (N'CREATE NONCLUSTERED INDEX IX_Transactions_RowHash ON dbo.Transactions (RowHash)');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_SlipNo' AND object_id = OBJECT_ID(N'dbo.Transactions'))
    EXEC (N'CREATE NONCLUSTERED INDEX IX_Transactions_SlipNo
              ON dbo.Transactions (SlipNo) WHERE SlipNo IS NOT NULL');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_NarrationCnic' AND object_id = OBJECT_ID(N'dbo.Transactions'))
    EXEC (N'CREATE NONCLUSTERED INDEX IX_Transactions_NarrationCnic
              ON dbo.Transactions (NarrationCnic) WHERE NarrationCnic IS NOT NULL');

COMMIT TRANSACTION;

------------------------------------------------------------------------------
-- 4. Quick verification (read-only)
------------------------------------------------------------------------------
SELECT c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType, c.max_length, c.is_nullable
FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(N'dbo.Transactions')
  AND c.name IN (N'Id', N'PayOrderNo', N'TxnRefSeqNo', N'SlipNo', N'NarrationCnic');

SELECT i.name AS IndexName, i.type_desc, i.is_primary_key, i.is_unique, i.filter_definition
FROM sys.indexes AS i
WHERE i.object_id = OBJECT_ID(N'dbo.Transactions') AND i.name IS NOT NULL
ORDER BY i.is_primary_key DESC, i.name;
