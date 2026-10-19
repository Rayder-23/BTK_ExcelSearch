/*
================================================================================
  init_db.sql   (SQL Server / T-SQL)

  Builds the whole ExcelSearch database schema in ONE run, from nothing to the
  current state. It is the active scripts joined in order:

    001_create_tables.sql   dbo.ImportBatches, dbo.Transactions (+ the filter indexes)
    002_create_staging.sql  ImportBatches.Status / CommittedAt, dbo.ImportStaging
    003_concurrency.sql     ImportBatches.ImportedBy, ImportStaging.ExistingRowHash,
                            IX_ImportBatches_Status_ImportedAt
    004_enable_rcsi.sql     READ_COMMITTED_SNAPSHOT ON (readers do not wait on a commit)
    006_surrogate_key.sql   Transactions.Id is the primary key, PayOrderNo / TxnRefSeqNo are optional
                            (filtered unique index for rows that have both), SlipNo / NarrationCnic columns

  NOT included: 005_query_indexes.sql (a draft; every statement in it is commented out
  and nothing in it has been applied to the live database).

  How it gets to the final shape: sections 1-3 are the ORIGINAL scripts 001-003 (so a database
  built by them and this file go through exactly the same steps), and section 5 (006) then
  turns the business key into a surrogate key. A fresh database therefore passes through the
  old key design for a moment inside this script; the END state is what counts, and section 7
  checks it and stops with an error if anything is missing.

  Safe to re-run: every table, column and index is created only if it is missing, and
  RCSI is switched on only if it is off. Nothing is dropped, truncated or reseeded.
  Re-running it on the live database changes nothing, except that RCSI's
  WITH ROLLBACK IMMEDIATE only runs when RCSI is still off.

  Before you run it
    * Change the database name on the two lines marked  <<< EDIT  if you do not want
      "ExcelSearch". The CREATE DATABASE line only runs when that database is missing.
    * Run it as a login that may create databases/tables (the app's dev login is sa).
    * Close the app and other tools connected to the database before the RCSI step
      at the end: it disconnects other sessions.
    * Keep the numbered scripts: they stay the history of how the schema changed.
      When a schema change is made, update the numbered script, this file, the
      scaffolded model and db.md together (see AGENTS.md).

  Excel header -> column mapping, key design and status codes are documented in
  001_create_tables.sql, 002_create_staging.sql and db.md.
================================================================================
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;   -- any error inside a transaction rolls that part back
GO

------------------------------------------------------------------------------
-- 0. The database
------------------------------------------------------------------------------
IF DB_ID(N'ExcelSearch') IS NULL          -- <<< EDIT (database name)
    CREATE DATABASE [ExcelSearch];        -- <<< EDIT (database name)
GO

USE [ExcelSearch];                        -- <<< EDIT (database name)
GO

------------------------------------------------------------------------------
-- 1. (001_create_tables.sql) ImportBatches, Transactions
------------------------------------------------------------------------------
BEGIN TRANSACTION;

------------------------------------------------------------------------------
-- 1. ImportBatches
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ImportBatches', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ImportBatches
(
        Id            INT IDENTITY(1,1) NOT NULL,
        FileName      NVARCHAR(260)     NOT NULL,
        SheetName     NVARCHAR(100)     NULL,
        FileHash      CHAR(64)          NOT NULL,   -- SHA-256 hex; used for a "file already imported" WARNING, not a hard block
        ImportedAt    DATETIME2(0)      NOT NULL
                      CONSTRAINT DF_ImportBatches_ImportedAt DEFAULT SYSUTCDATETIME(),
        TotalRows     INT               NOT NULL CONSTRAINT DF_ImportBatches_TotalRows    DEFAULT 0,
        NewRows       INT               NOT NULL CONSTRAINT DF_ImportBatches_NewRows      DEFAULT 0,
        SkippedRows   INT               NOT NULL CONSTRAINT DF_ImportBatches_SkippedRows  DEFAULT 0,  -- exact duplicates
        UpdatedRows   INT               NOT NULL CONSTRAINT DF_ImportBatches_UpdatedRows  DEFAULT 0,  -- conflicts the user chose to overwrite
        RejectedRows  INT               NOT NULL CONSTRAINT DF_ImportBatches_RejectedRows DEFAULT 0,  -- invalid rows not imported

        CONSTRAINT PK_ImportBatches PRIMARY KEY CLUSTERED (Id)
    );
    CREATE NONCLUSTERED INDEX IX_ImportBatches_FileHash ON dbo.ImportBatches (FileHash);
END;

------------------------------------------------------------------------------
-- 2. Transactions
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Transactions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Transactions
(
        -- Keys ---------------------------------------------------------------
        PayOrderNo        NVARCHAR(50)     NOT NULL,
        TxnRefSeqNo       NVARCHAR(50)     NOT NULL,
        Id                UNIQUEIDENTIFIER NOT NULL
                          CONSTRAINT DF_Transactions_Id DEFAULT NEWSEQUENTIALID(),  -- becomes the PRIMARY KEY in section 5 (006)

        -- Import tracking ----------------------------------------------------
        ImportBatchId     INT              NOT NULL,
        SourceRowNumber   INT              NULL,        -- Excel row number in the source file
        RowHash           CHAR(64)         NOT NULL,    -- SHA-256 hex of normalized business columns
        CreatedAt         DATETIME2(0)     NOT NULL
                          CONSTRAINT DF_Transactions_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt         DATETIME2(0)     NULL,        -- set when a conflict is overwritten

        -- Business columns ---------------------------------------------------
        ValueDate         DATE             NOT NULL,
        BranchCode        NVARCHAR(20)     NULL,
        BranchName        NVARCHAR(100)    NULL,
        RegNo             NVARCHAR(50)     NULL,
        ApplicationNo     NVARCHAR(50)     NULL,
        PlotNo            NVARCHAR(50)     NULL,
        StreetNo          NVARCHAR(50)     NULL,
        ChallanNo         NVARCHAR(50)     NULL,        -- repeats across rows; NOT unique
        ChequeInstNo      NVARCHAR(50)     NULL,
        CustomerName      NVARCHAR(200)    NULL,
        Cnic              VARCHAR(20)      NULL,        -- digits only, dashes stripped by the importer

        Discount          DECIMAL(18,2)    NULL,
        DownPaymentAmount DECIMAL(18,2)    NULL,
        Debit             DECIMAL(18,2)    NULL,
        Credit            DECIMAL(18,2)    NULL,
        RunningBalance    DECIMAL(18,2)    NULL,

        BankName          NVARCHAR(100)    NULL,
        AccountNo         NVARCHAR(50)     NULL,
        Project           NVARCHAR(100)    NULL,
        DealerName        NVARCHAR(100)    NULL,
        DataSource        NVARCHAR(100)    NULL,        -- Excel column "Name of Data"

        Narration1        NVARCHAR(500)    NULL,
        Narration2        NVARCHAR(500)    NULL,
        Narration3        NVARCHAR(500)    NULL,
        Narration4        NVARCHAR(500)    NULL,
        Narration5        NVARCHAR(500)    NULL,

        -- Constraints (the key design here is replaced by section 5 / 006) ---
        CONSTRAINT PK_Transactions PRIMARY KEY CLUSTERED (PayOrderNo, TxnRefSeqNo),
        CONSTRAINT UQ_Transactions_Id UNIQUE NONCLUSTERED (Id),
        CONSTRAINT FK_Transactions_ImportBatches
            FOREIGN KEY (ImportBatchId) REFERENCES dbo.ImportBatches (Id)

        -- Indexes for the filter screen --------------------------------------
        -- (PayOrderNo is already covered by the leading column of the PK)
    );
    CREATE NONCLUSTERED INDEX IX_Transactions_TxnRefSeqNo ON dbo.Transactions (TxnRefSeqNo);
    CREATE NONCLUSTERED INDEX IX_Transactions_ValueDate ON dbo.Transactions (ValueDate);
    CREATE NONCLUSTERED INDEX IX_Transactions_Bank_Account ON dbo.Transactions (BankName, AccountNo);
    CREATE NONCLUSTERED INDEX IX_Transactions_Project ON dbo.Transactions (Project);
    CREATE NONCLUSTERED INDEX IX_Transactions_DealerName ON dbo.Transactions (DealerName);
    CREATE NONCLUSTERED INDEX IX_Transactions_CustomerName ON dbo.Transactions (CustomerName);
    CREATE NONCLUSTERED INDEX IX_Transactions_Cnic ON dbo.Transactions (Cnic);
    CREATE NONCLUSTERED INDEX IX_Transactions_ApplicationNo ON dbo.Transactions (ApplicationNo);
    CREATE NONCLUSTERED INDEX IX_Transactions_ChallanNo ON dbo.Transactions (ChallanNo);
    CREATE NONCLUSTERED INDEX IX_Transactions_ImportBatchId ON dbo.Transactions (ImportBatchId);
END;

COMMIT TRANSACTION;
GO

------------------------------------------------------------------------------
-- 2. (002_create_staging.sql) batch status columns, ImportStaging
------------------------------------------------------------------------------
BEGIN TRANSACTION;

------------------------------------------------------------------------------
-- 1. ImportBatches: lifecycle columns
--    Existing rows (if any) default to 'Committed'.
------------------------------------------------------------------------------
IF COL_LENGTH(N'dbo.ImportBatches', N'Status') IS NULL
BEGIN
    ALTER TABLE dbo.ImportBatches
        ADD Status VARCHAR(20) NOT NULL
            CONSTRAINT DF_ImportBatches_Status DEFAULT 'Committed'
            CONSTRAINT CK_ImportBatches_Status CHECK (Status IN ('Staged', 'Committed', 'Discarded'));
END;

IF COL_LENGTH(N'dbo.ImportBatches', N'CommittedAt') IS NULL
BEGIN
    ALTER TABLE dbo.ImportBatches
        ADD CommittedAt DATETIME2(0) NULL;
END;

------------------------------------------------------------------------------
-- 2. ImportStaging
------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ImportStaging', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ImportStaging
(
        -- Identity of the staged row ------------------------------------------
        ImportBatchId     INT           NOT NULL,
        ExcelRowNumber    INT           NOT NULL,   -- 1-based row in the source sheet

        -- Workflow ------------------------------------------------------------
        Status            TINYINT       NOT NULL
                          CONSTRAINT DF_ImportStaging_Status DEFAULT 0,   -- see codes above
        Overwrite         BIT           NOT NULL
                          CONSTRAINT DF_ImportStaging_Overwrite DEFAULT 0, -- user's choice for Conflict rows
        ErrorText         NVARCHAR(2000) NULL,      -- all validation errors for the row, joined
        RowHash           CHAR(64)      NULL,       -- SHA-256 hex; NULL when the row is invalid

        -- Parsed data (same types/sizes as dbo.Transactions, but all NULLable) -
        PayOrderNo        NVARCHAR(50)  NULL,
        TxnRefSeqNo       NVARCHAR(50)  NULL,
        ValueDate         DATE          NULL,
        BranchCode        NVARCHAR(20)  NULL,
        BranchName        NVARCHAR(100) NULL,
        RegNo             NVARCHAR(50)  NULL,
        ApplicationNo     NVARCHAR(50)  NULL,
        PlotNo            NVARCHAR(50)  NULL,
        StreetNo          NVARCHAR(50)  NULL,
        ChallanNo         NVARCHAR(50)  NULL,
        ChequeInstNo      NVARCHAR(50)  NULL,
        CustomerName      NVARCHAR(200) NULL,
        Cnic              VARCHAR(20)   NULL,

        Discount          DECIMAL(18,2) NULL,
        DownPaymentAmount DECIMAL(18,2) NULL,
        Debit             DECIMAL(18,2) NULL,
        Credit            DECIMAL(18,2) NULL,
        RunningBalance    DECIMAL(18,2) NULL,

        BankName          NVARCHAR(100) NULL,
        AccountNo         NVARCHAR(50)  NULL,
        Project           NVARCHAR(100) NULL,
        DealerName        NVARCHAR(100) NULL,
        DataSource        NVARCHAR(100) NULL,

        Narration1        NVARCHAR(500) NULL,
        Narration2        NVARCHAR(500) NULL,
        Narration3        NVARCHAR(500) NULL,
        Narration4        NVARCHAR(500) NULL,
        Narration5        NVARCHAR(500) NULL,

        CONSTRAINT PK_ImportStaging PRIMARY KEY CLUSTERED (ImportBatchId, ExcelRowNumber),
        CONSTRAINT FK_ImportStaging_ImportBatches
            FOREIGN KEY (ImportBatchId) REFERENCES dbo.ImportBatches (Id)

        -- Join to dbo.Transactions and find repeats inside the file

        -- Preview screen: page through one status at a time, and count per status
    );
    CREATE NONCLUSTERED INDEX IX_ImportStaging_Key ON dbo.ImportStaging (ImportBatchId, PayOrderNo, TxnRefSeqNo);
    CREATE NONCLUSTERED INDEX IX_ImportStaging_Status ON dbo.ImportStaging (ImportBatchId, Status, ExcelRowNumber);
END;

COMMIT TRANSACTION;
GO

------------------------------------------------------------------------------
-- 3. (003_concurrency.sql) ImportedBy, ExistingRowHash, stale-batch index
------------------------------------------------------------------------------
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
GO

------------------------------------------------------------------------------
-- 4. (004_enable_rcsi.sql) READ_COMMITTED_SNAPSHOT
--    Cannot run inside a transaction. Disconnects other sessions on this database.
------------------------------------------------------------------------------
-- Close the app and any SSMS windows connected to this database first;
-- WITH ROLLBACK IMMEDIATE disconnects other sessions.
IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 0)
    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO

------------------------------------------------------------------------------
-- 5. (006_surrogate_key.sql) Id becomes the primary key; PayOrderNo / TxnRefSeqNo optional;
--    SlipNo / NarrationCnic columns; new indexes. See Docs/KeylessImport.md.
------------------------------------------------------------------------------
SET QUOTED_IDENTIFIER ON;   -- required for filtered indexes
GO

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
GO

------------------------------------------------------------------------------
-- 6. (007_cash_matching.sql) BranchCode NVARCHAR(50); ImportStaging.MatchReason; IX_Transactions_Match.
--    See Docs/KeylessImport.md.
------------------------------------------------------------------------------
SET QUOTED_IDENTIFIER ON;
GO

BEGIN TRANSACTION;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Transactions') AND name = N'BranchCode' AND max_length < 100)
    ALTER TABLE dbo.Transactions ALTER COLUMN BranchCode NVARCHAR(50) NULL;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.ImportStaging') AND name = N'BranchCode' AND max_length < 100)
    ALTER TABLE dbo.ImportStaging ALTER COLUMN BranchCode NVARCHAR(50) NULL;

IF COL_LENGTH(N'dbo.ImportStaging', N'MatchReason') IS NULL
    ALTER TABLE dbo.ImportStaging ADD MatchReason TINYINT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Transactions_Match' AND object_id = OBJECT_ID(N'dbo.Transactions'))
    EXEC (N'CREATE NONCLUSTERED INDEX IX_Transactions_Match
              ON dbo.Transactions (ValueDate, BranchCode) INCLUDE (Credit, Debit, BankName, SlipNo)');

COMMIT TRANSACTION;
GO

------------------------------------------------------------------------------
-- 7. Final check: stop with an error if the database is not in the shape the app needs
------------------------------------------------------------------------------
DECLARE @problems NVARCHAR(MAX) = N'';
IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
               JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE i.object_id = OBJECT_ID(N'dbo.Transactions') AND i.is_primary_key = 1 AND c.name = N'Id')
    SET @problems += N' Transactions primary key is not on Id.';
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Transactions') AND name IN (N'PayOrderNo', N'TxnRefSeqNo') AND is_nullable = 0)
    SET @problems += N' PayOrderNo / TxnRefSeqNo are still NOT NULL.';
IF COL_LENGTH(N'dbo.Transactions', N'SlipNo') IS NULL OR COL_LENGTH(N'dbo.Transactions', N'NarrationCnic') IS NULL
   OR COL_LENGTH(N'dbo.ImportStaging', N'SlipNo') IS NULL OR COL_LENGTH(N'dbo.ImportStaging', N'NarrationCnic') IS NULL
    SET @problems += N' SlipNo / NarrationCnic columns are missing.';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Transactions_Key' AND object_id = OBJECT_ID(N'dbo.Transactions') AND is_unique = 1 AND has_filter = 1)
    SET @problems += N' UX_Transactions_Key (filtered unique) is missing.';
IF COL_LENGTH(N'dbo.ImportStaging', N'MatchReason') IS NULL
    SET @problems += N' ImportStaging.MatchReason is missing.';
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 1)
    SET @problems += N' READ_COMMITTED_SNAPSHOT is off.';
IF NOT (CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS NVARCHAR(128)) LIKE N'%[_]CI[_]%')
    SET @problems += N' The database collation is not case-insensitive (name has no _CI_): keys that differ only by case would not collide.';
IF @problems <> N''
BEGIN
    DECLARE @msg NVARCHAR(2000) = N'init_db.sql finished but the database is not ready:' + @problems;
    THROW 51000, @msg, 1;
END
PRINT N'ExcelSearch database is ready.';
GO

------------------------------------------------------------------------------
-- 8. Verification (read-only): tables and column counts, indexes, RCSI
------------------------------------------------------------------------------
SELECT t.name AS TableName, COUNT(c.column_id) AS ColumnCount
FROM sys.tables AS t
JOIN sys.columns AS c ON c.object_id = t.object_id
WHERE t.name IN (N'ImportBatches', N'Transactions', N'ImportStaging')
GROUP BY t.name
ORDER BY t.name;

SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName, i.type_desc, i.is_primary_key, i.is_unique
FROM sys.indexes AS i
WHERE i.object_id IN (OBJECT_ID(N'dbo.ImportBatches'), OBJECT_ID(N'dbo.Transactions'), OBJECT_ID(N'dbo.ImportStaging'))
  AND i.name IS NOT NULL
ORDER BY TableName, i.is_primary_key DESC, IndexName;

SELECT name AS DatabaseName, is_read_committed_snapshot_on
FROM sys.databases
WHERE database_id = DB_ID();
GO
