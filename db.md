# ExcelSearch Database Schema
Version: 1.37 | Last Updated: 2026-10-19
Database: ExcelSearch
Server: LOCAL

## MAINTENANCE RULES:
    1. Bump Version and Last Updated above whenever this file changes
    2. Every table's dated inline notes explain WHY a column/table exists or changed — keep new notes dated (YYYY-MM-DD) and specific, don't just describe the column type
    3. A table marked [REMOVED] stays in this file as a historical/legacy record — do not delete those sections, they document what old code may still (incorrectly) reference
    4. Cross-reference api.txt/api.md (if it exists) wherever a table is directly exposed/managed via a REST endpoint
---

Source of truth for the structure: `Scripts/db/001_create_tables.sql`, then `002`, `003`, `004` and `006` (each re-runnable; 006, added 2026-10-14, replaces the business key with a surrogate primary key, see the notes under Transactions and `Docs/KeylessImport.md`). `Scripts/db/init_db.sql` joins 001-004 and 006 into one re-runnable script that also creates the database if it is missing; use it to build a fresh database. `005_query_indexes.sql` is a draft and is not part of the schema. EF Core entities in `src/ExcelSearch.Data` are scaffolded from this schema. Collation: `SQL_Latin1_General_CP1_CI_AS` (case-insensitive).

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
| NewRows | int | NOT NULL | 0 | Rows inserted (since 2026-10-14 this includes flagged duplicates that were imported anyway). |
| SkippedRows | int | NOT NULL | 0 | Valid rows left out: duplicates that were skipped (keyed ones always; key-less ones when the user chose to skip flagged duplicates) and conflicts that were not overwritten. |
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
- 2026-10-08: Primary key was the business key (PayOrderNo, TxnRefSeqNo), both required text. Chosen so a duplicate key is rejected by the database itself (error 2627), not only by import code.
- 2026-10-08: `Id` (GUID) was a spare surrogate: unique, NOT the PK. The note here said that if real data turned out to have gaps or non-unique key pairs, Id should become the PK and the pair a normal index.
- 2026-10-14: **That day came.** The real bank-statement file has no PayOrderNo on 99.99% of rows and no TxnRefSeqNo on all but one (most deposits are cash). `006_surrogate_key.sql` made `Id` the clustered PRIMARY KEY (`PK_Transactions`), made PayOrderNo and TxnRefSeqNo NULLable, and kept keyed rows unique with a FILTERED unique index `UX_Transactions_Key (PayOrderNo, TxnRefSeqNo) WHERE both are NOT NULL`. Rows without a full key are NOT restricted: identical rows are allowed and are flagged at import, not blocked. A row with only one of the two values counts as key-less. Rollback: `Scripts/db/rollback/006_rollback.sql`.
- 2026-10-14: `SlipNo` and `NarrationCnic` added (see the column table): hints extracted at import from the narrations, because the narrations are all that identify a cash deposit. They are not part of `RowHash`, and not reliable (typed by bank staff).
- 2026-10-08: Identifiers (account no, CNIC, refs…) are text because Excel mixes numbers and strings, values can exceed INT range, and leading zeros must survive. Amounts are decimal(18,2).
- 2026-10-08: Because the collation is case-insensitive, keys differing only by case (`ab1` / `AB1`) count as the same key, and trailing spaces are ignored in comparisons.

Keys and tracking

| Column | Type | Null | Default | Notes |
|---|---|---|---|---|
| Id | uniqueidentifier | NOT NULL | newsequentialid() | PRIMARY KEY, clustered (`PK_Transactions`) since 2026-10-14; before that a spare unique column (`UQ_Transactions_Id`, dropped). Filled by the default: the commit insert leaves it out. |
| PayOrderNo | nvarchar(50) | NULL | | Optional since 2026-10-14. Excel: `payorder no`. With TxnRefSeqNo it is the business key of rows that have both (unique, see `UX_Transactions_Key`). |
| TxnRefSeqNo | nvarchar(50) | NULL | | Optional since 2026-10-14. Excel: `Txn Ref/ Seq No`. |
| SlipNo | nvarchar(30) | NULL | | Added 2026-10-14. The 8-digit slip number found in Narration 1 at import (digits only; about 77% of the rows of the real statement file have one). NOT unique: slips are reused. |
| NarrationCnic | varchar(20) | NULL | | Added 2026-10-14. A 13-digit CNIC-like number found in Narrations 2-5 at import, digits only (dashes dropped). A hint, separate from `Cnic`; the Query CNIC filter matches either. |
| ImportBatchId | int | NOT NULL | | FK → ImportBatches.Id. |
| SourceRowNumber | int | NULL | | Excel row number in the source file. |
| RowHash | char(64) | NOT NULL | | SHA-256 hex of the normalized business columns (not SlipNo / NarrationCnic). Rows with a full key: same key + same hash = exact duplicate (skip); same key + different hash = conflict (user decides). Key-less rows: same hash = "Already in database" (flagged, imported unless skipped). Indexed (`IX_Transactions_RowHash`). |
| CreatedAt | datetime2(0) | NOT NULL | sysutcdatetime() | UTC. |
| UpdatedAt | datetime2(0) | NULL | | Set when a conflict is overwritten. |

Business columns (Excel header → column)

| Column | Type | Null | Excel header | Notes |
|---|---|---|---|---|
| ValueDate | date | NOT NULL | Value Date | |
| BranchCode | nvarchar(50) | NULL | Br. Code | Widened from nvarchar(20) on 2026-10-19 (`007_cash_matching.sql`; the question raised on 2026-10-14 is decided). For UBL rows, whose Br. Code column is empty, the import fills it from `/Br: nnnn` in Narration 1. Used with value date and amount to recognise cash rows (`IX_Transactions_Match`). |
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
- `PK_Transactions` — clustered, (Id). (Until 2026-10-14: clustered on (PayOrderNo, TxnRefSeqNo); `UQ_Transactions_Id` is gone.)
- `UX_Transactions_Key` — unique, filtered `WHERE PayOrderNo IS NOT NULL AND TxnRefSeqNo IS NOT NULL`, (PayOrderNo, TxnRefSeqNo). Added 2026-10-14. Needs `SET QUOTED_IDENTIFIER ON` in the session that creates it (sqlcmd defaults to OFF; the scripts set it).
- Added 2026-10-19: `IX_Transactions_Match` (ValueDate, BranchCode) INCLUDE (Credit, Debit, BankName, SlipNo), for the date + amount + branch lookup of cash rows.
- Added 2026-10-14: `IX_Transactions_PayOrderNo` (PayOrderNo; it used to be the PK's leading column), `IX_Transactions_RowHash` (RowHash), `IX_Transactions_SlipNo` (SlipNo, filtered `IS NOT NULL`), `IX_Transactions_NarrationCnic` (NarrationCnic, filtered `IS NOT NULL`).
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
| Status | tinyint | NOT NULL | 0 | 0 Pending, 1 Invalid, 2 New, 3 ExactDuplicate, 4 FileDuplicate, 5 Conflict, 6 LikelyDuplicate (added 2026-10-14: a key-less row with the same slip number, value date, account and amounts as a Transactions row but other values differing; a hint only). Rows with a full key can only be 1-5 and keep their old rules. |
| Overwrite | bit | NOT NULL | 0 | User's choice for Conflict rows. |
| ErrorText | nvarchar(2000) | NULL | | Validation errors for the row, joined. |
| RowHash | char(64) | NULL | | SHA-256 hex; NULL when the row failed validation while parsing. |
| ExistingRowHash | char(64) | NULL | | For Conflict rows: RowHash of the matching `Transactions` row at classification time. Commit compares it with the current hash and aborts if the row changed since the preview. Added 2026-10-09. |
| SlipNo, NarrationCnic | as in Transactions | NULL | | Added 2026-10-14 by `006_surrogate_key.sql`. |
| MatchReason | tinyint | NULL | | Added 2026-10-19 by `007_cash_matching.sql`. Set on Status 6 rows: 1 = same slip no. + date + account + amounts, 2 = same value date + amount + branch code (+ bank), a hint only, 3 = same Txn Ref (+ bank + account) with other values. NULL otherwise. |
| PayOrderNo … Narration5 | as in Transactions | NULL | | Same names, types and sizes as the `Transactions` business columns (ValueDate is date). |

Indexes: `PK_ImportStaging` (clustered, ImportBatchId + ExcelRowNumber); `IX_ImportStaging_Key` (ImportBatchId, PayOrderNo, TxnRefSeqNo); `IX_ImportStaging_Status` (ImportBatchId, Status, ExcelRowNumber).

## Local SQLite database (alternative provider)
Everything above describes SQL Server. The app can also run on a local SQLite file; `Database:Provider` in appsettings chooses
(`"SqlServer"`, the default, or `"Sqlite"`) and a restart switches it. The two databases are separate: nothing is copied between them.
The SQLite schema is `Scripts/db/sqlite/001_create_schema.sql` plus `002_surrogate_key.sql` (version 2, 2026-10-14: the same change as SQL Server's 006; SQLite cannot alter a primary key, so it REBUILDS `Transactions` and `ImportStaging`, keeping every existing row; rollback: `Scripts/db/rollback/002_rollback_sqlite.sql`), embedded in the app and applied at startup by `SqliteSchemaInitializer` (a new file gets 001 then 002; an old file is upgraded; the schema version is `PRAGMA user_version`, and a file with a NEWER version than the app knows is refused). Same three
tables, same column names, same primary keys, same indexes, same `Status` check. The entities and `AppDbContext` are shared; the provider
differences are in `Sqlite/SqliteModelConfiguration.cs` (value converters) and the stores.

Scope: SQLite runs the whole Import pipeline (stage, classify, preview, conflicts, overwrite choices, re-check, commit, discard,
startup cleanup, invalid-row export) and the Query screen (search, paging, sorting, totals, details, export). The search is one shared
class (`TransactionQueryServiceBase`); `SqliteTransactionQueryService` overrides only text matching, text ordering, multi-select lists and timeout detection.

### Type mapping (SQL Server to SQLite)
| SQL Server | SQLite column | Stored as | Notes |
|---|---|---|---|
| nvarchar(n), varchar(n), char(n) | TEXT | text | Lengths are not enforced by SQLite; the parser truncates to the SQL Server lengths, so the data is the same. |
| int, tinyint | INTEGER | integer | `Status` keeps its range with `CHECK (Status BETWEEN 0 AND 6)` (0-5 in schema version 1). |
| bit (`Overwrite`) | INTEGER | 0 / 1 | `CHECK (Overwrite IN (0, 1))`. |
| date (`ValueDate`) | TEXT | `yyyy-MM-dd` | ISO, sorts and compares correctly as text. |
| datetime2(0) (`ImportedAt`, `CommittedAt`, `CreatedAt`, `UpdatedAt`) | TEXT | `yyyy-MM-ddTHH:mm:ss.fffZ` (UTC) | Defaults use `strftime('%Y-%m-%dT%H:%M:%fZ','now')`. An EF converter reads them back as `DateTimeKind.Utc`. |
| uniqueidentifier (`Transactions.Id`) | TEXT | upper-case GUID text | The default builds a random version-4 GUID in SQL. NOT sequential (SQLite has no `NEWSEQUENTIALID()`); `Id` is not the key. |
| int IDENTITY (`ImportBatches.Id`) | INTEGER PRIMARY KEY AUTOINCREMENT | integer | AUTOINCREMENT so an id is never reused, like an identity. |
| decimal(18,2): `Discount`, `DownPaymentAmount`, `Debit`, `Credit`, `RunningBalance` | INTEGER | **minor units (cents)**: 12.50 is stored as 1250 | EF would store a decimal as TEXT, and then range filters, ORDER BY and SUM() compare text. An EF value converter (SQLite only) and the staging loader convert with round-half-away-from-zero (0.005 becomes 0.01, -0.005 becomes -0.01). Values come back with scale 2. Anything written by hand in SQL must use cents (`Debit >= 10000` means 100.00). |
| Default case-insensitive collation | `COLLATE NOCASE` on `PayOrderNo` and `TxnRefSeqNo` in `Transactions` and `ImportStaging` (so also in their indexes) | | `abc` and `ABC` are the same key and a duplicate, as on SQL Server. |
| Filtered unique index | `UX_Transactions_Key ... WHERE PayOrderNo IS NOT NULL AND TxnRefSeqNo IS NOT NULL` is a PARTIAL unique index | | Same meaning as on SQL Server. `SlipNo` / `NarrationCnic` are plain TEXT; `SlipNo` and `NarrationCnic` have partial indexes. |

### SQL Server versus SQLite: where behaviour differs
| Topic | SQL Server | SQLite | Same result for the user? |
|---|---|---|---|
| Who writes | many users, row locks, RCSI for readers | ONE writer per file at a time (WAL mode: readers never wait for it) | Single user: yes. See limitations. |
| Commit exclusivity | `sp_getapplock` + `UPDLOCK` on the batch row | `BEGIN IMMEDIATE` (the write lock) for the whole commit; a second writer waits | Yes: same guards, same failure reasons, same messages. A lock timeout is the same `LockTimeout` result (after `CommitLockTimeoutSeconds`). |
| Discard vs commit | discard's conditional `UPDATE` waits for the commit's row lock | the same `UPDATE` waits for the commit's write lock | Yes (tested: discard always ends consistent). |
| Staging load | `SqlBulkCopy`, 10,000-row batches | one transaction, one prepared `INSERT` per row | Yes. The load holds the write lock for the whole file. |
| Classify / re-check / delete | chunks of 4,000 rows (below lock escalation) | one statement each (no escalation with one writer) | Yes. |
| Key case-insensitivity | whole database collation (all columns, all languages) | `NOCASE` on the two key columns only, ASCII letters only | **No, for non-ASCII letters** (`É` and `é` are different keys on SQLite) and for non-key columns (an equality filter on a name or bank is case-sensitive). |
| Trailing spaces | ignored by `=` | significant | Yes in practice: the parser trims every text value. |
| Money | exact decimal(18,2) | exact integer cents; range about +-9.2e16 | Yes. |
| Text length limits | enforced | not enforced | Yes: the parser already truncates. |
| Timestamps | server clock | this PC's clock | Yes (single machine). |
| `Id` of a transaction | sequential GUID | random v4 GUID | Yes: not used for ordering. |
| Text search (customer name contains / starts with, reference numbers) | `StartsWith` / `Contains` / `=`, case-insensitive through the collation; `StartsWith` can use an index | `LIKE ... ESCAPE` a backslash, with `%`, `_` and the backslash in the typed text escaped (EF's `Contains` is `instr()`, which is CASE-SENSITIVE). Case-insensitive for ASCII letters only. `LIKE` uses an index only on the two NOCASE key columns, so name / reference searches scan (about 0.3 s per 500,000 rows) | Yes for ASCII. Non-ASCII letters match case-sensitively. |
| Sorting a text column | database collation, case-insensitive | `ORDER BY col COLLATE NOCASE` (ASCII), so `alice` sorts next to `Alice` | Yes for ASCII. |
| Multi-select lists (bank, project, dealer, branch, source) | `IN (...)`, case-insensitive | `col COLLATE NOCASE IN (...)` | Yes for ASCII. |
| Dropdown values (distinct) | one value per case-insensitive spelling | `HBL` and `hbl` would be two entries (DISTINCT is case-sensitive), listed in NOCASE order | Only if the data mixes cases of the same bank. |
| Money filters, sort, totals | decimal | SQL on the integer cents (`Debit >= 10000`, `ORDER BY Debit`, `SUM(Debit)`); EF converts to and from decimal | Yes. |
| Query timeout | 60 s command timeout becomes `QueryTimeoutException` | SQLite has no statement time limit; only waiting for another connection's lock (`SQLITE_BUSY`) maps to it | Yes in practice (single user). |
| Connection settings | none needed | per connection: `foreign_keys = ON`, `busy_timeout = 30000`, `synchronous = NORMAL` (an EF interceptor runs them on every open); database-wide: `journal_mode = WAL` (set once by the initializer) | n/a |

### Limitations of the local mode
- Single machine, single user. SQLite coordinates writers with file locks and a shared-memory file (`-shm`); that does not work over a network.
  **Never place the SQLite file on a network share or a synced folder (OneDrive, Dropbox, ...)** - it can corrupt the database. The default
  `%LocalAppData%\ExcelSearch\excelsearch.db` is on a local disk.
- Two copies of the app on the same file work (writers queue), but an import that is staging holds the write lock until the whole file is
  loaded; the other copy's commit/discard waits up to 30 s (`busy_timeout`) and then reports it could not get the lock.
- The file has two companions while the app runs (`excelsearch.db-wal`, `excelsearch.db-shm`); back up the three together, or close the app first.
- No tool in the app moves data between SQL Server and SQLite.

## Change History
- 2026-10-08: Script `001_create_tables.sql` run against the local `ExcelSearch` database; created `dbo.ImportBatches` (10 columns) and `dbo.Transactions` (34 columns) with all keys and indexes above. Verified that the PK is (PayOrderNo, TxnRefSeqNo) and that a duplicate key insert fails with error 2627.
- 2026-10-08: Ran `DBCC CHECKIDENT ('dbo.ImportBatches', RESEED, 0)` while the table was empty, so the first real batch gets Id 1. A rolled-back test insert during verification had consumed identity value 1. No data change.
- 2026-10-09: This file expanded from the empty template to document both tables, relationships, indexes and history. Introspected from the live database; structure matches the script exactly. Both tables currently hold 0 rows.
- 2026-10-09: Ran `002_create_staging.sql`: added `ImportBatches.Status` / `CommittedAt` and created `dbo.ImportStaging` with its keys and indexes. Verified in a rolled-back transaction that a second staging row with the same (ImportBatchId, ExcelRowNumber) fails with error 2627. Re-seeded `ImportBatches` identity to 0 afterwards (the test consumed value 1). Both tables hold 0 rows.
- 2026-10-09: Ran `003_concurrency.sql`: added `ImportBatches.ImportedBy`, `ImportStaging.ExistingRowHash` and `IX_ImportBatches_Status_ImportedAt`. `READ_COMMITTED_SNAPSHOT` deliberately left OFF (the optional line in the script stays commented). No identity reseed. Integration tests run against this same database using unique `TEST_`-prefixed keys and delete only their own rows (batch ids are consumed, so Ids have gaps). A temporary `ExcelSearch_Test` database was created and dropped the same day.
- 2026-10-12: `READ_COMMITTED_SNAPSHOT` is now ON (`Scripts/db/004_enable_rcsi.sql`, run by the owner), so the "left OFF" note above is out of date. Readers no longer wait for a large commit: a `COUNT(*)` during a 200,000-row commit returned in 2-62 ms.
- 2026-10-12: No schema change this day. The Query screen reads `Transactions` only. `Scripts/db/init_db.sql` added: 001-004 in one re-runnable script (creates the database if missing). Not run against the live database; its content equals what 001-004 already created.
- 2026-10-12: `Scripts/db/005_query_indexes.sql` added as a DRAFT of index proposals with measurements (500,000-row run). Every statement is commented out; no index was created. Existing filter indexes were enough for the measured searches once the sort tie-break was changed in code (Docs/Performance.md section 8). If an index is ever added, update 001, `init_db.sql`, the scaffolded model and this file together.
- 2026-10-12: Test data: the 500,000-row and 200,000-row tests insert `TEST_<guid>` rows and delete them again (chunked, by prefix) in the same run; batch Ids are consumed, so `ImportBatches.Id` has gaps. Manual imports of the sample workbooks (keys like `PO-A-0001`, `PO-Q-0001`) are real rows and stay until deleted by hand.
- 2026-10-13: Local SQLite provider added (Import pipeline only). New script `Scripts/db/sqlite/001_create_schema.sql` (`PRAGMA user_version = 1`), embedded in the app. NO change to the SQL Server schema, scripts or the scaffolded model. See "Local SQLite database" above for the type mapping and differences. `WITHOUT ROWID` was measured for `Transactions` and `ImportStaging` and not used (Docs/Performance.md section 9).
- 2026-10-13: Query screen enabled on the Local DB (SQLite). NO schema change and no new index: the 500,000-row run (Docs/Performance.md section 10) had no search over about 1.1 s. The name filter gained a Contains / Starts-with mode (`TransactionFilter.CustomerNameMode`) on both databases; Starts-with can use `IX_Transactions_CustomerName` on SQL Server.
- 2026-10-14: **Surrogate primary key (key-less import).** Ran `Scripts/db/006_surrogate_key.sql` on the local `ExcelSearch` database (3,040 rows at the time; all kept their keys and values): `Transactions.Id` became the clustered PRIMARY KEY, `PayOrderNo` / `TxnRefSeqNo` became NULLable with a filtered unique index for rows that have both, `SlipNo` / `NarrationCnic` added to `Transactions` and `ImportStaging`, new indexes added (see Indexes). `ImportStaging.Status` gained code 6. Re-run on a copy and on a fresh database built from `init_db.sql` (which now includes 006) with the same result; `Scripts/db/rollback/006_rollback.sql` tested too. SQLite: new `002_surrogate_key.sql` (user_version 2), tested on a version-1 file with data. EF model re-scaffolded. Why and how: `Docs/KeylessImport.md`.
- 2026-10-14: **Open question, not decided: widen `BranchCode`?** `BranchCode` is `nvarchar(20)` (SQLite: not enforced, but the parser applies the same limit). In the real statement file, 24 of 50,921 rows have a Br. Code of 21-35 characters and are therefore Invalid (the other 50,897 import). No schema change was made, so those rows stay rejected; they can be exported from the Invalid tab and fixed in Excel. If this comes up again, the choices are: (a) leave as is, (b) widen to e.g. `nvarchar(50)` (needs a new numbered SQL Server script, a new numbered SQLite script, a rollback note, `init_db.sql`, the re-scaffolded model, the parser limit in `ExcelParser`/`ImportRow`, the `ImportStaging` column, and this file), or (c) truncate instead of reject (loses text; not recommended). Context: `Docs/KeylessImport.md` > Known gaps.
- 2026-10-19: **Cash-row recognition (`Scripts/db/007_cash_matching.sql`, SQLite `003_cash_matching.sql` = user_version 3).** New real sample with two bank statements (UBL 6,623 rows with a Txn Ref on every row and a Pay order on about half; HBL 4,874 cash rows with neither). Changes: `BranchCode` widened to nvarchar(50) on `Transactions` and `ImportStaging` (this settles the 2026-10-14 question; SQLite needed no change), `ImportStaging.MatchReason` added, `IX_Transactions_Match` added. Classification (`StatusSql` / `SqliteStatusSql`) now has three tiers: full key (unchanged); Txn Ref only (same bank + account + Txn Ref: same hash = Already in database, other values = Possible duplicate); no reference (same hash; then same slip no.; then same date + amount + branch code, unless both rows have different slip numbers). Possible duplicate rows are still imported unless 'Skip flagged duplicates' is ticked. Applied to the dev database, tested with a rollback (`Scripts/db/rollback/007_rollback.sql`, `003_rollback_sqlite.sql`); `init_db.sql` includes it; EF model re-scaffolded. Details: `Docs/KeylessImport.md` > Cash rows.
- 2026-10-19: **Runs on SQL Server 2012.** The production server is SQL Server 2012 Express (11.0.2100), database `AuditExcel`. `init_db.sql` and `001`/`002` no longer use inline `INDEX` clauses inside `CREATE TABLE` (SQL Server 2014+); the indexes are separate `CREATE NONCLUSTERED INDEX` statements with the same names and columns. The app avoids `STRING_AGG` (2017+) and `OPENJSON` (2016+): EF Core uses `UseCompatibilityLevel(110)`. Schema built on that server with `init_db.sql` (empty, pre-production); the 79 Integration tests pass against it.
