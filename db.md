# ExcelSearch Database Schema
Version: 1.30 | Last Updated: 2026-10-09
Database: ExcelSearch
Server: LOCAL

## MAINTENANCE RULES:
    1. Bump Version and Last Updated above whenever this file changes
    2. Every table's dated inline notes explain WHY a column/table exists or changed — keep new notes dated (YYYY-MM-DD) and specific, don't just describe the column type
    3. A table marked [REMOVED] stays in this file as a historical/legacy record — do not delete those sections, they document what old code may still (incorrectly) reference
    4. Cross-reference api.txt/api.md (if it exists) wherever a table is directly exposed/managed via a REST endpoint
---

Source of truth for the structure: `Scripts/db/001_create_tables.sql` (re-runnable; creates a table only if it does not exist). EF Core entities in `src/ExcelSearch.Data` are scaffolded from this schema. Collation: `SQL_Latin1_General_CP1_CI_AS` (case-insensitive).

## Relationships
- `Transactions.ImportBatchId` → `ImportBatches.Id` (`FK_Transactions_ImportBatches`, no cascade)
- `ImportStaging.ImportBatchId` → `ImportBatches.Id` (`FK_ImportStaging_ImportBatches`, no cascade)

## Table: dbo.ImportBatches
One row per imported Excel file (audit trail and row counts).

- 2026-10-08: Created by `001_create_tables.sql`. Exists so every `Transactions` row can be traced to the file it came from, and so a re-import of the same file can be warned about.

| Column | Type | Null | Default | Notes |
|---|---|---|---|---|
| Id | int IDENTITY(1,1) | NOT NULL | | PK. |
| FileName | nvarchar(260) | NOT NULL | | Imported file name. |
| SheetName | nvarchar(100) | NULL | | Worksheet that was read. |
| FileHash | char(64) | NOT NULL | | SHA-256 hex of the file. Used for a "file already imported" WARNING only, not a hard block (index is non-unique). |
| ImportedAt | datetime2(0) | NOT NULL | sysutcdatetime() | UTC. |
| TotalRows | int | NOT NULL | 0 | Data rows found in the file. |
| NewRows | int | NOT NULL | 0 | Rows inserted. |
| SkippedRows | int | NOT NULL | 0 | Exact duplicates (same key, same RowHash). |
| UpdatedRows | int | NOT NULL | 0 | Conflicts (same key, different RowHash) the user chose to overwrite. |
| RejectedRows | int | NOT NULL | 0 | Invalid rows that were not imported. |

| Status | varchar(20) | NOT NULL | 'Committed' | `Staged` / `Committed` / `Discarded` (`CK_ImportBatches_Status`). Added 2026-10-09. |
| CommittedAt | datetime2(0) | NULL | | UTC time the batch was committed. Added 2026-10-09. |
| ImportedBy | nvarchar(256) | NULL | | Windows user (DOMAIN\user) who created the batch. NULL for batches that predate it. Added 2026-10-09. |

- 2026-10-09: `Status` and `CommittedAt` added by `002_create_staging.sql` for the staging design (files can exceed 100k rows). Rows that existed before default to `Committed`.

- 2026-10-09: `ImportedBy` added by `003_concurrency.sql` so several users can import at once: lists "my imports" and lets cleanup act on one user's batches.

Indexes: `PK_ImportBatches` (clustered, Id); `IX_ImportBatches_FileHash` (FileHash); `IX_ImportBatches_Status_ImportedAt` (Status, ImportedAt) for finding stale 'Staged' batches.

## Table: dbo.Transactions
One row per Excel data row.

- 2026-10-08: Created by `001_create_tables.sql`.
- 2026-10-08: Primary key is the business key (PayOrderNo, TxnRefSeqNo), both required text. Chosen so a duplicate key is rejected by the database itself (error 2627), not only by import code.
- 2026-10-08: `Id` (GUID) is a spare surrogate: unique, supplied by the app, NOT the PK. If real data turns out to have gaps or non-unique key pairs, promote Id to PK and demote the pair to a normal index.
- 2026-10-08: Identifiers (account no, CNIC, refs…) are text because Excel mixes numbers and strings, values can exceed INT range, and leading zeros must survive. Amounts are decimal(18,2).
- 2026-10-08: Because the collation is case-insensitive, keys differing only by case (`ab1` / `AB1`) count as the same key, and trailing spaces are ignored in comparisons.

Keys and tracking

| Column | Type | Null | Default | Notes |
|---|---|---|---|---|
| PayOrderNo | nvarchar(50) | NOT NULL | | PK part 1. Excel: `payorder no`. |
| TxnRefSeqNo | nvarchar(50) | NOT NULL | | PK part 2. Excel: `Txn Ref/ Seq No`. |
| Id | uniqueidentifier | NOT NULL | newsequentialid() | Unique (`UQ_Transactions_Id`). The app normally supplies its own GUID. |
| ImportBatchId | int | NOT NULL | | FK → ImportBatches.Id. |
| SourceRowNumber | int | NULL | | Excel row number in the source file. |
| RowHash | char(64) | NOT NULL | | SHA-256 hex of the normalized business columns. Same key + same hash = exact duplicate (skip); same key + different hash = conflict (user decides). |
| CreatedAt | datetime2(0) | NOT NULL | sysutcdatetime() | UTC. |
| UpdatedAt | datetime2(0) | NULL | | Set when a conflict is overwritten. |

Business columns (Excel header → column)

| Column | Type | Null | Excel header | Notes |
|---|---|---|---|---|
| ValueDate | date | NOT NULL | Value Date | |
| BranchCode | nvarchar(20) | NULL | Br. Code | |
| BranchName | nvarchar(100) | NULL | Branch Name | |
| RegNo | nvarchar(50) | NULL | Reg No | |
| ApplicationNo | nvarchar(50) | NULL | Application no | |
| PlotNo | nvarchar(50) | NULL | plot no | |
| StreetNo | nvarchar(50) | NULL | Street No | |
| ChallanNo | nvarchar(50) | NULL | Challan No | Repeats across rows; NOT unique. |
| ChequeInstNo | nvarchar(50) | NULL | Cheque Inst. No. | |
| CustomerName | nvarchar(200) | NULL | Customer Name | |
| Cnic | varchar(20) | NULL | CNIC No | Digits only; the importer strips dashes. |
| Discount | decimal(18,2) | NULL | Discount | |
| DownPaymentAmount | decimal(18,2) | NULL | Down payment Amount | |
| Debit | decimal(18,2) | NULL | Debit | |
| Credit | decimal(18,2) | NULL | Credit | |
| RunningBalance | decimal(18,2) | NULL | Run. Bal | |
| BankName | nvarchar(100) | NULL | Bank Name | |
| AccountNo | nvarchar(50) | NULL | Acct no | |
| Project | nvarchar(100) | NULL | Project | |
| DealerName | nvarchar(100) | NULL | Delear Name [sic] | Header is misspelled in the source files. |
| DataSource | nvarchar(100) | NULL | Name of Data [sic] | |
| Narration1 | nvarchar(500) | NULL | Narraction 1 [sic] | Header is misspelled in the source files. |
| Narration2 | nvarchar(500) | NULL | Narration 2 | |
| Narration3 | nvarchar(500) | NULL | Narration 3 | |
| Narration4 | nvarchar(500) | NULL | Narration 4 | |
| Narration5 | nvarchar(500) | NULL | Narration 5 | |

Indexes
- `PK_Transactions` — clustered, (PayOrderNo, TxnRefSeqNo). Also covers lookups by PayOrderNo alone (leading column).
- `UQ_Transactions_Id` — unique, (Id).
- Non-unique, for the filter screen: `IX_Transactions_TxnRefSeqNo` (TxnRefSeqNo), `IX_Transactions_ValueDate` (ValueDate), `IX_Transactions_Bank_Account` (BankName, AccountNo), `IX_Transactions_Project` (Project), `IX_Transactions_DealerName` (DealerName), `IX_Transactions_CustomerName` (CustomerName), `IX_Transactions_Cnic` (Cnic), `IX_Transactions_ApplicationNo` (ApplicationNo), `IX_Transactions_ChallanNo` (ChallanNo), `IX_Transactions_ImportBatchId` (ImportBatchId).

## Table: dbo.ImportStaging
Every parsed Excel row of a not-yet-committed batch (valid and invalid), so large files are classified in SQL instead of in memory.

- 2026-10-09: Created by `002_create_staging.sql`. The app bulk-loads a batch, SQL classifies rows set-based, the preview pages through them by Status, and commit/discard deletes the batch's staging rows.
- 2026-10-09: All data columns are NULLable because invalid rows may lack anything; the parser truncates over-long values to these sizes so a staging insert cannot fail on length. Types and sizes otherwise match `Transactions`.
- 2026-10-09: `ErrorText` is nvarchar(2000): the app must cap the joined error text to that length.

| Column | Type | Null | Default | Notes |
|---|---|---|---|---|
| ImportBatchId | int | NOT NULL | | PK part 1. FK → ImportBatches.Id. |
| ExcelRowNumber | int | NOT NULL | | PK part 2. 1-based row in the source sheet. |
| Status | tinyint | NOT NULL | 0 | 0 Pending, 1 Invalid, 2 New, 3 ExactDuplicate, 4 FileDuplicate, 5 Conflict. |
| Overwrite | bit | NOT NULL | 0 | User's choice for Conflict rows. |
| ErrorText | nvarchar(2000) | NULL | | Validation errors for the row, joined. |
| RowHash | char(64) | NULL | | SHA-256 hex; NULL when the row failed validation while parsing. |
| ExistingRowHash | char(64) | NULL | | For Conflict rows: RowHash of the matching `Transactions` row at classification time. Commit compares it with the current hash and aborts if the row changed since the preview. Added 2026-10-09. |
| PayOrderNo … Narration5 | as in Transactions | NULL | | Same names, types and sizes as the `Transactions` business columns (ValueDate is date). |

Indexes: `PK_ImportStaging` (clustered, ImportBatchId + ExcelRowNumber); `IX_ImportStaging_Key` (ImportBatchId, PayOrderNo, TxnRefSeqNo); `IX_ImportStaging_Status` (ImportBatchId, Status, ExcelRowNumber).

## Change History
- 2026-10-08: Script `001_create_tables.sql` run against the local `ExcelSearch` database; created `dbo.ImportBatches` (10 columns) and `dbo.Transactions` (34 columns) with all keys and indexes above. Verified that the PK is (PayOrderNo, TxnRefSeqNo) and that a duplicate key insert fails with error 2627.
- 2026-10-08: Ran `DBCC CHECKIDENT ('dbo.ImportBatches', RESEED, 0)` while the table was empty, so the first real batch gets Id 1. A rolled-back test insert during verification had consumed identity value 1. No data change.
- 2026-10-09: This file expanded from the empty template to document both tables, relationships, indexes and history. Introspected from the live database; structure matches the script exactly. Both tables currently hold 0 rows.
- 2026-10-09: Ran `002_create_staging.sql`: added `ImportBatches.Status` / `CommittedAt` and created `dbo.ImportStaging` with its keys and indexes. Verified in a rolled-back transaction that a second staging row with the same (ImportBatchId, ExcelRowNumber) fails with error 2627. Re-seeded `ImportBatches` identity to 0 afterwards (the test consumed value 1). Both tables hold 0 rows.
- 2026-10-09: Ran `003_concurrency.sql`: added `ImportBatches.ImportedBy`, `ImportStaging.ExistingRowHash` and `IX_ImportBatches_Status_ImportedAt`. `READ_COMMITTED_SNAPSHOT` deliberately left OFF (the optional line in the script stays commented). No identity reseed. Integration tests run against this same database using unique `TEST_`-prefixed keys and delete only their own rows (batch ids are consumed, so Ids have gaps). A temporary `ExcelSearch_Test` database was created and dropped the same day.
