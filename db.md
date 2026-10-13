# ExcelSearch Database Schema
Version: 1.33 | Last Updated: 2026-10-13
Database: ExcelSearch
Server: LOCAL

## MAINTENANCE RULES:
    1. Bump Version and Last Updated above whenever this file changes
    2. Every table's dated inline notes explain WHY a column/table exists or changed — keep new notes dated (YYYY-MM-DD) and specific, don't just describe the column type
    3. A table marked [REMOVED] stays in this file as a historical/legacy record — do not delete those sections, they document what old code may still (incorrectly) reference
    4. Cross-reference api.txt/api.md (if it exists) wherever a table is directly exposed/managed via a REST endpoint
---

Source of truth for the structure: `Scripts/db/001_create_tables.sql`, then `002`, `003` and `004` (each re-runnable). `Scripts/db/init_db.sql` joins 001-004 into one re-runnable script that also creates the database if it is missing; use it to build a fresh database. `005_query_indexes.sql` is a draft and is not part of the schema. EF Core entities in `src/ExcelSearch.Data` are scaffolded from this schema. Collation: `SQL_Latin1_General_CP1_CI_AS` (case-insensitive).

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
- 2026-10-08: `Id` (GUID) is a spare surrogate: unique, NOT the PK (filled by the column default, see the column table). If real data turns out to have gaps or non-unique key pairs, promote Id to PK and demote the pair to a normal index.
- 2026-10-08: Identifiers (account no, CNIC, refs…) are text because Excel mixes numbers and strings, values can exceed INT range, and leading zeros must survive. Amounts are decimal(18,2).
- 2026-10-08: Because the collation is case-insensitive, keys differing only by case (`ab1` / `AB1`) count as the same key, and trailing spaces are ignored in comparisons.

Keys and tracking

| Column | Type | Null | Default | Notes |
|---|---|---|---|---|
| PayOrderNo | nvarchar(50) | NOT NULL | | PK part 1. Excel: `payorder no`. |
| TxnRefSeqNo | nvarchar(50) | NOT NULL | | PK part 2. Excel: `Txn Ref/ Seq No`. |
| Id | uniqueidentifier | NOT NULL | newsequentialid() | Unique (`UQ_Transactions_Id`). Filled by the default: the commit insert leaves it out (2026-10-12: corrected; an earlier note said the app supplies it). |
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

## Local SQLite database (alternative provider)
Everything above describes SQL Server. The app can also run on a local SQLite file; `Database:Provider` in appsettings chooses
(`"SqlServer"`, the default, or `"Sqlite"`) and a restart switches it. The two databases are separate: nothing is copied between them.
The SQLite schema is `Scripts/db/sqlite/001_create_schema.sql` (embedded in the app and applied at startup by `SqliteSchemaInitializer` when
the file is empty; the schema version is `PRAGMA user_version`, and a file with a NEWER version than the app knows is refused). Same three
tables, same column names, same primary keys, same indexes, same `Status` check. The entities and `AppDbContext` are shared; the provider
differences are in `Sqlite/SqliteModelConfiguration.cs` (value converters) and the stores.

Scope: SQLite runs the whole Import pipeline (stage, classify, preview, conflicts, overwrite choices, re-check, commit, discard,
startup cleanup, invalid-row export) and the Query screen (search, paging, sorting, totals, details, export). The search is one shared
class (`TransactionQueryServiceBase`); `SqliteTransactionQueryService` overrides only text matching, text ordering, multi-select lists and timeout detection.

### Type mapping (SQL Server to SQLite)
| SQL Server | SQLite column | Stored as | Notes |
|---|---|---|---|
| nvarchar(n), varchar(n), char(n) | TEXT | text | Lengths are not enforced by SQLite; the parser truncates to the SQL Server lengths, so the data is the same. |
| int, tinyint | INTEGER | integer | `Status` keeps its range with `CHECK (Status BETWEEN 0 AND 5)`. |
| bit (`Overwrite`) | INTEGER | 0 / 1 | `CHECK (Overwrite IN (0, 1))`. |
| date (`ValueDate`) | TEXT | `yyyy-MM-dd` | ISO, sorts and compares correctly as text. |
| datetime2(0) (`ImportedAt`, `CommittedAt`, `CreatedAt`, `UpdatedAt`) | TEXT | `yyyy-MM-ddTHH:mm:ss.fffZ` (UTC) | Defaults use `strftime('%Y-%m-%dT%H:%M:%fZ','now')`. An EF converter reads them back as `DateTimeKind.Utc`. |
| uniqueidentifier (`Transactions.Id`) | TEXT | upper-case GUID text | The default builds a random version-4 GUID in SQL. NOT sequential (SQLite has no `NEWSEQUENTIALID()`); `Id` is not the key. |
| int IDENTITY (`ImportBatches.Id`) | INTEGER PRIMARY KEY AUTOINCREMENT | integer | AUTOINCREMENT so an id is never reused, like an identity. |
| decimal(18,2): `Discount`, `DownPaymentAmount`, `Debit`, `Credit`, `RunningBalance` | INTEGER | **minor units (cents)**: 12.50 is stored as 1250 | EF would store a decimal as TEXT, and then range filters, ORDER BY and SUM() compare text. An EF value converter (SQLite only) and the staging loader convert with round-half-away-from-zero (0.005 becomes 0.01, -0.005 becomes -0.01). Values come back with scale 2. Anything written by hand in SQL must use cents (`Debit >= 10000` means 100.00). |
| Default case-insensitive collation | `COLLATE NOCASE` on `PayOrderNo` and `TxnRefSeqNo` in `Transactions` and `ImportStaging` (so also in their indexes) | | `abc` and `ABC` are the same key and a duplicate, as on SQL Server. |

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
- 2026-10-12: `Scripts/db/005_query_indexes.sql` added as a DRAFT of index proposals with measurements (500,000-row run). Every statement is commented out; no index was created. Existing filter indexes were enough for the measured searches once the sort tie-break was changed in code (docs/Performance.md section 8). If an index is ever added, update 001, `init_db.sql`, the scaffolded model and this file together.
- 2026-10-12: Test data: the 500,000-row and 200,000-row tests insert `TEST_<guid>` rows and delete them again (chunked, by prefix) in the same run; batch Ids are consumed, so `ImportBatches.Id` has gaps. Manual imports of the sample workbooks (keys like `PO-A-0001`, `PO-Q-0001`) are real rows and stay until deleted by hand.
- 2026-10-13: Local SQLite provider added (Import pipeline only). New script `Scripts/db/sqlite/001_create_schema.sql` (`PRAGMA user_version = 1`), embedded in the app. NO change to the SQL Server schema, scripts or the scaffolded model. See "Local SQLite database" above for the type mapping and differences. `WITHOUT ROWID` was measured for `Transactions` and `ImportStaging` and not used (docs/Performance.md section 9).
- 2026-10-13: Query screen enabled on the Local DB (SQLite). NO schema change and no new index: the 500,000-row run (docs/Performance.md section 10) had no search over about 1.1 s. The name filter gained a Contains / Starts-with mode (`TransactionFilter.CustomerNameMode`) on both databases; Starts-with can use `IX_Transactions_CustomerName` on SQL Server.
