# Quickstart: development setup

For a coding agent or developer who has just cloned the repository. It gets you from a clean checkout to a building app and passing
tests. Publishing a package for other machines is a different job: see [Deployment.md](Deployment.md). Commands, architecture and rules
are in [AGENTS.md](../AGENTS.md); the schema is in [db.md](../db.md).

Two ways to run, pick what you need:

- **Local DB (SQLite)**: no server, nothing to install beyond the SDK. Enough for most work on the Import and Query screens.
- **SQL Server**: needed for the SQL Server code paths and for the Integration test tier.

## 1. Requirements and how to check them

| Requirement | Needed for | Check | Set up |
|---|---|---|---|
| Windows 10/11 | everything (WPF) | `winver` | none |
| .NET SDK 8 or newer | build, run, test | `dotnet --version` (8.x or higher) | <https://dotnet.microsoft.com/download/dotnet/8.0> or `winget install Microsoft.DotNet.SDK.8` |
| .NET Desktop Runtime | running the app (included with the SDK) | `dotnet --list-runtimes` shows `Microsoft.WindowsDesktop.App` | comes with the SDK |
| Git | clone, commit | `git --version` | `winget install Git.Git` |
| SQL Server (Express is fine) | SQL Server mode, Integration tests | `sqlcmd -S .\SQLEXPRESS -E -C -Q "SELECT @@VERSION"` | install SQL Server Express 2022 and enable TCP and the `sa` login (or use Windows authentication) |
| `sqlcmd` | running the database scripts from a shell | `sqlcmd -?` | `winget install Microsoft.Sqlcmd`, or use SSMS instead |
| `dotnet-ef` | only to re-scaffold the EF model | `dotnet ef --version` | `dotnet tool install --global dotnet-ef` (keep it on the 8.x line) |
| Python 3 + `openpyxl` | only to regenerate the sample workbooks | `python -c "import openpyxl"` | `pip install openpyxl` |

An agent without a Windows desktop can still build the libraries (`ExcelSearch.Core`, `ExcelSearch.Data`) on any OS with .NET 8, but the
WPF app and the test project target `net8.0-windows`, so building and running everything needs Windows.

## 2. Clone, restore, build

```
git clone https://github.com/Rayder-23/BTK_ExcelSearch.git
cd BTK_ExcelSearch
dotnet build ExcelSearch.sln
```

The folder may have a `[D]` in its path on the author's machine (`D:\Ry Work [D]\...`). That breaks PowerShell wildcard handling: use
`-LiteralPath` or .NET APIs, or just clone to a path without brackets.

## 3. Run the tests that need nothing (Unit tier)

```
dotnet test src/ExcelSearch.Tests --filter "Category!=Integration&Category!=Large"
```

About 240 tests in under 10 seconds. They use no server; the SQLite halves of the contract tests use a temporary file. If this passes,
the toolchain is fine.

## 4. Run the app with Local DB (no server)

Settings files are in `src/ExcelSearch/`. Copy the template and run:

```
copy src\ExcelSearch\appsettings.Sqlite.json.example src\ExcelSearch\appsettings.Development.json
dotnet run --project src/ExcelSearch
```

Debug builds use the Development environment, so `appsettings.Development.json` is loaded. The database file appears at
`%LocalAppData%\ExcelSearch\excelsearch.db` with the schema created automatically. You can also choose "Local DB" in the Database
selector at the bottom left of the running app.

Note: the in-app choice is saved in `%LocalAppData%\ExcelSearch\user-settings.json` and overrides every appsettings file and environment
variable. If a setting seems ignored, look there.

## 5. Set up SQL Server mode (optional)

1. Create the database and tables: run `Scripts/db/init_db.sql` (SSMS, or `sqlcmd -S .\SQLEXPRESS -E -C -i Scripts/db/init_db.sql`).
   It is safe to re-run. Close other connections first; the READ_COMMITTED_SNAPSHOT step disconnects other sessions.
2. Verify the collation is case-insensitive (the name contains `_CI_`):
   `sqlcmd -S .\SQLEXPRESS -E -C -d ExcelSearch -Q "SELECT DATABASEPROPERTYEX('ExcelSearch','Collation')"`
3. Create `src/ExcelSearch/appsettings.Development.json` from `appsettings.Development.json.example` and fill in the server, user and
   password. The key is `ConnectionStrings:DefaultConnection`. This file is gitignored: never commit it or print its contents.
4. Run the app (`dotnet run --project src/ExcelSearch`). If the connection fails, a dialog names the file and key to fix.

## 6. Run the Integration tests (SQL Server)

They use ONLY the environment variable `EXCELSEARCH_TEST_CONNECTION` (not the app configuration), skip when it is unset, and refuse a
database whose name contains "prod". Point it at the development database, without echoing it:

```powershell
$env:EXCELSEARCH_TEST_CONNECTION = '<the development connection string>'
dotnet test src/ExcelSearch.Tests --filter "Category=Integration&Category!=Large"
```

Tests create and delete only their own `TEST_<guid>` rows. The Large tier (200,000 and 500,000 rows) additionally needs
`EXCELSEARCH_RUN_LARGE=1`.

## 7. Optional extras

- Sample workbooks for trying the Import and Query screens are already in `Samples/test/`, with a manual checklist in
  `Samples/test/README.md`. To regenerate them: `python Scripts/samples/make_samples.py`.
- After a schema change, update the SQL scripts, the SQLite script, `db.md` and re-scaffold the EF model (command in AGENTS.md).

## Quick troubleshooting

| Symptom | Likely cause |
|---|---|
| Build fails: the exe is locked | a running `ExcelSearch.exe`; close it or build to another output folder |
| Integration tests all show "skipped" | `EXCELSEARCH_TEST_CONNECTION` is not set in that shell |
| Integration tests fail with "Refusing to run" | the database name contains "prod"; use the development database |
| App uses SQL Server although you set `Database__Provider=Sqlite` | `user-settings.json` overrides environment variables; change the selector in the app or delete that file |
| "could not connect to SQL Server" dialog | wrong server, login or `TrustServerCertificate` in the connection string; the dialog names the file |
| PowerShell cannot find the repo path | the `[D]` in the path; use `-LiteralPath` |
