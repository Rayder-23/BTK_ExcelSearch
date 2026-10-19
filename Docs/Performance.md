---
title: ExcelSearch Performance Report
version: 1.7
last_updated: 2026-10-19
status: current
scope: Excel import pipeline (parse, stage, classify, commit) on SQL Server and on local SQLite, invalid-row export, reader latency, Query screen searches (SQL Server), re-check and conflict paging
benchmark_file: synthetic workbook, 200,000 rows, TEST_ keys only
environment: SQL Server Express (.\SQLEXPRESS), single development database, .NET 8, developer PC
---

# ExcelSearch Performance Report

How fast the import pipeline is, how it got there, and what is still slow.
The yardstick is one synthetic 200,000-row `.xlsx` file, produced by the large integration tests
(`EXCELSEARCH_RUN_LARGE=1`). All numbers are from the developer PC against the development database, so treat
them as relative, not absolute. Single runs vary by roughly 10-25% from run to run.

## 1. Summary

| Stage (200,000 rows) | Before | Now | Change |
|---|---|---|---|
| Load into staging (parse + insert) | 283.8 s (705 rows/s) | 12.7-21.3 s (about 9,400-15,700 rows/s) | about 13-22x faster |
| Of which database insert only | about 269.6 s | about 3.7 s | about 73x faster |
| Classify in SQL | 6.7 s | 5.8-9.3 s | unchanged (noise) |
| Stage total (load + classify) | 290.4 s | 22.5 s (measured in the same run) | about 12.9x faster |
| Commit into `Transactions` | not built | 17-27 s | n/a |
| Working-set growth while staging | +125 MB | +7 to +48 MB | about 3x less |
| `SELECT COUNT(*)` on `Transactions` during a commit | not measured | 2-62 ms | does not wait for the commit |
| Parse alone, with the Excel-error-cell scan added | 13.0-14.2 s | 7.8 s (25,673 rows/s) | no slowdown (the scan is a byte search, run only when needed; the lower figure is also machine noise) |
| Export 100,000 invalid rows from a 200,000-row file | MiniExcel: 7.3 s, +213 MB | 6.3 s (15,752 rows/s), +13 MB | about 16x less memory, memory no longer grows with rows |

The single change that mattered most was replacing the Entity Framework row-by-row loader with `SqlBulkCopy`
(section 3.1). Nothing else comes close.

## 2. Development stages and what each one did to performance

| # | Stage | Performance effect |
|---|---|---|
| 1 | Streaming Excel parser (ExcelDataReader, lazy `IEnumerable<ImportRow>`) | Parsing 200,000 rows takes 13-14 s and memory stays flat, because rows are never held as a list. Parsing is about 14 s of every import. |
| 2 | Staging tables + set-based classification in SQL | Classification is a few set-based `UPDATE`s joined to `Transactions` instead of one lookup per row from the app. About 6-9 s for 200,000 rows. |
| 3 | Classify in row-number ranges of 4,000 | Keeps each `UPDATE` under SQL Server's lock-escalation threshold, so one import does not take a table lock and block other users. A concurrency choice, not a speed-up. No before/after timing was taken. |
| 4 | EF Core loader (first working version) | Slow: 766 rows/s in the first measurement, 705 rows/s in the controlled baseline (283.8 s). Unusable at the planned file sizes. |
| 5 | **`SqlBulkCopy` loader** | **Load 283.8 s to 16.7 s in the same-day comparison.** See 3.1. |
| 6 | Commit step (single transaction, application lock, set-based insert/update) | 200,000 new rows committed in 17-27 s. See 3.2. |
| 7 | `READ_COMMITTED_SNAPSHOT` on, discard/commit locking | Readers do not wait for a commit. Discard cannot race a commit. See 3.3. |
| 8 | Excel error-cell scan (`ErrorCellScanner`) | A correctness fix with no measurable cost: a byte search of the sheet XML, and the full XML pass only when an error cell exists. |
| 9 | Invalid-row export with a streaming xlsx writer | 100,000 rows in 6.3 s with flat memory (+13 MB). See 3.4. |
| 10 | Re-check against the database, per-row conflict details | Set-based, batch-scoped `UPDATE`s in 4,000-row chunks that only write rows that changed. 1.9 s for a 200,000-row batch against 500,000 existing rows, working set +1 MB. See section 8. |
| 11 | Query screen: parameterized LINQ search, server-side paging and sorting, separate count and totals queries | First page of 100 in 0.01-0.5 s on 500,000 rows for the everyday searches; count and totals take 0.01-1 s and fill in after the rows. See section 8. |
| 12 | Sort tie-break follows the sort direction | Default search (newest first, unfiltered) page 2.36 s to 0.01 s, no new index. See section 8. |
| 13 | Query export in 10,000-row keyset chunks (selected columns or all 28) | 500,000 rows: CSV 12.5 s (+35 MB), xlsx 22.8 s (+25 MB). Memory does not grow with rows. Writing fewer columns only makes the file smaller; it was not timed separately. See section 8. |
| 14 | Local SQLite provider for the Import pipeline (prepared INSERT in one transaction, set-based SQL, `BEGIN IMMEDIATE` commit) | 200,000 rows: load 12.8 s (insert part about 1.5 s), classify 3.6 s, commit 13.7 s, working set +50 MB, readers unblocked. See section 9. |

## 3. What changed the numbers, and why

### 3.1 EF Core to `SqlBulkCopy` (the big one)

Controlled comparison, same file, same machine, same day:

| Metric | EF Core | `SqlBulkCopy` |
|---|---|---|
| Parse alone | 14.2 s | 13.0 s |
| Load (parse + insert) | 283.8 s (705 rows/s) | 16.7 s (11,955 rows/s) |
| Database insert only (load minus parse) | about 269.6 s | about 3.7 s |
| Classify | 6.7 s | 5.8 s |
| Total (load + classify) | 290.4 s | 22.5 s |
| Working-set growth | +125 MB | +42 MB |

Why it is so much faster:

- **No per-row overhead.** EF creates an entity object per row, tracks it, builds an `INSERT` with parameters and
  reads back results. For 200,000 rows that is hundreds of thousands of objects and a very large number of
  parameter round trips. `SqlBulkCopy` uses SQL Server's bulk-load protocol: rows are streamed over the connection
  in 10,000-row batches with no per-row statement.
- **Streaming end to end.** A forward-only `DbDataReader` (`ImportRowDataReader`) hands rows to `SqlBulkCopy` one at
  a time as the parser produces them. No list, no `DataTable`, no entity graph. That is also why memory growth fell
  from +125 MB to +42 MB.
- **The database stopped being the bottleneck.** The insert part dropped from about 270 s to about 4 s. What remains
  of the 16.7 s is the Excel parse (13.0 s). **Parsing is now about 80% of the load time.**

Deliberately not done: `TableLock` (it would be faster, but it blocks everyone else staging at the same time) and
disabling indexes during load. Neither was added without agreeing it first.

### 3.2 Commit step

200,000 rows into `Transactions` took 27.1 s in the first run and 17.3 s in the latest one. The run-to-run gap is
larger than any change made in between, so read it as "17-27 s".

- One `INSERT ... SELECT` (new rows) and one `UPDATE ... JOIN` (approved overwrites) inside a single transaction.
  Set-based, so there is no per-row traffic.
- An exclusive application lock (`sp_getapplock`) serializes commits. This costs nothing when idle; a second commit
  waits for the first (default up to 120 s, setting `CommitLockTimeoutSeconds`).
- The staging rows are deleted after the transaction, in 50,000-row chunks, so the transaction and the lock are not
  held while cleaning up.
- Likely table-lock escalation on `Transactions` during the large insert. It was not confirmed, and it is the reason
  readers matter (next section).

### 3.3 `READ_COMMITTED_SNAPSHOT`

With it on, readers see the last committed version of rows instead of waiting for an open transaction. Measured
during a real 200,000-row commit (17.3 s), `SELECT COUNT(*) FROM dbo.Transactions` from another connection returned
in 0.006, 0.003, 0.002, 0.002 and 0.062 s, while the commit was still running every time. It returned 0 each time,
because uncommitted rows are not visible. Without snapshot reads those queries would be expected to wait for the
commit. That case was not measured, so the "before" figure is an expectation, not data.

The discard-versus-commit hardening (conditional `UPDATE` plus `UPDLOCK, HOLDLOCK` on the batch row) is a
correctness change. In the staggered test the discard waited 1.9 s for a 50,000-row commit and was then refused.

### 3.4 Invalid-row export: why MiniExcel was replaced

The export reads the batch's invalid row numbers in keyset pages of 10,000, streams the source file once and joins the
two by row number, so neither side is ever loaded whole. The writer decides whether the whole thing stays flat:

| Exported rows | MiniExcel (managed memory growth) | Own `StreamingXlsxWriter` |
|---|---|---|
| 20,000 | +49 MB | not measured separately |
| 50,000 | +79 MB | not measured separately |
| 100,000 | +141 MB (working set +213 MB) | working set +13 MB |

- **Why MiniExcel grew with the row count:** it opens its zip in update mode, which holds each worksheet in memory until
  the file is closed, so a 100,000-row export is held as about 90 MB of XML plus overhead. It also rewrote text such as
  `2024/04/03` when given a date format for a column.
- **What the replacement does:** a forward-only `ZipArchive` in create mode, with the sheet XML written row by row,
  text stored inline (no shared-strings table) and a small stylesheet for the date format. Only the current row is held.
- **Result:** 100,000 rows in 6.3 s (15,752 rows/s), a 9.5 MB file, working set 117 MB to 131 MB, and the file reads
  back as 100,000 rows with the real parser.

## 4. Where the time goes now (200,000 rows)

| Step | Time | Share of a stage + commit run |
|---|---|---|
| Excel parse (inside "load") | about 13 s | largest |
| Bulk insert into staging | about 4 s | small |
| Classify in SQL | about 6-9 s | medium |
| Commit | about 17-27 s | largest single database step |
| Export of 100,000 invalid rows (on demand) | about 6 s | not part of an import run |
| Query: first page of results (500,000 rows) | 0.01-0.5 s | not part of an import run; see section 8 |

## 5. Options not taken yet

These need a decision before being done.

1. **Overlap parsing and inserting** (a producer thread feeding a bounded queue). The parse and the insert currently
   alternate on one thread. Overlapping them could bring the load close to the parse time alone (about 13 s). Low risk.
2. **Faster Excel parsing.** Parsing is now the floor of the load. Options are limited with xlsx; an option is to
   check the reader's settings and avoid per-cell work in normalization.
3. **Commit under `TableLock` or with indexes disabled.** Faster, but blocks other users or needs index rebuilds.
   Not worth it while commit is 17-27 s for 200,000 rows.
4. **Lock-escalation check on `Transactions` commit.** Confirm with a lock trace; if confirmed, insert in key ranges
   like the classify step does.

## 6. How to reproduce

```
$env:EXCELSEARCH_RUN_LARGE = '1'
dotnet test src/ExcelSearch.Tests --logger "console;verbosity=detailed"
```

The output reports parse, load, classify, commit and reader-latency timings, plus working-set growth, for the
large tests in `StagingStoreContract.cs` and `CommitContract.cs` (each runs on SQL Server and on SQLite; add `--filter "FullyQualifiedName~_Sqlite"` for SQLite only), the export timing and memory from `ExportContract.cs`, and
the search, query-export, re-check and conflict-paging timings from `QueryLargeTests.cs` (it seeds 500,000 rows, takes about
5 minutes, and also writes its report to `%TEMP%\ExcelSearch_query_large_report.txt`).
The tests use unique `TEST_` keys and delete only their own rows afterwards.

## 7. Known gaps in this report

- The first EF measurement (766 rows/s) was a single run; the controlled baseline is the 705 rows/s row.
- Classify with and without range chunking was never timed.
- The search timings (section 8) are single-user. They were taken on a table of 501,090 rows; behaviour at several million rows is extrapolated, not measured (the "contains" name search is a scan and grows with the table).
- Numbers come from one machine and one database instance; run-to-run noise is 10-25%.
- **Not timed:** the RegNo / ChequeInstNo / AccountNo search filters (section 8), and the export of invalid rows at sizes other than 100,000 rows. The MiniExcel growth figures at 20,000 and 50,000 rows come from a throwaway probe.
- The 7.8 s parse figure is lower than the earlier 13-14 s. Nothing in the parser was made faster in between, so treat
  it as machine and run-to-run variation until it is re-measured under the same conditions.

## 8. Query screen searches, re-check and conflict paging (500,000 rows)

Measured by `QueryLargeTests` (`EXCELSEARCH_RUN_LARGE=1`): 500,000 synthetic rows committed through the normal stage and
commit path (5 batches of 100,000: load 5-12 s, classify 3-7 s, commit 13-20 s each), 501,090 rows in `Transactions` in
all. Seconds, after the tie-break fix described below; "all three" runs page, count and totals at once, as the screen does.

| Search | Matching rows | Page (100) | Count | Totals | All three | Warm repeat |
|---|---:|---:|---:|---:|---:|---:|
| Date range (one month) | 7,750 | 0.17 | 0.02 | 0.12 | 0.07 | 0.07 |
| Exact CNIC | 1 | 1.50 (first call: plan compile) | 0.01 | 0.01 | 0.00 | 0.00 |
| Application no starts with | 10,000 | 0.07 | 0.03 | 0.68 | 0.71 | 0.65 |
| Customer name contains | 8,424 | 0.10 | 1.03 | 0.96 | 1.35 | 1.10 |
| Bank + project | 1,667 | 0.53 | 0.38 | 0.38 | 0.51 | 0.59 |
| Credit between | 20,010 | 0.11 | 0.24 | 0.24 | 0.26 | 0.26 |
| Date range + bank + customer contains | 0 | 0.45 | 0.87 | 0.84 | 1.10 | 1.03 |
| Unfiltered, ValueDate newest first (default) | 501,090 | 0.01 | 0.28 | 0.40 | 0.40 | 0.48 |
| Unfiltered, CustomerName | 501,090 | 0.01 | 0.15 | 0.45 | 0.43 | 0.41 |
| (extra) Unfiltered, Credit | 501,090 | 1.75 | 0.13 | 0.39 | 1.98 | 1.89 |
| (extra) 500,000-row result | 500,000 | 0.01 | 2.36 | 2.73 | 3.11 | 3.18 |

- **Biggest win: tie-break direction.** The first run had the default search (unfiltered, newest first) at 2.36 s for the page.
  The `ORDER BY` was `ValueDate DESC, PayOrderNo ASC, TxnRefSeqNo ASC`, but the `ValueDate` index holds the key columns
  ascending, so reading it backwards gives `PayOrderNo DESC` and SQL Server sorted all 500,000 rows. Making the tie-break
  follow the sort direction lets it read the index backwards and stop after 100 rows: **2.36 s to 0.01 s** with no new
  index. Sorting by `CustomerName` already matched the index and was 0.01 s.
- Still slow (about 2 s or more): sorting an unindexed column over everything (Credit, 1.75 s page), and a result of
  500,000 rows (count and totals 2.4-2.7 s: they must read every row). Customer name "contains" is a scan (about 1 s, grows
  with the table). Proposals, none applied, are in `Scripts/db/005_query_indexes.sql`.
- Memory: the searches never grew the working set by more than 17 MB. Exporting all 500,000 rows: CSV 12.5 s (40,104 rows/s,
  141 MB file, +35 MB); xlsx 22.8 s (21,887 rows/s, 90 MB file, +25 MB): flat, as with the invalid-row export.
- Dropdown lists (distinct values): 0.2-0.5 s each; cached by the service.
- A running search is cancelled 0.02 s after its token fires.
- **Re-check and conflict paging** on a 200,000-row batch (180,000 conflicts, 20,000 identical) against the 500,000 existing rows:
  stage + classify 18 s; `RefreshAgainstDatabaseAsync` 1.9 s with nothing changed and 1.9 s when 20,000 rows had changed
  (working set +1 MB); `GetConflictPageAsync` 0.11 s (page 1), 0.13 s (page 500), 0.34 s (page 1,800), working set +1-2 MB.
  The "Re-check" gap listed in section 7 is therefore closed for this size.
- Not timed: RegNo / ChequeInstNo / AccountNo filters (no index of their own), and the search with several concurrent users.

## 9. Local SQLite provider (Import pipeline)

The same 200,000-row synthetic workbook, the same contract tests, run against a temporary SQLite file (`EXCELSEARCH_RUN_LARGE=1`,
filter `_Sqlite`). Same PC, Debug test build. The SQL Server column is copied from sections 1 and 3 of this report (versions 1.0-1.3);
it was NOT re-measured on 2026-10-13 (the SQL Server service was stopped and could not be started from the session), so the comparison
is across days, not side by side. SQLite version bundled with Microsoft.Data.Sqlite 8.0.31: **3.53.3**.

| Step (200,000 rows) | SQLite | SQL Server (from this report) |
|---|---|---|
| Parse alone | 11.3 s | 7.8-14 s |
| Load into staging (parse + insert) | 12.8-13.9 s (14,400-15,600 rows/s) | 12.7-21.3 s |
| Of which the insert only | about 1.5 s | about 3.7 s |
| Classify | 3.3-3.6 s | 5.8-9.3 s |
| Same file again: load + classify | 9.6 s + 3.3 s, all 200,000 `ExactDuplicate` | not separately listed |
| Commit (includes deleting the staging rows afterwards) | 13.7 s | 17-27 s |
| `SELECT COUNT(*)` on `Transactions` while the commit runs | 0.001-0.039 s (WAL: readers read the last committed state) | 2-62 ms (RCSI) |
| Working-set growth while staging and committing | +50 MB (83 to 133 MB) | +7 to +48 MB |
| Export of 100,000 invalid rows | 8.5 s, +15 MB | 6.3 s, +13 MB |

Reading the table: parsing the Excel file is the floor on both (the SQLite insert itself is fast, one transaction and one prepared `INSERT`).
SQLite's classify and commit are not slower than SQL Server's at this size on one machine. This says nothing about concurrent users:
SQLite has one writer, SQL Server many.

### 9.1 `WITHOUT ROWID` was measured and not used

Throw-away benchmark (not part of the repo): 200,000 rows with ~12 secondary indexes, Release build, rows generated in memory, the real
store code, one run per layout (run-to-run noise 10-25%). `Transactions` has a wide row (about 30 columns) and a composite text key.

| Layout | Load | Classify | Commit + staging delete | 2nd file: classify | 20,000 key lookups | File |
|---|---|---|---|---|---|---|
| ordinary rowid tables (kept) | 8.6 s | 3.7 s | 20.0 s | 6.3 s | 2.99 s | 221 MB |
| `Transactions` WITHOUT ROWID | 6.7 s | 3.9 s | **46.0 s** | 7.4 s | 2.60 s | 263 MB |
| `ImportStaging` WITHOUT ROWID | 6.7 s | 4.6 s | 20.0 s | 6.9 s | 2.36 s | 218 MB |
| both WITHOUT ROWID | 6.7 s | 4.7 s | 45.6 s | 8.6 s | 2.57 s | 260 MB |

For `Transactions` it more than doubles the commit time and grows the file (every secondary index entry then carries the long composite key,
and rows are wide); the faster point lookups are not worth it. `ImportStaging` gains nothing (its key is two integers and its
classify got slower). Both tables stay ordinary rowid tables. (The differences in "Load" between rows are noise or warm-up.)

### 9.2 A faster commit insert was measured and NOT applied

Sorting the `INSERT ... SELECT` by the key (`ORDER BY PayOrderNo, TxnRefSeqNo`) so rows reach the primary-key b-tree in order made it
slower in the same benchmark (26.3 s against 16.1 s for the insert alone, rowid layout): the 11 secondary indexes are still filled in random order
and the sort is added work. Not applied, and no other speed-up was added (per the "ask first" rule).

### 9.3 Things to know when reading SQLite timings

- Waiting for another connection's lock is SQLite's busy handler (`PRAGMA busy_timeout`) followed by Microsoft.Data.Sqlite retrying until the
  command's `CommandTimeout`; measured: with only one of the two set, the wait is the longer one (about 30 s). A commit waits `CommitLockTimeoutSeconds`
  (both set to it); everything else waits 30 s.
- A staging load holds the write lock for the whole file (one transaction). Another writer waits up to 30 s.

## 10. Query screen on the local SQLite database (500,000 rows)

The same `QueryLargeContract` test as section 8, run on a temporary SQLite file (`EXCELSEARCH_RUN_LARGE=1`, filter `QueryLargeTests_Sqlite`):
500,000 `TEST_` rows seeded through stage + classify + commit, first page of 100 + count + totals, in seconds. The SQL Server column is copied
from section 8 (version 1.2) and was NOT re-measured today; the two runs were made on different days on the same PC, so compare the size of the
numbers, not the second decimal. No index was added on either database.

| Search | Matching rows | SQLite: page | count | totals | all three in parallel | warm repeat | SQL Server: all three | warm repeat |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Date range (one month) | 7,750 | 0.05 | 0.01 | 0.07 | 0.02 | 0.02 | 0.07 | 0.07 |
| Exact CNIC | 1 | 0.49 | 0.10 | 0.45 | 0.82 | 0.64 | 0.00 | 0.00 |
| Application no starts with | 10,000 | 0.30 | 0.05 | 0.30 | 0.62 | 0.64 | 0.71 | 0.65 |
| Customer name contains | 8,334 | 0.35 | 0.06 | 0.32 | 0.66 | 0.67 | 1.35 | 1.10 |
| Bank + project | 1,667 | 0.34 | 0.31 | 0.34 | 1.07 | 1.01 | 0.51 | 0.59 |
| Credit between | 20,010 | 0.29 | 0.26 | 0.29 | 0.83 | 0.86 | 0.26 | 0.26 |
| Date range + bank + customer contains | 0 | 0.07 | 0.06 | 0.07 | 0.20 | 0.19 | 1.10 | 1.03 |
| Unfiltered, ValueDate newest first (default) | 500,000 | 0.29 | 0.01 | 0.42 | 0.76 | 0.72 | 0.40 | 0.48 |
| Unfiltered, CustomerName | 500,000 | 0.32 | 0.01 | 0.44 | 0.72 | 0.74 | 0.43 | 0.41 |
| (extra) Unfiltered, Credit | 500,000 | 0.31 | 0.01 | 0.46 | 0.72 | 0.71 | 1.98 | 1.89 |
| (extra) 500,000-row result | 500,000 | 0.40 | 0.07 | 0.51 | 1.04 | 0.97 | 3.11 | 3.18 |

What the table says:
- **Nothing on SQLite is over about 1.1 s**, so no SQLite index change is proposed (the rule was: propose, do not apply, anything over about 2 s).
- Reference-number and name searches are scans on SQLite (about 0.3 s per 500,000 rows for the page and again for the totals): `LIKE` only uses an
  index on a NOCASE column, and the filter columns are BINARY. If the table grows to several million rows, the thing to try first is a
  `COLLATE NOCASE` index on `CustomerName` and `ApplicationNo` (for example `CREATE INDEX ... ON Transactions (CustomerName COLLATE NOCASE)`); it is
  a proposal, not measured, and nothing was created.
- Sorting a text column uses `COLLATE NOCASE` and still takes 0.32 s for the first page of 500,000 rows (SQLite keeps only the top 100 while scanning).
- Money is sorted and summed as integers: the credit range, the Credit sort and the totals run in SQL with no rows loaded into memory
  (the whole run grew the working set by at most 16 MB).
- Dropdown values: 0.05-0.38 s per field. The cancel test finished before it could cancel (0.3 s), as expected for a synchronous engine.

Other numbers from the same run (SQLite; SQL Server from section 8 in brackets): export of all 500,000 rows to CSV 12.9 s, +52 MB (12.5 s, +35 MB);
to xlsx 21.6 s, +55 MB (22.8 s, +25 MB); re-check of a 200,000-row batch 1.5 s unchanged and 3.0 s with 20,000 rows changed (1.9 s, 1.9 s);
conflict page 0.02-0.05 s (0.11-0.34 s). Seeding: commit of each 100,000-row batch took 9.6 s, 12.6 s, 13.3 s, 14.6 s, 16.1 s - it grows with the
table because the 12 indexes get bigger.

## 11. After the surrogate key (rows without a pay order no), 2026-10-14

Schema change: `Transactions.Id` is the clustered key, four new indexes (`PayOrderNo`, `RowHash`, filtered `SlipNo`, filtered `NarrationCnic`) and a filtered unique
index on the old key, so 14 plain indexes instead of 10 plus the unique one. Sort tie-break and export keyset are `Id`. Measured with the same Large tests, one
provider at a time (the first run overlapped other heavy tests and its numbers are not used; it also hit two test-cleanup deadlocks, now retried).

| | Before (sections 8, 10) | After |
|---|---|---|
| SQL Server: load / classify / commit of one 100,000-row batch | 5-12 s / 3-7 s / 13-20 s | 5-9 s / 5-7 s / 12-16 s |
| SQL Server: unfiltered, newest first, page (default search) | 0.01 s | 0.02 s (the `Id` tie-break reads the `ValueDate` index backwards as before) |
| SQL Server: unfiltered sorted by Credit, page | 1.75 s | 1.59 s |
| SQL Server: 500,000-row result, count / totals | 2.4-2.7 s | 2.3 s / 2.9 s |
| SQLite: commit of one 100,000-row batch | 9.6-16.1 s | **22.5-38.4 s** (more indexes to maintain; load and classify unchanged at 2-6 s) |
| SQLite: slowest search | about 1.1 s | 1.9-2.1 s (exact CNIC, first call; it now checks `Cnic` and `NarrationCnic`) |
| Working set growth while searching | 13-17 MB | 14-16 MB |

Real statement file (50,921 rows, no keys): stage + classify 10.8 s then commit 4.2 s on SQL Server, 4.8 s + 3.0 s on SQLite (first run, cold); the same file staged
again takes 6.8 s / 4.2 s and every row is Already in database. 200,000-row load/classify alone on SQL Server, no other test running: 31 s / 18 s; invalid-row
export of 100,000 rows 11.3 s.

What to take from it: the SQL Server figures did not move. The SQLite commit got about twice as slow because `Transactions` has four more indexes; it is still
seconds for tens of thousands of rows (the real file commits in 3 s). If SQLite commit time matters, the first thing to try is dropping `IX_Transactions_RowHash`
and relying on `SlipNo`, but nothing was changed.

## 12. SQL Server 2012 production server (200,000-row tests), 2026-10-19

The app was made to run on SQL Server 2012 (`STRING_AGG` replaced, EF `UseCompatibilityLevel(110)`, indexes created outside `CREATE TABLE`, multi-select filters as ORed
parameters; see `db.md`). The new production server is **SQL Server 2012 Express (11.0.2100) on another machine, `192.168.8.10`, reached over the LAN**, database
`AuditExcel`, empty before the run. The dev server is the local SQL Server Express used in sections 1-11. Same PC, same Debug test build, same synthetic 200,000-row
workbook, same day, tests run one after the other (`EXCELSEARCH_RUN_LARGE=1`; filter: the three 200,000-row SQL Server tests). The 500,000-row Query tests were NOT run.

Tests first: Unit 265 passed; Integration 79 passed on the 2012 server and 79 on the dev server (parallel and serial runs on the 2012 server).

| Step (200,000 rows) | Dev server (local) | 2012 server (LAN) |
|---|---|---|
| Parse alone | 7.1 s | 6.2 s |
| Load into staging (parse + insert) | 13.8 s (14,548 rows/s); 11.6-11.8 s in the commit test | **50-53 s (3,749-3,974 rows/s)** |
| Of which the insert only | about 6.7 s | **about 47 s** |
| Classify | 10.4 s (13.2 s for the repeat file) | 7.5-7.6 s (12.8 s for the repeat file) |
| Commit | 17.2 s | **57.0 s** |
| Same file again: load + classify, all 200,000 Already in database | 11.6 s + 13.2 s | 52.3 s + 12.8 s |
| `SELECT COUNT(*)` while the commit runs | 0.023-0.036 s | 0.004-0.059 s (not blocked; value 0 because uncommitted rows are invisible) |
| Export of 100,000 invalid rows | 6.0 s, +19 MB | 7.4 s, +12 MB |
| Working-set growth | +1 to +42 MB | +9 to +43 MB |

What the table says:
- Everything that runs inside the database (classify, the repeat-file classify) is as fast on the 2012 server as locally, and reading while a commit runs is not blocked
  (READ_COMMITTED_SNAPSHOT is on there).
- What is slower is whatever moves rows over the network: the bulk insert is about 7x slower and the commit about 3x slower. The commit runs inside the server, so its time is
  the 2012 Express machine's disk and CPU (and 10 GB / 1 GB-memory Express limits), not the network. The load is limited by the network and the old server together. A file of
  about 50,000 rows (the real statements) would take roughly a quarter of these times; this was not measured.
- Not separated: how much of the gap is the server's hardware, how much SQL Server 2012, and how much the LAN. The tests do not tell them apart.
- Measured once each (the commit test twice, see below); run-to-run noise is 10-25% or more on a network.

Two things seen during the run:
- **TLS 1.0.** The driver printed "the negotiated TLS 1.0 is an insecure protocol" for the connection to this server. The original 2012 release does not offer TLS 1.2. Data
  (including the `sa` password exchange) is encrypted only with the old protocol. Applying the SQL Server 2012 service pack (SP4) on that server, or putting the server on a
  trusted network segment, fixes or reduces it. No app change.
- **One dropped connection.** The first 200,000-row commit test failed with "A transport-level error has occurred when receiving results from the server (Physical
  connection is not usable)" during the bulk load of stage #1, after about 80 s. The same test passed when run again alone (3 min 52 s). Cause not established (network or
  server); treat a long bulk load over this link as able to fail, in which case the batch stays Staged and the file can be loaded again. The failed run left one empty Staged batch
  in the table, which was deleted by hand.

## Version history

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-10-12 | First report: EF to `SqlBulkCopy`, commit step, `READ_COMMITTED_SNAPSHOT`, reader latency during commit. |
| 1.1 | 2026-10-12 | Added the invalid-row export (MiniExcel replaced by a streaming writer: +213 MB to +13 MB), the error-cell scan cost, and the not-yet-timed re-check. |
| 1.2 | 2026-10-12 | Added section 8: Query screen search timings at 500,000 rows, the tie-break fix (2.36 s to 0.01 s), export memory, re-check and conflict paging at 200,000 rows. |
| 1.3 | 2026-10-12 | Stage list extended (11-13: Query screen, tie-break fix, query export), "where the time goes" and reproduction steps now cover the Query tests, known-gaps list updated. Numbers unchanged from 1.2. |
| 1.4 | 2026-10-13 | Added section 9: the local SQLite provider (200,000-row load, classify, commit, memory, reader latency), the `WITHOUT ROWID` measurement (not used) and the ordered-insert measurement (not applied). SQL Server figures not re-measured. |
| 1.5 | 2026-10-13 | Added section 10: the Query screen on SQLite at 500,000 rows next to the SQL Server figures; no index proposed (nothing over about 1.1 s). |
| 1.6 | 2026-10-14 | Added section 11: the surrogate-key change (more indexes), SQL Server unchanged, SQLite commit about 2x slower, and the real key-less statement file end to end. |
| 1.7 | 2026-10-19 | Added section 12: the 200,000-row import tests on the SQL Server 2012 production server next to the dev server (load 3.7x, commit 3.3x slower; classify unchanged), the TLS 1.0 warning and one dropped connection. |
