<p align="center">
  <img src="src/ExcelSearch/Assets/logo-512.png" alt="ExcelSearch logo" width="160">
</p>

# ExcelSearch

A Windows desktop app (WPF) for importing bank-transaction Excel sheets (including bank statements and cash deposits with no pay order number) into SQL Server 2012 or newer (or a local SQLite file) and searching them.

**Status: working.** Two screens are built and tested:

- **Import** - choose an `.xlsx`, see exactly what would happen (new rows, rows already in the database, conflicts, repeats inside the file, possible duplicates, invalid rows), choose which conflicts overwrite the database, re-check against the database, export the invalid rows, then commit or discard. Files of 200,000+ rows are handled (streamed, staged in SQL Server, classified in SQL).
- **Local DB option** - pick **Local DB** in the Database selector (bottom of the left rail) and the whole Import screen runs on a local SQLite file instead of SQL Server (single machine, single user). The Query screen works there too. See "Local DB (SQLite)" below.
- **Query** - search the imported transactions: date range, customer name, CNIC, application / pay order / reference / slip numbers (starts-with or exact; the CNIC filter also finds a CNIC written in the narrations), bank / project / dealer / branch / data-source lists, amount ranges and import batch. Server-side paging and sorting (click any column header; every column sorts), totals for the whole result, row details with the import it came from, and export to Excel or CSV (selected columns or all 30 fields). A new page always starts at the top of the grid.

## Solution layout

```
ExcelSearch.sln
src/
├── ExcelSearch/         WPF app: MVVM (CommunityToolkit.Mvvm), views, theme, DI host, appsettings
├── ExcelSearch.Core/    class library: Excel parser (ExcelDataReader), staging and query interfaces, filters, exporters
├── ExcelSearch.Data/    class library: EF Core model, SqlServer/ (staging store, query service) and Sqlite/ (staging store, schema initializer)
└── ExcelSearch.Tests/   xUnit tests (net8.0-windows so it can reference the WPF project)
Scripts/
├── db/                  T-SQL: 001-004, 006, 007 numbered scripts, init_db.sql (all of them in one), 005 (draft indexes, not applied); sqlite/ the SQLite schema (001-003); rollback/ the tested undo scripts
├── run/                 launcher scripts (run.ps1, run-dev.cmd, run-release.cmd)
├── publish.ps1          builds the framework-dependent win-x64 package into publish/ (gitignored)
└── samples/             make_samples.py - generates the sample workbooks
Samples/                 original bank-format examples; Samples/test/ holds the generated test workbooks and a manual checklist
Docs/quickstart.md       development setup for a fresh clone: requirements, how to check and install them, first run, tests
Docs/Deployment.md       publishing, production connection string, new database, first-run checklist, backup
Docs/Performance.md      performance report (import pipeline, search, export; SQL Server 2012 server timings in section 12)
Docs/KeylessImport.md    importing bank statements without pay order numbers: how it worked before, what changed and why, how to revert
db.md                    database schema documentation and change history
AGENTS.md                guidance for coding agents (CLAUDE.md points to it)
```

The WPF project references Core and Data. Core and Data never reference the WPF project.

## Tech stack

- .NET 8 (`net8.0-windows` for the app and tests, `net8.0` for the libraries)
- WPF with MVVM (CommunityToolkit.Mvvm), `Microsoft.Extensions.Hosting` for configuration and dependency injection
- Entity Framework Core 8, `SqlBulkCopy` (SQL Server) and Microsoft.Data.Sqlite (local SQLite option)
- ExcelDataReader (reading `.xlsx`); a small forward-only writer for exports (ClosedXML is used in the tests only)
- xUnit

## Prerequisites

- Windows
- .NET 8 SDK (or newer; the target stays .NET 8) to build. To only RUN a published package, the .NET 8 Desktop Runtime (x64) or newer is enough (see [Docs/Deployment.md](Docs/Deployment.md))
- SQL Server **2012 or newer** (developed on Express 2022, production runs on SQL Server 2012 Express). The app avoids features newer than 2012 (`STRING_AGG`, `OPENJSON`, inline `INDEX` in `CREATE TABLE`); EF Core runs at compatibility level 110. The database must have `READ_COMMITTED_SNAPSHOT` on; `init_db.sql` does that. A 2012 server at the original release only offers TLS 1.0; apply its service pack to get TLS 1.2. Not needed for the local SQLite option.
- `dotnet-ef` only to re-scaffold the model: `dotnet tool install --global dotnet-ef`
- Python with `openpyxl` only to regenerate the sample workbooks

## Getting started

1. **Create the database.** Run `Scripts/db/init_db.sql` (for example in SSMS or `sqlcmd`). It creates the `ExcelSearch` database if it is missing, then all tables, indexes and the RCSI setting (scripts 001-004, 006 and 007). It is safe to re-run and also upgrades an older database. Edit the database name on the lines marked `<<< EDIT` if you want another one. Close other connections first: the RCSI step disconnects other sessions. The numbered scripts `001`-`004` are the same thing in steps, kept as history. See [db.md](db.md) for the schema.
2. **Configure the connection string.** Settings files live in `src/ExcelSearch/`:

   | File | In git? | Used when |
   |---|---|---|
   | `appsettings.json` | yes | Always loaded first (connection string left empty) |
   | `appsettings.Development.json` | no (gitignored) | Debug builds (Visual Studio, `dotnet run`); never copied into a Release build |
   | `appsettings.Production.json` | no (gitignored, holds the password) | Release builds and the published package |
   | `appsettings.Production.sample.json`, `*.json.example` | yes | Templates to copy |

   Copy `appsettings.Development.json.example` to `appsettings.Development.json` (and `appsettings.Production.sample.json` to `appsettings.Production.json`) and fill in your server, user and password. The key is `ConnectionStrings:DefaultConnection`. An optional top-level `CommitLockTimeoutSeconds` (default 120) sets how long a commit waits for another commit. Never commit the real files.
3. **Build and run.**
   ```
   dotnet build ExcelSearch.sln
   dotnet run --project src/ExcelSearch
   ```
   or use `Scripts\run\run-dev.cmd` / `run-release.cmd`. If the connection fails, a dialog names the settings file and key to fix (a down server can take about 15 seconds to time out); the window stays open so you can switch to Local DB. To deploy a package to other machines, see [Docs/Deployment.md](Docs/Deployment.md) (`Scripts\publish.ps1`).

Debug builds run in the `Development` environment; any other build defaults to `Production`.

## Local DB (SQLite)

The quickest way: use the **Database** selector at the bottom of the left rail (SQL Server / Local DB). Choosing the other one asks for confirmation and restarts the app; the choice is remembered per Windows user in `%LocalAppData%\ExcelSearch\user-settings.json` and overrides appsettings. The two databases are separate; nothing is copied between them.

Or, to set it in configuration instead, add this to `appsettings.Development.json` / `appsettings.Production.json` (see `appsettings.Sqlite.json.example`) and restart:

```json
"Database": { "Provider": "Sqlite", "SqlitePath": "%LocalAppData%\\ExcelSearch\\excelsearch.db" }
```

`SqlitePath` is optional (that value is the default). On first start the app creates the folder and the file and applies the scripts in `Scripts/db/sqlite/` (001-003); nothing needs to be installed. Import (stage, preview, conflicts, re-check, commit, discard, invalid-row export) and the Query screen work exactly as on SQL Server (text search is case-insensitive for ASCII letters; see [db.md](db.md)). SQL Server remains the default when `Database:Provider` is missing.

Limits: one machine and one user, and **never put the file on a network share or a synced folder**. The type mapping (money as integer cents, dates as ISO text, case-insensitive keys) and every difference from SQL Server are in [db.md](db.md).

## Publishing

`Scripts\publish.ps1` makes a framework-dependent, non-single-file win-x64 package in `publish/` that uses `appsettings.Production.json`. To point it at the real server, change that file's one connection-string value and run the script. The file holds the `sa` password: keep it out of git and restrict who can read the deployment folder. The full procedure, the production database setup order and a first-run checklist are in [Docs/Deployment.md](Docs/Deployment.md).

## Trying it with sample data

`Samples/test/` has generated workbooks for every case the Import screen handles (all new, invalid rows, duplicates and conflicts, repeats, missing header, large file for paging, ...) and `10_query_demo_3000_rows.xlsx` for the Query screen. `Samples/test/README.md` lists what each file does, the expected counts, and a manual checklist for both screens. Committing a sample writes real rows to the database you point the app at. Regenerate them with `python Scripts/samples/make_samples.py` (or `... 10` for just the query demo).

## Tests

```
dotnet test src/ExcelSearch.Tests --filter "Category!=Integration&Category!=Large"   # Unit: 265 tests, no SQL Server needed (about 10 s)
dotnet test src/ExcelSearch.Tests --filter "Category=Integration&Category!=Large"    # Integration: 79 tests, needs the SQL Server dev database (about 20-30 s)
```

The SQL Server (Integration) tests run against the database in the environment variable `EXCELSEARCH_TEST_CONNECTION` and nowhere else (they skip when it is unset, and refuse a database whose name contains "prod"; the app configuration is never read). The staging/commit/re-check/export tests are written once and also run against a temporary SQLite file (one fresh file per test class, deleted afterwards; those do not need SQL Server and are not skipped by the variable below). Every test uses unique `TEST_<guid>` keys, scopes its work to its own import batch, and deletes only the rows it created. `EXCELSEARCH_SKIP_DB_TESTS=1` skips them; the large tests (200,000-row import, 500,000-row Query run) also need `EXCELSEARCH_RUN_LARGE=1` and print their timings. Results are summarized in [Docs/Performance.md](Docs/Performance.md).

## Database model

Three tables: `ImportBatches` (one row per imported file), `Transactions` (one row per Excel data row) and `ImportStaging` (rows of a file that has been loaded but not yet committed).

- The primary key of `Transactions` is a surrogate `Id` (GUID). **PayOrderNo and TxnRefSeqNo are optional**: bank statements for cash deposits have neither. Rows that have **both** are still unique on that pair (a filtered unique index), and an import tells an exact duplicate (same key, same hash) from a conflict (same key, different hash). Rows without them can repeat; an import **flags** the ones that look like duplicates and imports them anyway unless **Skip flagged duplicates** is ticked. Recognition runs in tiers: identical content (already in the database / repeated in the file); a row with only a **Txn Ref** (UBL statements) by that ref plus bank and account; a cash row with no reference (HBL statements) by slip number, then by value date + amount + branch code (not when both rows carry different slip numbers). The Possible-duplicates tab has a **Why flagged** column. Branch code (`Br. Code`, up to 50 characters) is read from `/Br: nnnn` in Narration 1 when the column is empty. Details: [Docs/KeylessImport.md](Docs/KeylessImport.md).
- `SlipNo` (from Narration 1) and `NarrationCnic` (a 13-digit CNIC in Narrations 2-5) are extracted at import for searching and for the duplicate hint. They are hints: the narrations are typed by hand.
- Each row stores a `RowHash` (SHA-256 of the business columns) for those comparisons.
- Identifiers such as CNIC and account numbers are stored as text so leading zeros survive; `Cnic` is digits only.
- Commits are serialized with an application lock; discard and commit cannot race.

Full column list, indexes and history: [db.md](db.md).

## Re-scaffolding the EF Core model

If the schema changes, update the numbered script, `init_db.sql` and [db.md](db.md), then regenerate the entities from the live database (replace the connection string with yours):

```
dotnet ef dbcontext scaffold "<connection string>" Microsoft.EntityFrameworkCore.SqlServer ^
  --project src/ExcelSearch.Data --startup-project src/ExcelSearch ^
  --context AppDbContext --context-dir . --output-dir Entities ^
  --namespace ExcelSearch.Data.Entities --context-namespace ExcelSearch.Data ^
  --table dbo.ImportBatches --table dbo.Transactions --table dbo.ImportStaging --no-onconfiguring --force
```

## Notes

- `AppDbContext` is registered through `AddDbContextFactory` (a desktop app has no request scope); each operation creates its own short-lived context.
- Index ideas for larger data sets are in `Scripts/db/005_query_indexes.sql`. It is a draft: every statement is commented out and nothing in it has been applied.

## License

Proprietary, all rights reserved (see [LICENSE](LICENSE)). Third-party packages keep their own licences.

## Credits

**Developed By:**

- Rayder-23
