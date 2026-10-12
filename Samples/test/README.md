---
title: Sample workbooks for trying the import screen
version: 1.0
last_updated: 2026-10-12
generator: Scripts/samples/make_samples.py
---

# Sample workbooks

Files `01`-`09` in this folder (`Samples/test/`) are made by `python Scripts/samples/make_samples.py` (needs `pip install openpyxl`; output is
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
