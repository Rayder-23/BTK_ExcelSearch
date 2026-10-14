/*
================================================================================
  002_rollback_sqlite.sql   (SQLite)

  Undoes 002_surrogate_key.sql: schema version 2 -> 1. The primary key goes back to
  (PayOrderNo, TxnRefSeqNo), both NOT NULL, and SlipNo / NarrationCnic are dropped.
  Use it together with a git revert of the key-less import change (Docs/KeylessImport.md).

  READ THIS FIRST
    * COPY THE DATABASE FILE FIRST (the .db, plus -wal and -shm if present).
    * Run it so that the FIRST error stops the script:
          sqlite3 -bail excelsearch.db ".read 002_rollback_sqlite.sql"
      Without -bail a failure in the copy step would be followed by the DROP of the
      current table. The copy fails, and nothing has been dropped yet, when any row has
      no PayOrderNo or no TxnRefSeqNo (the old primary key cannot hold it): delete those
      rows first, knowing that they are lost:
          DELETE FROM Transactions WHERE PayOrderNo IS NULL OR TxnRefSeqNo IS NULL;
    * Batches still 'Staged' are marked 'Discarded' and staging is emptied.
    * The app must be closed.
================================================================================
*/

BEGIN IMMEDIATE;

UPDATE ImportBatches SET Status = 'Discarded' WHERE Status = 'Staged';
DELETE FROM ImportStaging;

DROP TABLE IF EXISTS Transactions_old;
CREATE TABLE Transactions_old
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

-- Fails here (NOT NULL on the key columns) if a key-less row exists. Nothing is dropped before this.
INSERT INTO Transactions_old (PayOrderNo, TxnRefSeqNo, Id, ImportBatchId, SourceRowNumber, RowHash, CreatedAt, UpdatedAt, ValueDate, BranchCode, BranchName, RegNo, ApplicationNo, PlotNo, StreetNo, ChallanNo, ChequeInstNo, CustomerName, Cnic, Discount, DownPaymentAmount, Debit, Credit, RunningBalance, BankName, AccountNo, Project, DealerName, DataSource, Narration1, Narration2, Narration3, Narration4, Narration5)
SELECT PayOrderNo, TxnRefSeqNo, Id, ImportBatchId, SourceRowNumber, RowHash, CreatedAt, UpdatedAt, ValueDate, BranchCode, BranchName, RegNo, ApplicationNo, PlotNo, StreetNo, ChallanNo, ChequeInstNo, CustomerName, Cnic, Discount, DownPaymentAmount, Debit, Credit, RunningBalance, BankName, AccountNo, Project, DealerName, DataSource, Narration1, Narration2, Narration3, Narration4, Narration5
FROM Transactions;

DROP TABLE Transactions;
ALTER TABLE Transactions_old RENAME TO Transactions;

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
    CONSTRAINT PK_ImportStaging PRIMARY KEY (ImportBatchId, ExcelRowNumber),
    CONSTRAINT CK_ImportStaging_Status    CHECK (Status BETWEEN 0 AND 5),
    CONSTRAINT CK_ImportStaging_Overwrite CHECK (Overwrite IN (0, 1)),
    CONSTRAINT FK_ImportStaging_ImportBatches FOREIGN KEY (ImportBatchId) REFERENCES ImportBatches (Id)
);

CREATE INDEX IF NOT EXISTS IX_ImportStaging_Key    ON ImportStaging (ImportBatchId, PayOrderNo, TxnRefSeqNo);
CREATE INDEX IF NOT EXISTS IX_ImportStaging_Status ON ImportStaging (ImportBatchId, Status, ExcelRowNumber);

PRAGMA user_version = 1;

COMMIT;
