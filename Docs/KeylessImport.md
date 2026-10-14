# Key-less import: change record

This document records one feature: importing bank-statement sheets that have **no PayOrderNo / TxnRefSeqNo**. It says how the app worked before, what changed and why, and how to go back. Written before the code was changed; the final section is filled in at the end.

## Baseline for reverting

| | |
|---|---|
| Commit before the change | `7f8379d7a7fe4b6c4b398ed600689f70012657d6` ("Docs: Add development quickstart for fresh clones") |
| Branch | `main`, equal to `origin/main`, clean tree at that point |
| Commits that make up the change | _not committed yet_ (commits are made only when asked; once they exist, list the first and last hash here) |

Revert the code: `git revert --no-edit <first>^..<last>` (history kept), or on a throwaway branch `git checkout 7f8379d -- .`.
A code revert does **not** undo the database change. See "Reverting the database" below.

## How the app worked before (at `7f8379d`)

- **Row identity:** the business key `(PayOrderNo, TxnRefSeqNo)` was the primary key of `dbo.Transactions` (SQLite: the same pair, `COLLATE NOCASE`). `Transactions.Id` (GUID) existed as a spare unique column only. Both key fields were `NOT NULL`.
- **Required fields:** PayOrderNo, TxnRefSeqNo and ValueDate. A sheet lacking any of those three headers was rejected as a file. A row lacking any of the three values was Invalid.
- **Row statuses** (stored in `ImportStaging.Status`, numbers never change): 0 Pending, 1 Invalid, 2 New (key not in `Transactions`), 3 ExactDuplicate (same key, same `RowHash`, skipped), 4 FileDuplicate (identical repeat earlier in the same file, skipped), 5 Conflict (same key, different hash; the user ticks Overwrite per row).
- **Same key twice in one file** with different values made every such row Invalid.
- **Commit:** inserts Status 2, updates Status 5 rows with Overwrite ticked, skips everything else. It re-checks under the `ExcelSearch.Commit` application lock (SQL Server) or `BEGIN IMMEDIATE` (SQLite). The primary key was the last backstop (error 2627). The code never caught it.
- **Counters** on `ImportBatches`: NewRows, SkippedRows (exact + in-file duplicates + skipped conflicts), UpdatedRows, RejectedRows.
- **Query:** the sort tie-break and the export keyset were `(PayOrderNo, TxnRefSeqNo)`. The detail panel was looked up by that key (`TransactionKey`). Filters existed for both fields. The CNIC filter searched `Cnic` only. Export offered 28 fields. No filter or column for slip numbers.
- **Import preview:** five tabs (New, Conflicts, Invalid, Already in database, Repeated in file). Commit was never blocked by duplicates or conflicts.
- **Schema versions:** SQL Server scripts `001`-`004` joined in `init_db.sql` (`005` is a draft); SQLite `PRAGMA user_version = 1` (`001_create_schema.sql`).

## Why it changes

The real file supplied for testing (50,921 rows) has all 28 headers, but PayOrderNo is on 5 rows and TxnRefSeqNo on 1. Almost all of its transactions are cash deposits that have no such references, so under the old rules about 99.99% of it was Invalid. The data that identifies a deposit is in the narrations: a slip number in Narration 1 (about 77% of rows) and often a 13-digit CNIC in Narrations 2-5 (about 34%). Slip numbers repeat (445 are used more than once), so they cannot be a key.

## What changed

All of it, as built (the approved plan is in the appendix; where the build differs, it is noted).

**Database** (`Scripts/db/`)
- `006_surrogate_key.sql` (SQL Server) and `sqlite/002_surrogate_key.sql` (SQLite, schema version 2): `Transactions.Id` is the clustered primary key; `PayOrderNo` / `TxnRefSeqNo` are nullable; `UX_Transactions_Key` (filtered unique) keeps rows that have both unique; `SlipNo` and `NarrationCnic` added to `Transactions` and `ImportStaging`; indexes `IX_Transactions_PayOrderNo`, `_RowHash`, `_SlipNo`, `_NarrationCnic`; `ImportStaging.Status` allows 6. `init_db.sql` includes 006. SQLite rebuilds the two tables (it cannot alter a key) and keeps every row.
- The EF model was re-scaffolded from the upgraded database (generated, not hand-edited).
- Rollback scripts: `Scripts/db/rollback/006_rollback.sql`, `002_rollback_sqlite.sql` (see below).

**Import** (`ExcelSearch.Core`, `ExcelSearch.Data`)
- `Required` headers = Value Date only; a row needs a Value Date and nothing else. No amount rule was added (the real file could not justify one: every row has a Credit, none a Debit).
- `NarrationExtractor`: `SlipNo` = the first 8-digit run in Narration 1; `NarrationCnic` = the first 13-digit run (plain or `12345-1234567-1`) in Narrations 2-5. Not part of `RowHash`.
- New status `LikelyDuplicate = 6`. Classification of rows WITHOUT a full key: identical repeat inside the file = FileDuplicate (first copy stays New); same `RowHash` in the database = ExactDuplicate; same slip no + date + account + debit + credit = LikelyDuplicate; otherwise New. Rows WITH a full key are classified exactly as before (Conflict / Overwrite included). The rule is one SQL fragment per provider (`SqlServer/StatusSql.cs`, `Sqlite/SqliteStatusSql.cs`) shared by classify and re-check.
- `CommitAsync(batchId, skipFlaggedDuplicates = false)`: inserts New rows and, unless asked to skip, the flagged key-less rows (statuses 3, 4, 6 on a row without a full key). A keyed duplicate is never inserted. `CommitResult` gained `ImportedFlaggedDuplicates` and `SkippedLikelyDuplicates`; `ImportBatches.NewRows` counts every row written, `SkippedRows` every valid row left out (no counter schema change).
- Commit's "someone else committed this key meanwhile" checks apply to keyed rows only; two users committing the same key-less file at the same moment simply both succeed (duplicates are allowed).

**Query**
- `TransactionKey` is the `Id`; the sort tie-break, the export keyset and the detail lookup use `Id`. `TransactionRecord` has `Id`, nullable `PayOrderNo` / `TxnRefSeqNo`, `SlipNo`, `NarrationCnic`.
- New "Slip no" filter; the CNIC filter matches `Cnic` OR `NarrationCnic`; `SlipNo` is sortable. Export has 30 fields.
- Default columns: Value date, Slip no, Customer, CNIC, Bank, Account no, Project, Debit, Credit, Narration 1, Narration 2. Pay order no, Txn ref, Application no, Dealer and Down payment are hidden until ticked in the Columns picker (a change from before, because most statement rows have none of them).

**Import screen**
- Sixth tab "Possible duplicates". Cards say "flagged" instead of "skipped". A "Skip flagged duplicates (N)" checkbox appears when N > 0 key-less flagged rows exist (default off). The button reads "Import 103 new rows (+46 flagged duplicates)". The Done panel adds "Flagged duplicates imported" and "Possible duplicates (skipped)". Grid columns on all tabs but Conflicts: Row, Value date, Slip no, Customer, Debit, Credit, Bank, Narration 1.

**Tests and samples**
- Rewritten for the new rule: parser required fields, sample counts, column lists, 28 to 30 fields, the sort/export tests (compare by Id order, not key order), the SQLite index count. Added: `KeylessContract` (3 tests, both providers), key-less query test, SQLite v1 to v2 upgrade test, `NarrationExtractorTests`, one Import ViewModel test for the checkbox.
- New synthetic samples `11_keyless_statement.xlsx`, `12_keyless_reimport_of_11.xlsx`; sample 05 now lacks the Value Date header; sample 02 counts changed (README updated). The real file stays out of git (`.gitignore`).

**Measured on the real file** (50,921 rows, this PC): both providers parse it; 50,897 rows import (98 of them flagged repeats), 24 are Invalid (all: Br. Code longer than the 20-character column). Slip no found on 38,662 imported rows (76%), narration CNIC on 24,029 (47%, more than the first estimate because dashed numbers now count). Importing the same file again flags all 50,799 non-repeat rows as Already in database. Stage then commit: about 10 s + 4 s on SQL Server, 5 s + 3 s on SQLite (first run, cold).

## Reverting the database

A git revert changes the code, not the databases. Both databases that were upgraded need their own step, and the old app will not run on the new schema.

**Before you upgrade** (any database that matters): back it up. SQL Server: a normal backup. SQLite: close the app and copy `excelsearch.db` together with `-wal` / `-shm` if present. An older ExcelSearch refuses a schema-version-2 SQLite file, so the copy is also the easy way back.

**SQL Server:** `Scripts/db/rollback/006_rollback.sql` (select the database first). It restores the primary key on `(PayOrderNo, TxnRefSeqNo)`, makes both NOT NULL again and drops `SlipNo`, `NarrationCnic` and the indexes 006 added. It REFUSES to run, changing nothing, while any row has no PayOrderNo or no TxnRefSeqNo, because the old key cannot hold them; the message says so and the script header shows the DELETE that removes them (those rows are lost, there is no other place for them in the old model). Batches still Staged are marked Discarded. Tested on a scratch database: refusal with a key-less row, clean rollback after deleting it, and the upgrade can be run again afterwards.

**SQLite:** `Scripts/db/rollback/002_rollback_sqlite.sql`, run with `sqlite3 -bail excelsearch.db ".read 002_rollback_sqlite.sql"` (the app closed, a copy taken). `-bail` matters: the copy step fails, before anything is dropped, if a key-less row exists, and without `-bail` the next statement would drop the table. Same rules as above. Tested with Python's `sqlite3`: refusal, clean rollback back to version 1 with the rows identical, upgrade again.

**The code:** `git revert --no-edit <first>^..<last>` (the hashes of this change, listed at the top once committed) or, on a throwaway branch, `git checkout 7f8379d -- .`.

Order if you go back: roll the databases back first (they must have no key-less rows), then put the old code back.

## Known gaps

- **24 rows of the real file are rejected** because `Br. Code` is longer than 20 characters (values up to 35 characters: it holds more than a code in those rows). The existing rule is unchanged; the rows can be exported from the Invalid tab and fixed, or `BranchCode` can be widened (a schema change, not made here).
- A re-imported file doubles its rows unless "Skip flagged duplicates" is ticked. That is the cost of "import by default". The whole-file hash warning in `ImportBatches.FileHash` is not shown on screen.
- Genuine identical cash deposits (same day, account, amount, slip and narrations) cannot be told from a re-import: they are flagged, never silently dropped.
- The slip number and CNIC rules are tuned on one real file and one narration style (HBL statements); other banks may need other rules. They are one small class (`NarrationExtractor`).
- Rows imported before this change have no `SlipNo` / `NarrationCnic` until re-imported (no backfill).
- Not verified: a production SQL Server (006 was run on the dev server and on scratch copies only); a case-sensitive database; a Local DB file with millions of rows through the 002 rebuild (tested with a few rows; the copy is one `INSERT ... SELECT`).
- The two statement samples and the real-file figures above are single-machine, first-run timings.

---

## Appendix: the approved plan

### Plan: import bank-statement sheets that have no business key

## Context
The real file from your friend (`Samples/software filed (1).xlsx`, 50,921 rows) has all 28 headers but almost no values in the key columns: PayOrderNo is on 5 rows and TxnRefSeqNo on 1. Today those rows are all Invalid, because `(PayOrderNo, TxnRefSeqNo)` is required, is the primary key of `Transactions`, and drives New / Already-in-database / Conflict.

What the file does have: Value Date, Credit, Bank, Account, Project and five narrations. Measured on it:
- Narration 1 holds a slip number, an 8-digit run (optional letter prefix), on about 77% of rows.
- A 13-digit CNIC-like number appears in Narrations 2-5 on about 34% of rows.
- 90 groups (188 rows) are identical on every column.
- Slip numbers are reused (445 slips appear more than once), so they cannot be a key.

Decisions you made:
- Duplicates are allowed. They are flagged on the preview and imported by default, with a "Skip flagged duplicates" choice. Nothing blocks the commit.
- Conflict / Overwrite stays, but only for rows that have both PayOrderNo and TxnRefSeqNo.
- Slip no. and narration CNIC are extracted into searchable columns.
- Both existing databases get an upgrade script.

**Safety item, do first:** `Samples/software filed (1).xlsx` is real data (names, CNICs) and is untracked and not gitignored. Add it to `.gitignore` before any commit. All new samples and tests use synthetic data shaped like it.

## Design

### 1. Identity model
- `Transactions.Id` (GUID, `NEWSEQUENTIALID()`, already exists) becomes the primary key, clustered.
- `PayOrderNo` and `TxnRefSeqNo` become nullable.
- A filtered unique index `UX_Transactions_Key (PayOrderNo, TxnRefSeqNo) WHERE both are NOT NULL` keeps keyed rows unique, so the existing keyed conflict and overwrite logic and the 2627 backstop stay valid. Key-less rows are unrestricted.
- Rows with only one of the two key fields count as key-less (the value is kept as data).
- New indexes: `IX_Transactions_RowHash`, and `IX_Transactions_SlipNo` filtered to rows where SlipNo is not null.
- New nullable columns on `Transactions` and `ImportStaging`:
  - `SlipNo`: `nvarchar(30)`
  - `NarrationCnic`: `varchar(20)`, digits only
- Both columns are derived at import and are not part of `RowHash`.

### 2. Validation (Core)
- `HeaderAliases.Required` becomes ValueDate only. No amount rule is added: that check never existed, and the real file cannot justify one. Every row has a Credit (17 of them are 0) and none has a Debit, so it says nothing about statements with Debit-only rows. The file's only universally filled field is Value Date.
- Row errors: "PayOrderNo is required" and "TxnRefSeqNo is required" are removed. ValueDate stays required. Everything else is unchanged.
- Consequence to accept: a sheet with a "Value Date" header is now accepted as an import file, because no other required header remains.
- New `Core/Import/NarrationExtractor.cs` (pure functions, one rule table):
  - SlipNo: the 8-digit run in Narration 1.
  - NarrationCnic: a 13-digit run (not part of a longer number) in Narrations 2-5.
  - You named Narrations 1-3, but CNICs also appear in 4 and 5 in the real file, so I search 2-5.
  - Called from `ExcelParser.BuildRow`, and `ImportRow` gets the two properties.
- The rules are tuned against the real file and the coverage is reported, but only synthetic examples go in tests.

### 3. Classification and commit (Data)
Keyed rows keep today's algorithm unchanged. Key-less rows get a second path in `ClassifyAsync` and `RefreshAgainstDatabaseAsync`, in both stores:
1. Repeats inside the file (partitioned by `RowHash`): the first stays New and later copies become FileDuplicate. Nothing is made Invalid.
2. Against `Transactions`:
   - Same `RowHash`: ExactDuplicate.
   - Otherwise, same `(ValueDate, AccountNo, SlipNo, Debit, Credit)` with SlipNo not null: new status `LikelyDuplicate = 6` ("Possible duplicate").
   - Otherwise: New.
3. `RowStatus` gets `LikelyDuplicate = 6`. Existing numbers do not change. The SQLite CHECK (0..5) is widened in the rebuild.
4. `CommitAsync` gets a `skipFlaggedDuplicates` argument (default false):
   - It inserts Status 2.
   - It also inserts key-less rows with Status 3, 4 or 6 unless the argument is true.
   - Keyed ExactDuplicate / FileDuplicate rows are always skipped, as today.
   - The "New row now exists with different values" re-check applies to keyed rows only.
   - The insert and update SQL gain the two new columns.
5. `CommitResult` gets `ImportedFlaggedDuplicates`. `ImportBatches.NewRows` counts everything inserted and `SkippedRows` everything not inserted, so there is no counter schema change.
6. `GetConflictPageAsync` is unchanged, since only keyed rows can be Conflict.

### 4. Query (Core + Data + WPF)
- `TransactionKey` becomes the `Id` (Guid), and `TransactionRecord` gets `Id`. `GetDetailAsync` and the export keyset (`StreamChunksAsync`) use `Id`. The sort tie-break becomes `Id`. This also handles key-less rows, where the old key sort would tie.
- `TransactionRecord` / `TransactionDetail`: `PayOrderNo` and `TxnRefSeqNo` are nullable. The grid and Details show empty cells.
- New text filter "Slip no". The CNIC filter matches `Cnic` or `NarrationCnic`.
- New columns Slip no and Narration CNIC, which are selectable and exportable. `TransactionExportWriter` goes from 28 to 30 fields (update the "all 28 fields" wording).
- Default visible columns change so a statement-style file is useful: value date, bank, account, credit / debit, slip no, narrations 1-2. Pay order / txn ref become selectable but hidden. Say so if you want them to stay visible.

### 5. Import screen (WPF)
- A sixth tab, "Possible duplicates", and an unchanged five others. The hint texts say:
  - Already in database / Repeated in file / Possible duplicates: "flagged; imported unless you tick Skip flagged duplicates" (for key-less rows).
  - Keyed duplicates still say "skipped".
- A "Skip flagged duplicates" checkbox on the summary bar, default off.
- Grid columns on all tabs: Row, Value date, Slip no, Customer, Credit/Debit, Bank, Narration 1. The Pay order / Txn ref columns show only on the Conflict tab.
- `CommitButtonText` reads "Import N new rows (+M flagged duplicates)".
- `CanCommit` counts the importable flagged rows. The confirmation text and the Done panel get the flagged line.
- Summary card for Possible duplicates; the other card texts are reworded.
- `ImportViewModel` passes the checkbox to `CommitAsync`.

### 6. Database upgrade scripts and model
- SQL Server `Scripts/db/006_surrogate_key.sql`, re-runnable and in one transaction:
  1. Drop `PK_Transactions` and `UQ_Transactions_Id`.
  2. Make the key columns NULL.
  3. Add `PK_Transactions (Id)` clustered, then the filtered unique index, the new columns and the new indexes.
  - Append it to `Scripts/db/init_db.sql` after 004. Add a note in 001's header that 006 supersedes its key design.
- SQLite `Scripts/db/sqlite/002_surrogate_key.sql` (a new numbered script, never edit 001), ending with `PRAGMA user_version = 2`. SQLite cannot alter a primary key, so it rebuilds:
  1. Mark Staged batches Discarded and clear `ImportStaging`.
  2. Rebuild `Transactions` as `_new`, copy the rows, drop, rename and recreate the indexes.
  3. Rebuild `ImportStaging` with the new columns and CHECK 0..6.
  - There is no existing rebuild example in the repo, so this one is tested on a copy of an old database file.
- Existing rows get NULL `SlipNo` / `NarrationCnic` (no backfill; the dev SQL Server was wiped).
- Re-scaffold the EF model with the command in AGENTS.md, and check `AppDbContext.Partial.cs` and `SqliteModelConfiguration` still apply. The filtered index is declared in the model only if the scaffold produces it, otherwise via the partial hook.
- `Scripts/db/005_query_indexes.sql` (draft): update its key-based index notes.

### 7. Tests (policy: minimal, behaviour-focused)
Rewrite, not add, where a test encodes the old rule:
- `ParserTests`:
  - The missing-required-fields test now expects only ValueDate.
  - The missing-required-header test now uses a sheet without a Value Date header.
  - Add one Theory for `NarrationExtractor`.
- Contract tests (run on both providers):
  - Keep the keyed classification, conflict, commit and re-check tests as they are (adjust the `Row` helper to nullable keys).
  - Add: key-less classification (New / ExactDuplicate / FileDuplicate / LikelyDuplicate).
  - Add: commit imports flagged rows by default and skips them with the flag.
  - Add: a mixed keyed + key-less file.
  - Add: query detail and export keyset by `Id` with key-less rows.
- `SqliteSpecificTests`: add one upgrade test (old v1 file to v2 keeps rows and keys).
- ViewModel tests: update the existing commit-text, tab-count and enablement tests for the sixth tab and the checkbox. No other new UI tests.
- `SampleFilesTests` / `make_samples.py`: add a synthetic `11_keyless_statement.xlsx` with slip numbers, repeats and a few keyed rows, plus its README checklist entry.

### 8. Docs
`db.md` (new version, dated change entry, key and Status 6 text), `AGENTS.md` (database design rules, status table, Commands note), `README.md`, `Samples/test/README.md`, `Docs/Deployment.md` (run 006 on an existing server database), `Docs/Performance.md` (re-measure the 500k Query and the 200k import).

### 9. New change document: `Docs/KeylessImport.md`
(The folder on disk is `Docs/`. At the time of writing git still recorded the existing files as `docs/...`; that was fixed afterwards by renaming the folder in git to `Docs/`.)
A record of this one feature, written FIRST (before any code) and finished at the end:
- **Baseline for reverting:** commit `7f8379d7a7fe4b6c4b398ed600689f70012657d6` ("Docs: Add development quickstart for fresh clones", origin/main, clean tree). At the end I add the hashes of the commits that make up the change, so `git revert <first>^..<last>` or `git checkout 7f8379d -- .` is spelled out.
- **How the app worked before:** the business key as primary key; required PayOrderNo / TxnRefSeqNo; the five statuses and what each meant; Conflict / Overwrite; the 2627 backstop; the (PayOrderNo, TxnRefSeqNo) sort tie-break and export keyset; the Query filters; 28 export fields; SQLite schema version 1 and the SQL Server scripts 001-004.
- **What changed and why:** the real-file findings (the numbers in Context), the four decisions you made, and the major design choices (surrogate Id primary key, filtered unique index for keyed rows, key-less classification path, status 6, `skipFlaggedDuplicates`, extracted SlipNo / NarrationCnic, header rule = ValueDate only, the CNIC search in Narrations 2-5).
- **This plan**, inserted as written (updated if it changes during the work).
- **Reverting the database, not only the code:** the schema changes are not undone by a git revert. So I also write and test two rollback scripts in `Scripts/db/rollback/` (not embedded, not run by the app):
  - `006_rollback.sql` (SQL Server): drop the new columns and indexes, restore the old primary key. It refuses to run, with a message, while key-less rows exist (they would violate the old key), and says how to delete them first.
  - `002_rollback.sql` (SQLite): the same, rebuilding to schema version 1. An older app refuses a version-2 file, so the document also says to copy the `.db` file (with `-wal`/`-shm`) before upgrading.
- A "known gaps" list at the end, as the final report would give.

## Order of work
0. Write `Docs/KeylessImport.md` (baseline hash, old behaviour, this plan).
1. `.gitignore` for the real file.
2. Scripts 006 / SQLite 002, then re-scaffold.
3. Core: `NarrationExtractor`, validation, `RowStatus`, `ImportRow`.
4. Data: the two stores' classify / refresh / commit, and the query services (`Id` key).
5. WPF: Import and Query changes.
6. Tests, sample file, docs, rollback scripts.
7. Verify (including a rollback dry run on a copy) and report.
8. Finish `Docs/KeylessImport.md` with the final commit hashes. Commits happen only when you ask.

## Verification
- Unit tier, then Integration with `EXCELSEARCH_TEST_CONNECTION` (the dev database), then the Large tier on demand.
- Run script 006 on a copy of the dev database that already holds rows. Run SQLite 002 on a v1 file with data and confirm the keys and rows survive.
- Import the real file into Local DB and into the dev SQL Server:
  - It parses (about 50.9k rows, none Invalid for missing keys).
  - It reports slip-number coverage near 77% and CNIC coverage near 34%.
  - It reports the flagged duplicate counts.
- Re-import the same file: all key-less rows are flagged ExactDuplicate (about 50.9k). Skip flagged duplicates imports 0. The second commit without the flag doubles the rows, as requested.
- Query: filter by slip no and by CNIC (Cnic or narration), sort and page with null keys, export a result, open Details for a key-less row.
- Time the 50.9k import, the 500k Query and a 200k import against `Docs/Performance.md`.
- A manual UIA pass on the Import and Query screens.

## Risks to know
- A re-imported file doubles its rows unless "Skip flagged duplicates" is ticked. That is the cost of "import by default". The whole-file hash warning remains as the only blocker for that case.
- Genuine same-day, same-amount, same-slip repeats cannot be told apart from true duplicates. They are flagged, never silently dropped.
- Slip numbers are shared, so the Possible duplicate flag is a hint, not proof.
- Old rows have no SlipNo / NarrationCnic until re-imported.
