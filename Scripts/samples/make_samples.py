"""Generates the sample workbooks in Samples/test/ for trying the import screen.

    python Scripts/samples/make_samples.py        (needs: pip install openpyxl)

Everything is deterministic (fixed seed), so re-running gives identical files. Keys use the prefixes PO-A- .. PO-F-
so a file's rows never collide with another file's, except where a file is meant to overlap (see Samples/README.md).
"""
import random
from datetime import datetime, timedelta
from pathlib import Path

from openpyxl import Workbook

OUT = Path(__file__).resolve().parents[2] / "Samples" / "test"

# Headers as they appear in the real bank files, including their misspellings ("Narraction 1", "Delear Name").
HEADERS = [
    "Value Date", "Br. Code", "Branch Name", "Reg No", "Application No", "Plot No", "Street No", "Challan No",
    "Customer Name", "Cheque/Inst No", "CNIC No", "payorder no", "Txn Ref/ Seq No", "Discount",
    "Down Payment Amount", "Debit", "Credit", "Run. Bal", "Bank Name", "Acct no", "Project", "Delear Name",
    "Name of Data", "Narraction 1", "Narration 2", "Narration 3", "Narration 4", "Narration 5",
]
COL = {h: i for i, h in enumerate(HEADERS)}

CUSTOMERS = ["Ahmed Khan", "Sara Malik", "Bilal Hussain", "Ayesha Siddiqui", "Usman Tariq", "Fatima Noor",
             "Hamza Qureshi", "Zainab Ali", "Imran Sheikh", "Hina Raza", "Kamran Butt", "Mehwish Anwar"]
BANKS = ["HBL", "UBL", "MCB", "Meezan Bank", "Allied Bank", "Bank Alfalah"]
PROJECTS = ["Bahria Town Karachi - Precinct 1", "Bahria Town Karachi - Precinct 10", "Bahria Paradise", "Bahria Sports City"]
BRANCHES = [("0101", "Clifton Branch"), ("0202", "Saddar Branch"), ("0303", "DHA Branch"), ("0404", "Gulshan Branch")]
DEALERS = ["Star Associates", "Prime Estate", "Zameen Traders", "Capital Realtors"]
BASE_DATE = datetime(2024, 4, 1)


def valid_row(prefix: str, n: int, rng: random.Random) -> dict:
    """One fully populated valid row; the key is f'PO-{prefix}-{n:04d}' / f'{n:03d}'."""
    br = BRANCHES[n % len(BRANCHES)]
    debit = round(rng.uniform(5_000, 450_000), 2)
    return {
        "Value Date": BASE_DATE + timedelta(days=n % 90),
        "Br. Code": br[0],                                   # text, leading zero kept
        "Branch Name": br[1],
        "Reg No": f"{n:05d}",
        "Application No": f"APP-{prefix}{n:04d}",
        "Plot No": f"{(n % 40) + 1}",
        "Street No": f"{(n % 12) + 1}",
        "Challan No": f"CH{rng.randint(100000, 999999)}",
        "Customer Name": CUSTOMERS[n % len(CUSTOMERS)],
        "Cheque/Inst No": f"{rng.randint(1000000, 9999999)}",
        "CNIC No": f"35202-{rng.randint(1000000, 9999999)}-{rng.randint(1, 9)}",   # dashes are stripped on import
        "payorder no": f"PO-{prefix}-{n:04d}",
        "Txn Ref/ Seq No": f"{n:03d}",
        "Discount": 0 if n % 5 else round(debit * 0.02, 2),
        "Down Payment Amount": round(debit * 0.25, 2),
        "Debit": debit,
        "Credit": 0,
        "Run. Bal": round(1_000_000 + n * 1_250.5, 2),
        "Bank Name": BANKS[n % len(BANKS)],
        "Acct no": f"{rng.randint(10**9, 10**10 - 1)}",
        "Project": PROJECTS[n % len(PROJECTS)],
        "Delear Name": DEALERS[n % len(DEALERS)],
        "Name of Data": "Sample data",
        "Narraction 1": f"Installment {n % 12 + 1} of 12",
        "Narration 2": "Online transfer" if n % 2 else "Cheque deposit",
        "Narration 3": None, "Narration 4": None, "Narration 5": None,
    }


def write(name: str, rows: list, headers=HEADERS, sheet="Data", top_rows=0, header_override=None) -> None:
    """rows are dicts keyed by header name. top_rows adds that many title lines above the header row."""
    wb = Workbook()
    ws = wb.active
    ws.title = sheet
    for i in range(top_rows):
        ws.cell(row=i + 1, column=1, value=["Bahria Town - Collections report", "Printed 12-Oct-2026", "", ][i] if i < 3 else "")
    header_row = top_rows + 1
    shown = header_override or headers
    for c, h in enumerate(shown, 1):
        ws.cell(row=header_row, column=c, value=h)
    for r, row in enumerate(rows, header_row + 1):
        if row is None:
            continue   # a gap: a completely blank row (the importer skips these silently)
        for c, h in enumerate(headers, 1):
            v = row.get(h)
            cell = ws.cell(row=r, column=c, value=v)
            if isinstance(v, datetime):
                cell.number_format = "dd/mm/yyyy"
    for c in range(1, len(shown) + 1):
        ws.column_dimensions[ws.cell(row=header_row, column=c).column_letter].width = 18
    wb.save(OUT / name)
    print(f"{name}: {sum(1 for r in rows if r is not None)} data rows")


def f01_perfect() -> list:
    rng = random.Random(101)
    rows = [valid_row("A", n, rng) for n in range(1, 41)]
    # Dates are accepted as real Excel dates and as text in the formats the importer knows.
    rows[6]["Value Date"] = "07/04/2024"
    rows[13]["Value Date"] = "2024-04-14"
    rows[20]["Value Date"] = "21-Apr-2024"
    rows[27]["Value Date"] = "28-4-2024"
    rows[8]["CNIC No"] = "3520212345671"                    # without dashes
    rows[9]["Credit"] = 15_000                              # a credit row
    rows[9]["Debit"] = 0
    return rows


def f02_invalid() -> list:
    rng = random.Random(202)
    rows = [valid_row("B", n, rng) for n in range(1, 13)]   # 12 good rows (New)

    def bad(n, **changes):
        r = valid_row("B", n, rng)
        r.update(changes)
        return r

    rows += [
        bad(13, **{"payorder no": None}),                                          # missing PayOrderNo
        bad(14, **{"Txn Ref/ Seq No": None}),                                      # missing Txn Ref
        bad(15, **{"Value Date": None}),                                           # missing Value Date
        bad(16, **{"Value Date": "31/31/2024"}),                                   # impossible date
        bad(17, **{"Value Date": "April 3rd"}),                                    # not a date
        bad(18, **{"Value Date": "2024/04/03"}),                                   # unsupported format
        bad(19, **{"Debit": "abc"}),                                               # not an amount
        bad(20, **{"Credit": "12,50x"}),                                           # not an amount
        bad(21, **{"Customer Name": "N" * 250}),                                   # longer than 200 characters
        bad(22, **{"payorder no": "PO-B-" + "9" * 60}),                            # key longer than 50 characters
        bad(23, **{"Br. Code": "B" * 30}),                                         # longer than 20 characters
        bad(24, **{"Run. Bal": "#DIV/0!"}),                                        # an Excel error value in the cell
        bad(25, **{"payorder no": None, "Txn Ref/ Seq No": None, "Value Date": None}),   # three errors on one row
    ]
    rows.append(None)                                                              # blank row: skipped silently
    # The same key twice with different values: both rows end up Invalid (a clash inside the file).
    a = valid_row("B", 26, rng)
    b = dict(a)
    b["Debit"] = a["Debit"] + 1_000
    rows += [a, b]
    rows += [valid_row("B", n, rng) for n in range(27, 31)]                        # 4 more good rows
    return rows


def f03_reimport_of_01() -> list:
    """Overlaps file 01: import 01 first, then this one."""
    rng = random.Random(101)
    base = [valid_row("A", n, rng) for n in range(1, 41)]   # regenerate file 01's rows exactly
    rows = [dict(r) for r in base]
    # file 01 contains a few text dates and a credit row; mirror those edits so the "same" rows are truly identical
    rows[6]["Value Date"] = "07/04/2024"
    rows[13]["Value Date"] = "2024-04-14"
    rows[20]["Value Date"] = "21-Apr-2024"
    rows[27]["Value Date"] = "28-4-2024"
    rows[8]["CNIC No"] = "3520212345671"
    rows[9]["Credit"] = 15_000
    rows[9]["Debit"] = 0

    out = rows[:15]                                          # 1-15: identical -> "Already in database"
    for i in range(15, 25):                                  # 16-25: same key, changed values -> Conflict
        r = dict(rows[i])
        if i % 2:
            r["Debit"] = r["Debit"] + 500
        else:
            r["Customer Name"] = r["Customer Name"] + " (corrected)"
        out.append(r)
    rng2 = random.Random(303)
    new_rows = [valid_row("C", n, rng2) for n in range(1, 9)]      # 8 brand-new keys -> New
    out += new_rows
    out += [dict(new_rows[0]), dict(new_rows[0])]                  # the first new row twice more -> Repeated in file
    out.append(dict(valid_row("C", 9, rng2), **{"Value Date": None}))   # one invalid row
    return out


def f04_repeats_in_file() -> list:
    rng = random.Random(404)
    rows = [valid_row("D", n, rng) for n in range(1, 21)]          # 20 distinct new rows
    rows += [dict(rows[2]) for _ in range(3)]                      # row 3 repeated 3 more times -> 3 Repeated in file
    rows += [dict(rows[9])]                                        # row 10 repeated once -> 1 Repeated in file
    clash = dict(valid_row("D", 21, rng))
    clash2 = dict(clash)
    clash2["Customer Name"] = "Someone Else"
    rows += [clash, clash2]                                        # same key, different values -> both Invalid
    return rows


def f05_missing_header() -> tuple:
    rng = random.Random(505)
    headers = [h for h in HEADERS if h != "payorder no"]
    return [valid_row("E", n, rng) for n in range(1, 11)], headers


def f06_paging() -> list:
    rng = random.Random(606)
    rows = [valid_row("F", n, rng) for n in range(1, 1051)]        # 1,050 new rows = 11 pages
    rows += [dict(rows[n]) for n in range(0, 120)]                 # 120 repeats = Repeated in file (2 pages)
    rows += [valid_row("F", 2000 + n, rng) | {"Value Date": None} for n in range(250)]   # 250 invalid = 3 pages
    return rows


def f08_header_not_first() -> tuple:
    rng = random.Random(808)
    rows = [valid_row("H", n, rng) for n in range(1, 11)]
    headers = [{"payorder no": "PAYORDER NO", "Txn Ref/ Seq No": "txn_ref_seq_no", "Value Date": "value date",
                "Narraction 1": "Narration1", "Delear Name": "Dealer Name"}.get(h, h) for h in HEADERS]
    return rows, headers


def main() -> None:
    OUT.mkdir(exist_ok=True)
    write("01_perfect_all_new.xlsx", f01_perfect())
    write("02_invalid_rows.xlsx", f02_invalid())
    write("03_reimport_of_01_duplicates_conflicts.xlsx", f03_reimport_of_01())
    write("04_repeated_rows_in_file.xlsx", f04_repeats_in_file())
    rows, headers = f05_missing_header()
    write("05_missing_required_header.xlsx", rows, headers=headers)
    write("06_large_for_paging.xlsx", f06_paging())
    (OUT / "07_not_really_excel.xlsx").write_text("This is plain text with an .xlsx name, not a workbook.\n", encoding="utf-8")
    print("07_not_really_excel.xlsx: text file")
    rows, shown = f08_header_not_first()
    # rows are keyed by the original header names; write() puts the renamed headers on row 4 (after 3 title rows)
    write("08_header_on_row_4_variant_headers.xlsx", rows, headers=HEADERS, top_rows=3, header_override=shown)


if __name__ == "__main__":
    main()
