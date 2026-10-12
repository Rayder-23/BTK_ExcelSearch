---
title: Sample workbooks for trying the import screen
version: 1.0
last_updated: 2026-10-12
generator: Scripts/samples/make_samples.py
---

# Sample workbooks

Files `01`-`08` in this folder (`Samples/test/`) are made by `python Scripts/samples/make_samples.py` (needs `pip install openpyxl`; output is
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
- Stage `02`, then **Discard**, **Start over**, or close the window (Yes / No / Cancel).
- Stage the same file in two app windows and commit one, then try to commit or discard in the other.
