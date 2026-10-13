---
title: Sample workbooks for trying the import screen
version: 1.2
last_updated: 2026-10-13
generator: Scripts/samples/make_samples.py
---

# Sample workbooks

Files `01`-`10` in this folder (`Samples/test/`) are made by `python Scripts/samples/make_samples.py` (needs `pip install openpyxl`; output is
deterministic). The two `software filed ...` files in the parent `Samples/` folder are the original bank-format examples and are not generated.
`SampleFilesTests` (parse-only, no database) checks the row counts below.

**Warning:** committing these writes real rows to the development `Transactions` table (keys look like `PO-A-0001`,
not `TEST_`). Staging, previewing and discarding write nothing to `Transactions`. There is no "delete committed
rows" in the app yet, so commit only what you are happy to leave there (or delete those rows yourself in SQL).

## What each file is for

| File | Rows | Expected result on the preview | Tests |
|---|---|---|---|
| `01_perfect_all_new.xlsx` | 40 | **New 40**, nothing else | The happy path. Commit enabled: "Import 40 new rows". Mixes real dates and text dates (`07/04/2024`, `2024-04-14`, `21-Apr-2024`, `28-4-2024`), a CNIC with and without dashes, a credit row, the real bank headers with their misspellings. |
| `02_invalid_rows.xlsx` | 31 (+1 blank row, skipped) | **New 16, Invalid 15** | Invalid tab with an Error column: missing PayOrderNo / Txn Ref / Value Date, impossible date, text date, wrong date format, non-numeric Debit and Credit, an Excel error value (`#DIV/0!`) in a cell, text over the column limit (customer, key, branch code), a row with three errors, and one key that appears twice with different values (both rows land in Invalid). Commit imports only the 16 new rows. |
| `03_reimport_of_01_duplicates_conflicts.xlsx` | 36 | See below: depends on whether `01` was committed | Already-in-database, Conflicts and Repeated-in-file. |
| `04_repeated_rows_in_file.xlsx` | 26 | **New 20, Repeated in file 4, Invalid 2** | One row repeated 3 more times and another once (identical copies are skipped), and a key that appears twice with different values (both Invalid). |
| `05_missing_required_header.xlsx` | 10 | **Rejected**: "Required header(s) not found ... payorder no" | The amber message on the idle screen; no batch is created. |
| `06_large_for_paging.xlsx` | 1,420 | **New 1,050 (11 pages), Repeated in file 120 (2 pages), Invalid 250 (3 pages)** | Paging: Previous/Next, "Page x of y", the loading overlay, scrolling. Commit and re-stage it to see Already in database 1,050 (11 pages). |
| `07_not_really_excel.xlsx` | - | **Rejected**: not a readable workbook | A text file with an `.xlsx` name. |
| `08_header_on_row_4_variant_headers.xlsx` | 10 | **New 10** | Three title lines above the header, and header spellings `PAYORDER NO`, `txn_ref_seq_no`, `value date`, `Dealer Name`, `Narration1`. First data row is Excel row 5. |
| `10_query_demo_3000_rows.xlsx` | 3,000 | **New 3,000** | Data for the **Query screen**: two years of dates, 8 banks, 6 projects, 12 dealers, about 260 customers, plus rows made to be found (see the Query checklist). Keys `PO-Q-0001`..`PO-Q-3000` and `PO-Q-NEEDLE`. `python Scripts/samples/make_samples.py 10` regenerates just this file. |
| `../software filed (only columns).xlsx` | 0 | Zero rows | The headers-only edge case: Commit stays disabled. |

## The two-step run (Already in database + Conflicts)

1. Stage and **commit** `01_perfect_all_new.xlsx` (40 new rows).
2. Stage `03_reimport_of_01_duplicates_conflicts.xlsx`. You should see:

| Tab | Count | Why |
|---|---|---|
| New | 8 | brand-new keys `PO-C-0001`..`0008` |
| Conflicts | 10 | rows 16-25 of file 01 with a changed Debit or a "(corrected)" customer name |
| Already in database | 15 | rows 1-15 of file 01, unchanged |
| Repeated in file | 2 | `PO-C-0001` appears twice more |
| Invalid | 1 | last row has no Value Date |

   If `01` was **not** committed first, the same file shows New 33, Repeated in file 2, Invalid 1.
3. Commit imports the 8 new rows. The 10 conflicts are skipped (overwrite choices come in the next step).

## Other things to try with these files

- Cancel while loading `06` (the batch must disappear: check `ImportBatches` has no `Staged` row for it).
- Stage `02`, then **Discard** (with its confirmation), or close the window (Yes / No / Cancel).
- Stage the same file in two app windows and commit one, then try to commit or discard in the other.

## Manual checklist: overwrite choices, re-check, export

Use `.\Scripts\run\run-dev.cmd` (run it twice to get two windows where a step says "second window"). Committing writes real
rows to the development database (see the warning above).

**A. Overwrite choices (Conflicts tab)** - files `01` and `03`
1. Make sure `01` is committed (stage + commit it; skip this if it already is - it is in the development database now). Stage `03`: Conflicts 10, New 8.
2. Open the Conflicts tab: an **Overwrite** checkbox column, and "0 of 10 conflicts will overwrite". The commit button says "Import 8 new rows".
3. Tick two rows: the label says "2 of 10 ...", the button says "Import 8 new, update 2". Untick one: "1 of 10", "update 1".
4. **Overwrite all** -> "10 of 10", all ticked. **Skip all** -> "0 of 10", all clear, button back to "Import 8 new rows".
5. Tick one row, click **Import**: the confirmation lists insert 8, overwrite 1, skip (9 conflicts + 15 already in database + 2 repeated = 26) and not import 1 invalid. Confirm; the done screen shows 8 new, 1 updated. (Committing `03` adds its 8 new rows; stage and discard instead if you do not want them.)

**B. Re-check against the database** - files `06` and `09`, two windows (do this before A.6; it commits `06`)
1. Window A: stage `09` while `06` is NOT yet committed: New 250, no conflicts. Leave it open.
2. Window B: stage `06` and commit it (1,050 new rows).
3. Window A: click **Import** directly. It must refuse with a message that rows changed and offers **Re-check against database**.
4. Window A: click **Re-check against database**. Expected message: "250 rows changed: 250 New -> Conflict." The cards, tabs and commit button update; you stay on the tab you were on, and the Conflicts tab now has 250 rows (3 pages).
5. Click **Re-check** again: "Nothing changed: the preview still matches the database."
6. (If `01` has not been committed yet, the same idea works with `03`: stage it first, commit `01` in the other window, re-check -> "25 rows changed: 15 New -> Already in database, 10 New -> Conflict.")

**A.6 Paging of overwrite choices** - continue in window A from B.5 (250 conflicts, 3 pages)
1. Tick a row on page 1, go to page 2 and back: it is still ticked (it is saved in the database).
2. **Overwrite all** -> "250 of 250 conflicts will overwrite" even though only 100 rows are on screen; the button says "Import 0 new, update 250".
3. **Skip all** -> "0 of 250". Then Discard the batch.
**C. Export invalid rows** - file `02` (and `04`, `06`)
1. Stage `02` -> Invalid tab (15 rows). The **Export invalid rows...** button appears at the bottom left (only on the Invalid tab).
2. Click it: a save dialog proposes `02_invalid_rows_errors.xlsx`. Save. A message gives the row count and offers to open the folder.
3. Open the exported file: the original header row, then the invalid rows with their original values (numbers still numbers, `Br. Code` like `0101` still text with its zero, the date cells as dates), then **Source Row** (the Excel row in `02`) and **Errors**. The `#DIV/0!` cell appears as the text `#DIV/0!`.
4. Fix the values in the exported file, save, and stage it with **Choose Excel file**: the fixed rows come in as New (the Source Row and Errors columns are ignored).
5. Fallback, missing file: copy `02` to a temp folder, stage the copy, delete the copy, export. The message says the original file was not found and the parsed values were exported (same columns, still with Source Row and Errors).
6. Fallback, changed file: stage a copy of `02`, open the copy in Excel, change a cell and save, then export: the message says the file has changed since it was imported.
7. Cancel: the save dialog's Cancel does nothing. (The mid-export Cancel button only shows for large exports; it is covered by tests.)

## Manual checklist: the Query screen

**Seed data:** stage and **commit** `10_query_demo_3000_rows.xlsx` (3,000 new rows; skip this if it is already committed). Files `01` and
`06` add more rows if you commit them too. Every step below adds the filter **Pay order no = `PO-Q-`** (starts with) so the answers
stay the same whatever else is in the database; the "Pay order no" box is one of the common filters in the filter drawer on the left.

**1. Search and totals**
1. Click **Query** in the left list. The **filter drawer** is open on the left and the grid area says "Set your filters and press Search".
2. Type `PO-Q-` in *Pay order no* and press **Search** (or press **Enter** in the box). The drawer folds away so the results get the full width (the **Filters (1)** button at the top reopens it, and
   a green chip `Pay order no: PO-Q-` shows what is applied). Rows appear first, newest first (the first row is `Edge Last Day`, 31/12/2024);
   the slim bar at the bottom then fills in: Credit **3,020,692,192.00**, Debit **6,388,848.10**, Down payment **203,305,569.31**, and **3,000 rows**
   in green at its right end. "Page 1 of 30".
3. Change *Rows* (bottom right) to 50 ("Page 1 of 60"), 200 ("of 15"), 500 ("of 6"). The totals do not change, and the grid goes back to page 1.
4. **Next** / **Previous** move between pages; Previous is disabled on page 1, Next on the last page. The totals stay the same on every page (they cover the whole result).

**2. Filters** (each one together with `PO-Q-` in *Pay order no*; press **Clear** between them). Inputs are full width; a long value wraps onto more
lines (try a 60-character customer name) and shows in full when you hover. The little **exact** switch sits on the label line. Click the **x** on a chip to
remove just that filter (it searches again); **Clear all** removes them all. Untick "Hide filters after searching" to keep the drawer open.
| Filter | Expected |
|---|---|
| Customer name contains `needle` (any case) | 1 row, `Needle Customer`, `PO-Q-NEEDLE`, Credit 123,456.78, date 29/02/2024 |
| CNIC `42101-9999999-9` (typed with dashes), switch **exact** on | the same 1 row; with **exact** off it still finds it |
| CNIC starts with `42101100` | 768 rows |
| Application no `NEEDLE` (starts with) | 1 row. Application no `00045` (exact on or off) finds only `PO-Q-0509` (its number is the text `00045`, zeros kept) |
| Pay order no exactly `PO-Q-NEEDLE` (exact on) | 1 row |
| Value date from 01/01/2024 to 31/03/2024 | 378 rows |
| Value date from = to = 01/01/2023 | includes `Edge First Day`; from = to = 31/12/2024 includes `Edge Last Day` (both ends are inclusive) |
| *More filters*: Bank = HBL | 376 rows |
| Bank = HBL and Project = Bahria Paradise | 126 rows |
| Bank = HBL and UBL (tick two) | 751 rows; the box says "2 selected" |
| Credit between 10000 and 20000 | 16 rows, including the two rows with exactly 10,000.00 and 20,000.00 but not 9,999.99 or 20,000.01 |
| Customer contains `khan` | 191 rows |
| Customer contains `100%` / `_` / `O'Brien` | 1 row each (`100% Cotton_Mills`, the same row, `O'Brien & Sons "Traders"`): `%` and `_` are plain text, not wildcards |
| Cheque / inst no `000123` (exact) | 1 row (`PO-Q-0509`); leading zeros are kept |
| Import batch no = the number shown in a row's details | all rows of that import |

**3. Sorting** (server-side: the whole result is re-sorted, not just the page)
1. With `PO-Q-` searched, click the **Credit** header: a green up arrow, smallest credit first, back on page 1. Click again: down arrow, largest first (about 2,000,000).
2. Go to page 2: the order continues from page 1 (no row repeats). Click **Customer**: alphabetical, page 1, arrow moves to Customer. Rows with the same value keep a stable order when you page.

**4. Details panel**
1. Click any row: its details open **under the row** across the width of the grid (all 28 fields in groups) and **Imported from** (file `10_query_demo_3000_rows.xlsx`, the Excel row, who imported, when, the batch number).
2. Click `PO-Q-0508` (Customer-name sort or Pay order no filter): *Narration 1* shows two lines (the comma and line break). Click ✕ in the details to close.

**5. Export** (with `PO-Q-` searched: 3,000 rows)
1. Click **Export results** (top right). The save dialog offers *Excel workbook* and *CSV*. Save as `.xlsx`; the message banner says "Exported 3,000 rows" and offers to open the folder.
2. Open it: a bold header, one column per ticked column (12 by default; choose *All columns* for all 28), 3,000 rows, dates as real dates, CNIC and `000123` as text with their zeros. The rows are in Pay order no order, not the grid's sort.
3. Export again as CSV: open it in Notepad. `PO-Q-0508`'s narration is in quotes and spans two lines. (Excel itself shows `000123` as 123 in a CSV: that is Excel's CSV behaviour; use the .xlsx for that column.)
4. Only rows matching the filters on screen are exported, not just the current page: search *Bank = HBL* and export: 376 rows.

**5b. Columns and what an export writes** - click **Columns** (top right): 12 everyday columns are ticked (all 28 fields are listed, the rest hidden); tick *Account no* or *Narration 1* and the column appears; untick *Dealer* and it goes. **All** ticks everything, **Default** goes back to the 12. The first two columns (Value date, Pay order no) stay in place while you scroll sideways.
1. Next to **Export results** the box says **Selected columns** (the default). Search `PO-Q-`, untick *Dealer* and *Project*, tick *Narration 1*, export to .xlsx: the file has exactly the ticked columns, in grid order, for every matching row (3,000), not just the page.
2. Switch the box to **All columns** and export again: all 28 fields, whatever is ticked.
3. Untick every column with the box on *Selected columns*: **Export results** is disabled and the hint says "Tick at least one column to export."; with *All columns* it works again.

**6. Messages and clearing**
1. Type `abc` in a Credit box and press Search: a red message under the filters; nothing is searched. Set a *from* date after the *to* date: another message.
2. Search for `zzzz` in Customer name: "No transactions match these filters." in the grid; **Export** is disabled.
3. Click **Clear**: all filters empty, the grid back to the prompt. The sort order and rows-per-page stay as you set them.
4. A banner (for example after an export) has a ✕ and never goes away on its own.

## Manual checklist: running on the local SQLite database

Uses the same workbooks; the data goes into a SQLite file, never into SQL Server. Use a throw-away path first so you can delete it afterwards.

1. **Switch the database.** At the bottom of the left rail, change the **Database** selector from *SQL Server* to *Local DB*, confirm the restart question, and let the app restart (it asks first if a file is staged). Expect: no SQL Server connection; the selector shows **Local DB**; **Query** is greyed out with a "SOON" mark and the tooltip "Not yet available for Local DB."; the file `%LocalAppData%\ExcelSearch\excelsearch.db` exists (with `-wal` and `-shm` next to it while the app runs). (To use a throw-away file instead, set `Database:SqlitePath` in `appsettings.Development.json` first.)
2. **Happy path.** Choose `01_perfect_all_new.xlsx`: **New 40**; commit: "Committed batch ...: 40 new". Choose it again: **Already in database 40**, nothing to import.
3. **Invalid rows + export.** `02_invalid_rows.xlsx`: **New 16, Invalid 15**; **Export invalid rows** writes an .xlsx with 15 rows, the original cell values, `Source Row` and `Errors`.
4. **Conflicts.** Follow "The two-step run" above with `03_...`: the Conflicts tab lists what differs column by column; tick one row's Overwrite, use Overwrite all / Skip all, **Re-check against database**; commit and confirm the counts (updated vs skipped conflicts).
5. **Repeats and paging.** `04_repeated_rows_in_file.xlsx` (New 20, Repeated in file 4, Invalid 2) and `06_large_for_paging.xlsx` (11 pages of New, 2 of Repeated, 3 of Invalid): Previous/Next and the page counters work.
6. **Rejected files.** `05_...` and `07_...` show the same messages as on SQL Server and create no batch.
7. **Discard.** Stage `10_query_demo_3000_rows.xlsx`, choose Discard and confirm: back to the start screen. Close the app with a staged, uncommitted file: you are asked whether to discard.
8. **Case-insensitive keys.** Stage a copy of `01` in which one PayOrderNo is changed to lower case after `01` was committed: that row is NOT New (it is found as the same key).
9. **Restart.** Close and start again: the data is still there (stage `01` again: Already in database 40), and a stale `Staged` batch from a crash is cleaned up at startup.
10. **Big file (optional).** There is no 200,000-row sample; the automated run does it: `EXCELSEARCH_RUN_LARGE=1 dotnet test src/ExcelSearch.Tests --filter "FullyQualifiedName~_Sqlite"` stages, commits and re-stages 200,000 rows on a temp SQLite file and prints the timings.
11. **Back to SQL Server.** Change the selector back to *SQL Server* and confirm: after the restart the Query screen is available again. (The choice is stored in `%LocalAppData%\ExcelSearch\user-settings.json`; delete that file to fall back to appsettings.)
12. **Closing.** Close the window: it closes at once (under a second) with no error. With a staged file it asks first.

Delete the Local DB file (and its `-wal`/`-shm`) when you are done if it was only a trial. Do not point `SqlitePath` at a network drive or a synced folder.
