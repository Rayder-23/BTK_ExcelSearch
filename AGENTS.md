# AGENTS.md

This file provides guidance to every coding agent when working with code in this repository. CLAUDE.md points here; every coding agent reads this file first.

## Project status

Early scaffold of a WPF app that will import bank-transaction Excel sheets into SQL Server and search them. The working feature is the Import screen (MVVM, CommunityToolkit.Mvvm): `MainWindow` is a shell with a left nav list (Import active, Query a disabled placeholder) and a ContentControl bound to `CurrentViewModel`; `ImportViewModel` (states Idle/Staging/Preview/Committing/Done) drives choose file, progress/cancel, a five-tab paged preview, commit, discard/start over and close handling. ViewModels are in `src/ExcelSearch/ViewModels`, Views in `Views`, the shared look (palette, fonts, banded `LedgerGrid`, buttons) in `Themes/Theme.xaml`, logo/icon files in `Assets/` (the original logo stays in the repo root), and they depend only on `IImportPreviewService` and `IDialogService` (so tests use fakes). `ExcelSearch.Core/Import` holds a streaming Excel parser (ExcelDataReader, xlsx only) that yields normalized, validated rows lazily; `ExcelSearch.Tests` (xUnit, net8.0-windows so it can reference the WPF project) covers it. `ExcelSearch.Core/Staging` (`IStagingStore`, `ImportPreviewService`) and `ExcelSearch.Data/SqlServerStagingStore` stage a parsed file into `ImportStaging` and classify it in SQL. `CommitAsync` (same store) writes a staged batch into `Transactions` in one transaction under the `ExcelSearch.Commit` application lock, then deletes its staging rows; `DiscardAsync` first flips a `Staged` batch to `Discarded` (refusing committed/discarded ones) and commit holds an UPDLOCK on the batch row, so the two cannot race; the commit lock wait is the `CommitLockTimeoutSeconds` setting (default 120). The database runs with READ_COMMITTED_SNAPSHOT on (`Scripts/db/004_enable_rcsi.sql`). `CleanupStaleBatchesAsync` runs at startup. The Conflict tab shows what differs from the database row (`GetConflictPageAsync`, `ConflictDiff`); the Conflicts tab has a per-row Overwrite checkbox (saved immediately via `SetConflictOverwriteAsync`) plus Overwrite all / Skip all and a database-read count; `RefreshAgainstDatabaseAsync` re-compares New / Already-in-database / Conflict rows with `Transactions` under the batch-row lock; `InvalidRowExporter` writes the Invalid rows (original cell values + Source Row + Errors) with the forward-only `StreamingXlsxWriter` (MiniExcel was tried and removed: it buffers the whole sheet in memory). `ErrorCellScanner` finds Excel error values (`#DIV/0!`) that ExcelDataReader reports as null. Sample workbooks are generated into `Samples/test/` by `Scripts/samples/make_samples.py`. There is no linter.

## Commands

```
dotnet build ExcelSearch.sln
dotnet run --project src/ExcelSearch
dotnet test src/ExcelSearch.Tests
```

Database tests run against the single development database using the app's own configuration. Every test uses unique `TEST_<guid>` keys and deletes only the rows it created (never truncate or reseed). Set `EXCELSEARCH_SKIP_DB_TESTS=1` to skip them; the 200,000-row test also needs `EXCELSEARCH_RUN_LARGE=1`.

Re-scaffold the EF model after a schema change (generated code is not hand-edited; use your real connection string):

```
dotnet ef dbcontext scaffold "<connection string>" Microsoft.EntityFrameworkCore.SqlServer --project src/ExcelSearch.Data --startup-project src/ExcelSearch --context AppDbContext --context-dir . --output-dir Entities --namespace ExcelSearch.Data.Entities --context-namespace ExcelSearch.Data --table dbo.ImportBatches --table dbo.Transactions --table dbo.ImportStaging --no-onconfiguring --force
```

`dotnet-ef` is installed as a global tool. Pass the connection string on the command line: the WPF project's host config is not available to `dotnet ef` at design time.

## Architecture

- `src/ExcelSearch` (net8.0-windows, WPF) references `ExcelSearch.Core` and `ExcelSearch.Data` (both net8.0). Core and Data must never reference the WPF project.
- Startup is in `App.xaml.cs`, not `StartupUri`: it builds a generic host, registers the context factory, parser, staging store and `MainWindow` in DI, and shows the window from the host. Windows get their dependencies by constructor injection.
- `AppDbContext` is registered through `AddDbContextFactory` (a singleton factory); consumers inject `IDbContextFactory<AppDbContext>` and create a short-lived context per operation. Do not inject a scoped `AppDbContext` into windows: the Development environment validates scopes and the app would crash on startup.
- `ExcelSearch.Data` holds scaffolded output only (`AppDbContext.cs`, `Entities/`). Add customisations through the generated partial hooks (`OnModelCreatingPartial`, `partial class` entities), or they are lost on the next `--force` scaffold.
- EF Core and Hosting package versions must stay on the 8.x line to match the target framework.

## Configuration

- Connection string key: `ConnectionStrings:DefaultConnection`.
- `appsettings.json` is committed with an empty value. `appsettings.Development.json` and `appsettings.Production.json` hold real credentials and are **gitignored**; the committed `*.json.example` files are templates. Never commit the real ones.
- `App.xaml.cs` forces the `Development` environment under `#if DEBUG`; other builds default to `Production`. Local development uses SQL Server Express (`.\SQLEXPRESS`, database `ExcelSearch`) with the credentials in `appsettings.Development.json`.

## Database

`Scripts/db/001_create_tables.sql` is the source of truth for the schema (re-runnable; creates tables only if missing). `db.md` documents the live schema and keeps a dated change history. Keep the script, the scaffolded model and `db.md` in sync, and follow `db.md`'s maintenance rules (bump its Version and Last Updated, add a dated note for every change, mark removed tables `[REMOVED]` rather than deleting them).

Design decisions that code must respect:
- `Transactions` primary key is the business key `(PayOrderNo, TxnRefSeqNo)`, so duplicates are rejected by the database (error 2627). `Transactions.Id` is a separate unique GUID that the app supplies; it is not the PK.
- `RowHash` (SHA-256 hex of the normalized business columns) drives import behaviour: same key and same hash is an exact duplicate (skip); same key and different hash is a conflict (user decides). `ImportBatches` counters (`NewRows`, `SkippedRows`, `UpdatedRows`, `RejectedRows`) mirror those outcomes.
- Identifiers (CNIC, account numbers, references) are text, not numbers, so leading zeros survive; `Cnic` is stored digits-only. Amounts are `decimal(18,2)`.
- The database collation is case-insensitive, so keys differing only by case collide.
- Some source Excel headers are misspelled (`Delear Name`, `Narraction 1`); the header-to-column mapping is in `db.md` and the script header.

## Environment gotchas

- The repo path contains `[D]` (`D:\Ry Work [D]\...`). PowerShell cmdlets treat brackets as wildcards, so `Start-Process -WorkingDirectory` and non-literal `-Path` arguments fail on it. Use `-LiteralPath` or .NET APIs (`System.Diagnostics.Process`).
- `sqlcmd` is available for queries. Use `-C` to trust the server certificate.
- The WPF app can be driven headlessly with UI Automation (controls have `AutomationId`s such as `ChooseFileButton`, `CommitButton`, `DiscardButton`, `RowsGrid`). The OpenFileDialog and message boxes cannot be driven that way.

## Git conventions

Commit subjects use a prefix: `Add:`, `Update:`, `Fix:`, `Refactor:`, `Docs:`, `Chore:`, etc., followed by a short imperative summary, with a bullet-point body explaining why. Commits and pushes are made only when asked.