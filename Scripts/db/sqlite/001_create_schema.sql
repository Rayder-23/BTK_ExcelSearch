/*
================================================================================
  001_create_schema.sql   (SQLite)

  The local-database schema of ExcelSearch: the same three tables as the SQL Server
  scripts (001_create_tables.sql + 002_create_staging.sql + 003_concurrency.sql),
  in SQLite's dialect, in ONE re-runnable script.

    ImportBatches   one row per imported file (Status: Staged / Committed / Discarded)
    Transactions    one row per Excel data row; PK = the business key (PayOrderNo, TxnRefSeqNo)
    ImportStaging   rows of a file that is loaded but not committed yet (PK = ImportBatchId, ExcelRowNumber)

  Applied by SqliteSchemaInitializer at app start when the database is empty. The script is
  embedded in the app; the schema version lives in PRAGMA user_version (this script sets it
  to 1 as its last step). The app refuses to open a database whose user_version is NEWER than
  the newest script it knows. A future change is a new file 002_*.sql that ends with
  PRAGMA user_version = 2;  (never edit this one once released).

  Safe to re-run: every object is created only if it is missing. Nothing is dropped.
  By hand:  sqlite3 excelsearch.db ".read 001_create_schema.sql"

  Per-connection settings are NOT in this file (they do not persist in the database file, or
  cannot run inside a transaction): the app runs  PRAGMA foreign_keys = ON,
  PRAGMA busy_timeout = 30000 and PRAGMA synchronous = NORMAL  on every connection, and
  PRAGMA journal_mode = WAL  once when it creates the database.

  --------------------------------------------------------------------------
  TYPE MAPPING  (SQL Server -> SQLite)
  --------------------------------------------------------------------------
  SQLite has only five storage classes (NULL, INTEGER, REAL, TEXT, BLOB) and does not
  enforce lengths, so the SQL Server types are mapped on purpose:

    NVARCHAR(n), VARCHAR(n), CHAR(n)
        -> TEXT. Lengths are not enforced; the parser already truncates to the
           SQL Server lengths, so the data is identical.
    INT, TINYINT
        -> INTEGER. TINYINT's range is kept with a CHECK (Status BETWEEN 0 AND 5).
    BIT
        -> INTEGER 0/1, with a CHECK (Overwrite IN (0, 1)).
    DATE
        -> TEXT 'yyyy-MM-dd' (ISO 8601). Sorts and compares correctly as text.
    DATETIME2(0) (timestamps, always UTC)
        -> TEXT 'yyyy-MM-ddTHH:mm:ss.fffZ' (ISO 8601 UTC, millisecond precision). Defaults use
           strftime('%Y-%m-%dT%H:%M:%fZ', 'now'). Compares correctly as text.
    UNIQUEIDENTIFIER (Transactions.Id)
        -> TEXT, an upper-case GUID such as 'B6F9E3A2-...' (the form EF Core writes). The
           default builds a random version-4 GUID in SQL, so INSERT ... SELECT can leave Id out
           exactly like SQL Server's NEWSEQUENTIALID() default. (Not sequential: SQLite has no
           such function, and Id is not the key.)
    INT IDENTITY(1,1) (ImportBatches.Id)
        -> INTEGER PRIMARY KEY AUTOINCREMENT. AUTOINCREMENT, not just INTEGER PRIMARY KEY, so an
           id is never reused after a batch row is deleted (SQL Server never reuses identities).
    DECIMAL(18,2) money columns (Discount, DownPaymentAmount, Debit, Credit, RunningBalance)
        -> INTEGER holding MINOR UNITS (cents): 12.50 is stored as 1250. Reason: SQLite has no
           decimal type. EF Core would store a decimal as TEXT, and then range filters
           (Debit >= 100), ORDER BY and SUM() compare or add TEXT and give wrong answers
           ('9' > '10'). Integers compare, sort and add exactly. The conversion happens in
           one place (an EF value converter used only on SQLite, and the staging loader):
           decimal x -> round(x * 100, half away from zero), and back with x / 100.
           Anything typed by hand in SQL must use cents too:  WHERE Debit >= 10000  means 100.00.
    Case-insensitive collation (SQL Server's default database collation)
        -> COLLATE NOCASE on the KEY columns PayOrderNo and TxnRefSeqNo, everywhere they appear
           (Transactions, ImportStaging, and so their indexes), so 'abc' and 'ABC' are the same key
           and a duplicate, as on SQL Server. NOCASE folds ASCII letters only (see db.md).
           Other text columns keep SQLite's default BINARY collation for now.

  Table layout: ordinary rowid tables. WITHOUT ROWID was measured for Transactions and
  ImportStaging and not kept; the numbers are in docs/Performance.md.
================================================================================
*/

BEGIN IMMEDIATE;

------------------------------------------------------------------------------
-- 1. ImportBatches
------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ImportBatches
(
    Id            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    FileName      TEXT    NOT NULL,                       -- <= 260 chars (not enforced)
    SheetName     TEXT    NULL,                           -- <= 100
    FileHash      TEXT    NOT NULL,                       -- SHA-256 hex, 64 chars; for the "file already imported" warning
    ImportedAt    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    TotalRows     INTEGER NOT NULL DEFAULT 0,
    NewRows       INTEGER NOT NULL DEFAULT 0,
    SkippedRows   INTEGER NOT NULL DEFAULT 0,             -- exact duplicates
    UpdatedRows   INTEGER NOT NULL DEFAULT 0,             -- conflicts the user chose to overwrite
    RejectedRows  INTEGER NOT NULL DEFAULT 0,             -- invalid rows not imported
    Status        TEXT    NOT NULL DEFAULT 'Committed',
    CommittedAt   TEXT    NULL,
    ImportedBy    TEXT    NULL,                           -- OS user who ran the import, <= 256
    CONSTRAINT CK_ImportBatches_Status CHECK (Status IN ('Staged', 'Committed', 'Discarded'))
);

CREATE INDEX IF NOT EXISTS IX_ImportBatches_FileHash         ON ImportBatches (FileHash);
CREATE INDEX IF NOT EXISTS IX_ImportBatches_Status_ImportedAt ON ImportBatches (Status, ImportedAt);

------------------------------------------------------------------------------
-- 2. Transactions
------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Transactions
(
    -- Keys -------------------------------------------------------------------
    PayOrderNo        TEXT    NOT NULL COLLATE NOCASE,
    TxnRefSeqNo       TEXT    NOT NULL COLLATE NOCASE,
    Id                TEXT    NOT NULL
                      DEFAULT (upper(lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4'
                               || substr(lower(hex(randomblob(2))), 2) || '-'
                               || substr('89ab', abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))), 2) || '-'
                               || lower(hex(randomblob(6))))),
    -- Import tracking --------------------------------------------------------
    ImportBatchId     INTEGER NOT NULL,
    SourceRowNumber   INTEGER NULL,                       -- Excel row number in the source file
    RowHash           TEXT    NOT NULL,                   -- SHA-256 hex of the normalized business columns
    CreatedAt         TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    UpdatedAt         TEXT    NULL,                       -- set when a conflict is overwritten
    -- Business columns -------------------------------------------------------
    ValueDate         TEXT    NOT NULL,                   -- 'yyyy-MM-dd'
    BranchCode        TEXT    NULL,
    BranchName        TEXT    NULL,
    RegNo             TEXT    NULL,
    ApplicationNo     TEXT    NULL,
    PlotNo            TEXT    NULL,
    StreetNo          TEXT    NULL,
    ChallanNo         TEXT    NULL,                       -- repeats across rows; NOT unique
    ChequeInstNo      TEXT    NULL,
    CustomerName      TEXT    NULL,
    Cnic              TEXT    NULL,                       -- digits only
    Discount          INTEGER NULL,                       -- money: minor units (cents)
    DownPaymentAmount INTEGER NULL,                       -- money: minor units (cents)
    Debit             INTEGER NULL,                       -- money: minor units (cents)
    Credit            INTEGER NULL,                       -- money: minor units (cents)
    RunningBalance    INTEGER NULL,                       -- money: minor units (cents)
    BankName          TEXT    NULL,
    AccountNo         TEXT    NULL,
    Project           TEXT    NULL,
    DealerName        TEXT    NULL,
    DataSource        TEXT    NULL,                       -- Excel column "Name of Data"
    Narration1        TEXT    NULL,
    Narration2        TEXT    NULL,
    Narration3        TEXT    NULL,
    Narration4        TEXT    NULL,
    Narration5        TEXT    NULL,
    -- Constraints ------------------------------------------------------------
    CONSTRAINT PK_Transactions PRIMARY KEY (PayOrderNo, TxnRefSeqNo),
    CONSTRAINT UQ_Transactions_Id UNIQUE (Id),
    CONSTRAINT FK_Transactions_ImportBatches FOREIGN KEY (ImportBatchId) REFERENCES ImportBatches (Id)
);

-- The same filter indexes as SQL Server (PayOrderNo is covered by the leading column of the PK).
CREATE INDEX IF NOT EXISTS IX_Transactions_TxnRefSeqNo   ON Transactions (TxnRefSeqNo);
CREATE INDEX IF NOT EXISTS IX_Transactions_ValueDate     ON Transactions (ValueDate);
CREATE INDEX IF NOT EXISTS IX_Transactions_Bank_Account  ON Transactions (BankName, AccountNo);
CREATE INDEX IF NOT EXISTS IX_Transactions_Project       ON Transactions (Project);
CREATE INDEX IF NOT EXISTS IX_Transactions_DealerName    ON Transactions (DealerName);
CREATE INDEX IF NOT EXISTS IX_Transactions_CustomerName  ON Transactions (CustomerName);
CREATE INDEX IF NOT EXISTS IX_Transactions_Cnic          ON Transactions (Cnic);
CREATE INDEX IF NOT EXISTS IX_Transactions_ApplicationNo ON Transactions (ApplicationNo);
CREATE INDEX IF NOT EXISTS IX_Transactions_ChallanNo     ON Transactions (ChallanNo);
CREATE INDEX IF NOT EXISTS IX_Transactions_ImportBatchId ON Transactions (ImportBatchId);

------------------------------------------------------------------------------
-- 3. ImportStaging
--    Status codes: 0 Pending, 1 Invalid, 2 New, 3 ExactDuplicate, 4 FileDuplicate, 5 Conflict.
--    All data columns are NULLable: an invalid row may be missing anything.
------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ImportStaging
(
    ImportBatchId     INTEGER NOT NULL,
    ExcelRowNumber    INTEGER NOT NULL,                   -- 1-based row in the source sheet
    Status            INTEGER NOT NULL DEFAULT 0,
    Overwrite         INTEGER NOT NULL DEFAULT 0,         -- the user's choice for Conflict rows
    ErrorText         TEXT    NULL,                       -- all validation errors for the row, joined; <= 2000
    RowHash           TEXT    NULL,                       -- NULL when the row is invalid
    ExistingRowHash   TEXT    NULL,                       -- for Conflict rows: the Transactions hash seen at classify time
    PayOrderNo        TEXT    NULL COLLATE NOCASE,
    TxnRefSeqNo       TEXT    NULL COLLATE NOCASE,
    ValueDate         TEXT    NULL,                       -- 'yyyy-MM-dd'
    BranchCode        TEXT    NULL,
    BranchName        TEXT    NULL,
    RegNo             TEXT    NULL,
    ApplicationNo     TEXT    NULL,
    PlotNo            TEXT    NULL,
    StreetNo          TEXT    NULL,
    ChallanNo         TEXT    NULL,
    ChequeInstNo      TEXT    NULL,
    CustomerName      TEXT    NULL,
    Cnic              TEXT    NULL,
    Discount          INTEGER NULL,                       -- money: minor units (cents)
    DownPaymentAmount INTEGER NULL,
    Debit             INTEGER NULL,
    Credit            INTEGER NULL,
    RunningBalance    INTEGER NULL,
    BankName          TEXT    NULL,
    AccountNo         TEXT    NULL,
    Project           TEXT    NULL,
    DealerName        TEXT    NULL,
    DataSource        TEXT    NULL,
    Narration1        TEXT    NULL,
    Narration2        TEXT    NULL,
    Narration3        TEXT    NULL,
    Narration4        TEXT    NULL,
    Narration5        TEXT    NULL,
    CONSTRAINT PK_ImportStaging PRIMARY KEY (ImportBatchId, ExcelRowNumber),
    CONSTRAINT CK_ImportStaging_Status    CHECK (Status BETWEEN 0 AND 5),
    CONSTRAINT CK_ImportStaging_Overwrite CHECK (Overwrite IN (0, 1)),
    CONSTRAINT FK_ImportStaging_ImportBatches FOREIGN KEY (ImportBatchId) REFERENCES ImportBatches (Id)
);

CREATE INDEX IF NOT EXISTS IX_ImportStaging_Key    ON ImportStaging (ImportBatchId, PayOrderNo, TxnRefSeqNo);
CREATE INDEX IF NOT EXISTS IX_ImportStaging_Status ON ImportStaging (ImportBatchId, Status, ExcelRowNumber);

------------------------------------------------------------------------------
-- 4. Schema version
------------------------------------------------------------------------------
PRAGMA user_version = 1;

COMMIT;
