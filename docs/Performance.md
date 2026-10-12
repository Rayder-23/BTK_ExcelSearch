---
title: ExcelSearch Performance Report
version: 1.3
last_updated: 2026-10-12
status: current
scope: Excel import pipeline (parse, stage, classify, commit), invalid-row export, reader latency, Query screen searches, re-check and conflict paging
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
large tests in `StagingStoreTests.cs` and `CommitTests.cs`, the export timing and memory from `ExportDbTests.cs`, and
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

## Version history

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-10-12 | First report: EF to `SqlBulkCopy`, commit step, `READ_COMMITTED_SNAPSHOT`, reader latency during commit. |
| 1.1 | 2026-10-12 | Added the invalid-row export (MiniExcel replaced by a streaming writer: +213 MB to +13 MB), the error-cell scan cost, and the not-yet-timed re-check. |
| 1.2 | 2026-10-12 | Added section 8: Query screen search timings at 500,000 rows, the tie-break fix (2.36 s to 0.01 s), export memory, re-check and conflict paging at 200,000 rows. |
| 1.3 | 2026-10-12 | Stage list extended (11-13: Query screen, tie-break fix, query export), "where the time goes" and reproduction steps now cover the Query tests, known-gaps list updated. Numbers unchanged from 1.2. |
