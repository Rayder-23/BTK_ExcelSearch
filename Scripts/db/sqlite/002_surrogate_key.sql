/*
================================================================================
  002_surrogate_key.sql   (SQLite)

  Lets bank-statement sheets that have NO PayOrderNo / TxnRefSeqNo be imported
  (the SQLite twin of Scripts/db/006_surrogate_key.sql; see Docs/KeylessImport.md).

    * Transactions.Id (TEXT GUID) becomes the PRIMARY KEY.
    * PayOrderNo and TxnRefSeqNo become NULLable (still COLLATE NOCASE).
    * Rows that have BOTH keep being unique: a partial unique index
      UX_Transactions_Key ... WHERE both are NOT NULL. Key-less rows are not restricted.
    * New nullable columns on Transactions and ImportStaging, derived at import from the
      narrations (not part of RowHash): SlipNo, NarrationCnic (digits only).
    * New indexes: IX_Transactions_PayOrderNo, IX_Transactions_RowHash, and partial
      indexes on SlipNo and NarrationCnic.
    * ImportStaging.Status allows 0..6 (6 = LikelyDuplicate).

  SQLite cannot change a primary key or a CHECK in place, so Transactions and ImportStaging
  are REBUILT: create *_new, copy the rows, drop the old table, rename. Existing
  Transactions rows keep every value (Id included); SlipNo / NarrationCnic are NULL for
  them. A batch that was still 'Staged' is marked 'Discarded' and its staging rows are
  removed: staging is only a work area, and the user can import the file again.

  Applied by SqliteSchemaInitializer after 001 when PRAGMA user_version is 1. Self-
  transactional; the last step sets PRAGMA user_version = 2. An older app refuses a
  version-2 file: copy the .db file (with -wal / -shm) before upgrading.
  To go back: Scripts/db/rollback/002_rollback_sqlite.sql
================================================================================
*/

BEGIN IMMEDIATE;

------------------------------------------------------------------------------
-- 1. Staging is a work area: drop what is in it
------------------------------------------------------------------------------
UPDATE ImportBatches SET Status = 'Discarded' WHERE Status = 'Staged';
DELETE FROM ImportStaging;

------------------------------------------------------------------------------
-- 2. Transactions: rebuild with Id as the primary key
------------------------------------------------------------------------------
DROP TABLE IF EXISTS Transactions_new;

CREATE TABLE Transactions_new
(
    -- Keys -------------------------------------------------------------------
    PayOrderNo        TEXT    NULL COLLATE NOCASE,
    TxnRefSeqNo       TEXT    NULL COLLATE NOCASE,
    Id                TEXT    NOT NULL
                      DEFAULT (upper(lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4'
                               || substr(lower(hex(randomblob(2))), 2) || '-'
                               || substr('89ab', abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))), 2) || '-'
                               || lower(hex(randomblob(6))))),
    SlipNo            TEXT    NULL,                       -- slip number found in Narration1
    NarrationCnic     TEXT    NULL,                       -- 13-digit CNIC found in Narrations 2-5, digits only
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
    CONSTRAINT PK_Transactions PRIMARY KEY (Id),
    CONSTRAINT FK_Transactions_ImportBatches FOREIGN KEY (ImportBatchId) REFERENCES ImportBatches (Id)
);

INSERT INTO Transactions_new (PayOrderNo, TxnRefSeqNo, Id, ImportBatchId, SourceRowNumber, RowHash, CreatedAt, UpdatedAt, ValueDate, BranchCode, BranchName, RegNo, ApplicationNo, PlotNo, StreetNo, ChallanNo, ChequeInstNo, CustomerName, Cnic, Discount, DownPaymentAmount, Debit, Credit, RunningBalance, BankName, AccountNo, Project, DealerName, DataSource, Narration1, Narration2, Narration3, Narration4, Narration5)
SELECT PayOrderNo, TxnRefSeqNo, Id, ImportBatchId, SourceRowNumber, RowHash, CreatedAt, UpdatedAt, ValueDate, BranchCode, BranchName, RegNo, ApplicationNo, PlotNo, StreetNo, ChallanNo, ChequeInstNo, CustomerName, Cnic, Discount, DownPaymentAmount, Debit, Credit, RunningBalance, BankName, AccountNo, Project, DealerName, DataSource, Narration1, Narration2, Narration3, Narration4, Narration5
FROM Transactions;

DROP TABLE Transactions;
ALTER TABLE Transactions_new RENAME TO Transactions;

-- Rows that have a full key stay unique; key-less rows are free.
CREATE UNIQUE INDEX UX_Transactions_Key ON Transactions (PayOrderNo, TxnRefSeqNo)
    WHERE PayOrderNo IS NOT NULL AND TxnRefSeqNo IS NOT NULL;

CREATE INDEX IF NOT EXISTS IX_Transactions_PayOrderNo    ON Transactions (PayOrderNo);
CREATE INDEX IF NOT EXISTS IX_Transactions_TxnRefSeqNo   ON Transactions (TxnRefSeqNo);
CREATE INDEX IF NOT EXISTS IX_Transactions_RowHash       ON Transactions (RowHash);
CREATE INDEX IF NOT EXISTS IX_Transactions_SlipNo        ON Transactions (SlipNo) WHERE SlipNo IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_Transactions_NarrationCnic ON Transactions (NarrationCnic) WHERE NarrationCnic IS NOT NULL;
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
-- 3. ImportStaging: rebuild with SlipNo / NarrationCnic and Status 0..6
--    Status codes: 0 Pending, 1 Invalid, 2 New, 3 ExactDuplicate, 4 FileDuplicate,
--    5 Conflict, 6 LikelyDuplicate. (Staging was emptied above, so nothing is copied.)
------------------------------------------------------------------------------
DROP TABLE ImportStaging;

CREATE TABLE ImportStaging
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
    SlipNo            TEXT    NULL,
    NarrationCnic     TEXT    NULL,
    CONSTRAINT PK_ImportStaging PRIMARY KEY (ImportBatchId, ExcelRowNumber),
    CONSTRAINT CK_ImportStaging_Status    CHECK (Status BETWEEN 0 AND 6),
    CONSTRAINT CK_ImportStaging_Overwrite CHECK (Overwrite IN (0, 1)),
    CONSTRAINT FK_ImportStaging_ImportBatches FOREIGN KEY (ImportBatchId) REFERENCES ImportBatches (Id)
);

CREATE INDEX IF NOT EXISTS IX_ImportStaging_Key    ON ImportStaging (ImportBatchId, PayOrderNo, TxnRefSeqNo);
CREATE INDEX IF NOT EXISTS IX_ImportStaging_Status ON ImportStaging (ImportBatchId, Status, ExcelRowNumber);

------------------------------------------------------------------------------
-- 4. Schema version
------------------------------------------------------------------------------
PRAGMA user_version = 2;

COMMIT;
