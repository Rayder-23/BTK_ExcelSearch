<p align="center">
  <img src="src/ExcelSearch/Assets/logo-512.png" alt="ExcelSearch logo" width="160">
</p>

# ExcelSearch

A Windows desktop app (WPF) for importing bank-transaction Excel sheets into SQL Server (or a local SQLite file) and searching them.

**Status: working.** Two screens are built and tested:

- **Import** - choose an `.xlsx`, see exactly what would happen (new rows, rows already in the database, conflicts, repeats inside the file, invalid rows), choose which conflicts overwrite the database, re-check against the database, export the invalid rows, then commit or discard. Files of 200,000+ rows are handled (streamed, staged in SQL Server, classified in SQL).
- **Local DB option** - pick **Local DB** in the Database selector (bottom of the left rail) and the whole Import screen runs on a local SQLite file instead of SQL Server (single machine, single user). The Query screen is not available on Local DB yet. See "Local DB (SQLite)" below.
- **Query** - search the imported transactions: date range, customer name, CNIC, application / pay order / reference numbers (starts-with or exact), bank / project / dealer / branch / data-source lists, amount ranges and import batch. Server-side paging and sorting, totals for the whole result, row details with the import it came from, and export to Excel or CSV (selected columns or all 28 fields).

## Solution layout

```
ExcelSearch.sln
src/
├── ExcelSearch/         WPF app: MVVM (CommunityToolkit.Mvvm), views, theme, DI host, appsettings
├── ExcelSearch.Core/    class library: Excel parser (ExcelDataReader), staging and query interfaces, filters, exporters
├── ExcelSearch.Data/    class library: EF Core model, SqlServer/ (staging store, query service) and Sqlite/ (staging store, schema initializer)
└── ExcelSearch.Tests/   xUnit tests (net8.0-windows so it can reference the WPF project)
Scripts/
├── db/                  T-SQL: 001-004 numbered scripts, init_db.sql (all of them in one), 005 (draft indexes, not applied); sqlite/ the SQLite schema
├── run/                 launcher scripts (run.ps1, run-dev.cmd, run-release.cmd)
└── samples/             make_samples.py - generates the sample workbooks
Samples/                 original bank-format examples; Samples/test/ holds the generated test workbooks and a manual checklist
docs/Performance.md      performance report (import pipeline, search, export)
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
- .NET 8 SDK (or newer; the target stays .NET 8)
- SQL Server (set up against SQL Server Express 2022). The database must have `READ_COMMITTED_SNAPSHOT` on; `init_db.sql` does that. Not needed for the local SQLite option.
- `dotnet-ef` only to re-scaffold the model: `dotnet tool install --global dotnet-ef`
- Python with `openpyxl` only to regenerate the sample workbooks

## Getting started

1. **Create the database.** Run `Scripts/db/init_db.sql` (for example in SSMS or `sqlcmd`). It creates the `ExcelSearch` database if it is missing, then all tables, indexes and the RCSI setting. It is safe to re-run. Edit the database name on the lines marked `<<< EDIT` if you want another one. Close other connections first: the RCSI step disconnects other sessions. The numbered scripts `001`-`004` are the same thing in steps, kept as history. See [db.md](db.md) for the schema.
2. **Configure the connection string.** Settings files live in `src/ExcelSearch/`:

   | File | In git? | Used when |
   |---|---|---|
   | `appsettings.json` | yes | Always loaded first (connection string left empty) |
   | `appsettings.Development.json` | no (gitignored) | Debug builds |
   | `appsettings.Production.json` | no (gitignored) | Release builds |
   | `*.json.example` | yes | Templates to copy |

   Copy `appsettings.Development.json.example` to `appsettings.Development.json` (and the Production one likewise) and fill in your server, user and password. The key is `ConnectionStrings:DefaultConnection`. An optional top-level `CommitLockTimeoutSeconds` (default 120) sets how long a commit waits for another commit. Never commit the real files.
3. **Build and run.**
   ```
   dotnet build ExcelSearch.sln
   dotnet run --project src/ExcelSearch
   ```
   or use `Scripts\run\run-dev.cmd` / `run-release.cmd`. If the connection fails, the screen shows the error (a down server can take about 15 seconds to time out).

Debug builds run in the `Development` environment; any other build defaults to `Production`.

## Local DB (SQLite)

The quickest way: use the **Database** selector at the bottom of the left rail (SQL Server / Local DB). Choosing the other one asks for confirmation and restarts the app; the choice is remembered per Windows user in `%LocalAppData%\ExcelSearch\user-settings.json` and overrides appsettings. The two databases are separate; nothing is copied between them.

Or, to set it in configuration instead, add this to `appsettings.Development.json` / `appsettings.Production.json` (see `appsettings.Sqlite.json.example`) and restart:

```json
"Database": { "Provider": "Sqlite", "SqlitePath": "%LocalAppData%\\ExcelSearch\\excelsearch.db" }
```

`SqlitePath` is optional (that value is the default). On first start the app creates the folder and the file and applies `Scripts/db/sqlite/001_create_schema.sql`; nothing needs to be installed. Import (stage, preview, conflicts, re-check, commit, discard, invalid-row export) works exactly as on SQL Server; the Query item is greyed out ("not yet available for Local DB"). SQL Server remains the default when `Database:Provider` is missing.

Limits: one machine and one user, and **never put the file on a network share or a synced folder**. The type mapping (money as integer cents, dates as ISO text, case-insensitive keys) and every difference from SQL Server are in [db.md](db.md).

## Trying it with sample data

`Samples/test/` has generated workbooks for every case the Import screen handles (all new, invalid rows, duplicates and conflicts, repeats, missing header, large file for paging, ...) and `10_query_demo_3000_rows.xlsx` for the Query screen. `Samples/test/README.md` lists what each file does, the expected counts, and a manual checklist for both screens. Committing a sample writes real rows to the database you point the app at. Regenerate them with `python Scripts/samples/make_samples.py` (or `... 10` for just the query demo).

## Tests

```
dotnet test src/ExcelSearch.Tests
```

Database tests run against the single development database from the same configuration as the app. The staging/commit/re-check/export tests are written once and also run against a temporary SQLite file (one fresh file per test class, deleted afterwards; those do not need SQL Server and are not skipped by the variable below). Every test uses unique `TEST_<guid>` keys, scopes its work to its own import batch, and deletes only the rows it created. `EXCELSEARCH_SKIP_DB_TESTS=1` skips them; the large tests (200,000-row import, 500,000-row Query run) also need `EXCELSEARCH_RUN_LARGE=1` and print their timings. Results are summarized in [docs/Performance.md](docs/Performance.md).

## Database model

Three tables: `ImportBatches` (one row per imported file), `Transactions` (one row per Excel data row) and `ImportStaging` (rows of a file that has been loaded but not yet committed).

- The primary key of `Transactions` is the business key **(PayOrderNo, TxnRefSeqNo)**, so a duplicate row is rejected by the database.
- `Transactions.Id` is a separate unique GUID filled by a column default.
- Each row stores a `RowHash` (SHA-256 of the business columns) so an import can tell an exact duplicate (same key, same hash) from a conflict (same key, different hash).
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

## Credits

**Developed By:**

- Rayder-23
