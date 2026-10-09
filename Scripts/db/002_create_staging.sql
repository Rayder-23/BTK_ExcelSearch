/*
================================================================================
  002_create_staging.sql   (SQL Server / T-SQL)   -- run AFTER 001_create_tables.sql

  Adds:
    * dbo.ImportBatches.Status / CommittedAt  - lifecycle of one import
    * dbo.ImportStaging                       - every parsed Excel row of a batch,
                                                held here until the user commits

  How staging is used
    1. App creates an ImportBatches row (Status = 'Staged') and bulk-loads ALL parsed
       rows (valid AND invalid) into ImportStaging.
    2. SQL classifies the rows set-based (no big lists in memory):
         Pending -> Invalid / New / ExactDuplicate / FileDuplicate / Conflict
    3. The preview screen pages through ImportStaging by Status.
    4. Commit (one transaction): INSERT ... SELECT the 'New' rows into Transactions,
       UPDATE the 'Conflict' rows the user chose to overwrite, DELETE the batch's
       staging rows, set ImportBatches.Status = 'Committed' and its counts.
    5. Discard: DELETE the batch's staging rows, Status = 'Discarded'.

  Status codes (ImportStaging.Status)
    0 Pending        loaded, not yet classified
    1 Invalid        has validation errors (see ErrorText)
    2 New            key not in Transactions
    3 ExactDuplicate key in Transactions with the same RowHash -> skipped
    4 FileDuplicate  repeat of an identical row earlier in the same file -> skipped
    5 Conflict       key in Transactions but RowHash differs -> user decides (Overwrite)

  Notes
    * All data columns are NULLable: invalid rows may be missing anything. The parser
      must truncate values to these lengths for rows it marks invalid, so a staging
      insert can never fail on length.
    * Column sizes and collation match dbo.Transactions, so key comparison behaves
      identically (case-insensitive on a default SQL Server collation).
    * Safe to re-run.
================================================================================
*/

-- >>> Edit this line to your database name, or select it in your SQL tool <<<
-- USE [YourDatabaseName];

SET NOCOUNT ON;
SET XACT_ABORT ON;

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
            FOREIGN KEY (ImportBatchId) REFERENCES dbo.ImportBatches (Id),

        -- Join to dbo.Transactions and find repeats inside the file
        INDEX IX_ImportStaging_Key    NONCLUSTERED (ImportBatchId, PayOrderNo, TxnRefSeqNo),
        -- Preview screen: page through one status at a time, and count per status
        INDEX IX_ImportStaging_Status NONCLUSTERED (ImportBatchId, Status, ExcelRowNumber)
    );
END;

COMMIT TRANSACTION;

------------------------------------------------------------------------------
-- 3. Quick verification (read-only)
------------------------------------------------------------------------------
SELECT c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType, c.max_length, c.is_nullable
FROM sys.columns AS c
WHERE c.object_id IN (OBJECT_ID(N'dbo.ImportStaging'))
   OR (c.object_id = OBJECT_ID(N'dbo.ImportBatches') AND c.name IN (N'Status', N'CommittedAt'))
ORDER BY c.object_id, c.column_id;

/*
-- ROLLBACK (development only)
-- DROP TABLE IF EXISTS dbo.ImportStaging;
-- ALTER TABLE dbo.ImportBatches DROP CONSTRAINT CK_ImportBatches_Status, DF_ImportBatches_Status;
-- ALTER TABLE dbo.ImportBatches DROP COLUMN Status, CommittedAt;
*/
