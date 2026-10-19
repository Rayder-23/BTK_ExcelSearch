/*
================================================================================
  007_cash_matching.sql   (SQL Server / T-SQL)   -- run AFTER 001-004 and 006

  Recognition of cash bank rows (HBL / UBL statements) that have no PayOrderNo / TxnRefSeqNo.

    * dbo.Transactions.BranchCode and dbo.ImportStaging.BranchCode: NVARCHAR(20) -> NVARCHAR(50).
    * dbo.ImportStaging.MatchReason (TINYINT, NULL): why a row was flagged "Possible duplicate"
        1 = same slip no. + date + account + amounts
        2 = same value date + amount + branch code (+ bank)  -- a hint only
        3 = same Txn Ref (+ bank + account) but other values differ
    * IX_Transactions_Match (ValueDate, BranchCode) INCLUDE (Credit, Debit, BankName, SlipNo)
      for the date + amount + branch lookup.

  Existing data is untouched. Safe to re-run; ONE transaction.
  To undo it: Scripts/db/rollback/007_rollback.sql
  Documented in Docs/KeylessImport.md and db.md.
================================================================================
*/

-- >>> Edit this line to your database name, or select it in your SQL tool <<<
-- USE [YourDatabaseName];

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;

------------------------------------------------------------------------------
-- 1. Br. Code is NVARCHAR(50); the real bank sheets hold longer values than 20 characters.
--    ImportStaging.MatchReason says why a row was flagged as a possible duplicate.
------------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Transactions') AND name = N'BranchCode' AND max_length < 100)
    ALTER TABLE dbo.Transactions ALTER COLUMN BranchCode NVARCHAR(50) NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ImportStaging') AND name = N'BranchCode' AND max_length < 100)
    ALTER TABLE dbo.ImportStaging ALTER COLUMN BranchCode NVARCHAR(50) NULL;

IF COL_LENGTH(N'dbo.ImportStaging', N'MatchReason') IS NULL
    ALTER TABLE dbo.ImportStaging ADD MatchReason TINYINT NULL;

------------------------------------------------------------------------------
-- 2. Index for the date + amount + branch match of cash rows (EXEC so the batch compiles first)
------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_Match' AND object_id = OBJECT_ID(N'dbo.Transactions'))
    EXEC (N'CREATE NONCLUSTERED INDEX IX_Transactions_Match
              ON dbo.Transactions (ValueDate, BranchCode) INCLUDE (Credit, Debit, BankName, SlipNo)');

COMMIT TRANSACTION;

SELECT OBJECT_NAME(c.object_id) AS TableName, c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType, c.max_length / 2 AS MaxChars
FROM sys.columns AS c
WHERE (c.object_id = OBJECT_ID(N'dbo.Transactions') OR c.object_id = OBJECT_ID(N'dbo.ImportStaging'))
  AND c.name IN (N'BranchCode', N'MatchReason');
