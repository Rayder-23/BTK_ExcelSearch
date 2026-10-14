# ExcelSearch deployment

Version 1.0 · 13/10/2026

ExcelSearch is published as a **framework-dependent** win-x64 package (many files, not a single exe, no trimming). The package is built
by `Scripts/publish.ps1` into `publish/` and is copied to each machine as a folder.

## Decision: ReadyToRun is not enabled (2026-10-14)

The first start after a publish takes about 4-5 s because the code is compiled as it starts. ReadyToRun would precompile it, but the gain
is small (startup only; imports and queries run the same), and it makes the package 2-3 times larger and publishing slower. Decision: do
not enable it for now. Revisit if users complain about start-up time (`<PublishReadyToRun>true</PublishReadyToRun>` in the csproj).

## Requirement on every machine

**.NET 8 Desktop Runtime (x64), or a newer major version.** The project sets `RollForward=LatestMajor`, so a machine that only has .NET 9
or 10 Desktop Runtime still runs the app. Download: <https://dotnet.microsoft.com/download/dotnet/8.0> (".NET Desktop Runtime", x64).
Risk of roll-forward: a newer runtime has not been tested against this build; if something behaves oddly on such a machine, install 8.

A machine without the runtime shows the standard Windows dialog "To run this application, you must install .NET Desktop Runtime" with a
download link, and the app does not start.

## Switching to the real production connection string

1. Open `src/ExcelSearch/appsettings.Production.json` and change **one value**: `ConnectionStrings` → `DefaultConnection`.
2. Run `Scripts\publish.ps1` from the repository root. It publishes Release, win-x64, `--self-contained false` into `publish\`, and
   refuses to run if `appsettings.Production.json` is missing.
3. Copy the `publish\` folder to the machines.

Already deployed? You can instead edit `appsettings.Production.json` beside `ExcelSearch.exe` on that machine (plain text) and restart.
The environment variable `ConnectionStrings__DefaultConnection` overrides the file. `Encrypt` and `TrustServerCertificate` come only from
the connection string (a server with a self-signed certificate needs `TrustServerCertificate=True`).

`appsettings.Production.json` is **not in git** (it is gitignored); `appsettings.Production.sample.json` is the committed template.

> **Security.** `appsettings.Production.json` contains the `sa` password in plain text. Keep it out of git, and restrict who can read the
> deployment folder (NTFS permissions). Everyone who can read that file has full control of the database server.

Release builds always use the Production settings (and Debug builds the Development ones), whatever `DOTNET_ENVIRONMENT` says.

## Creating a new production database

Run in SQL Server Management Studio or `sqlcmd` (use `-C` to trust the certificate), in this order:

1. Create the database (for example `CREATE DATABASE ExcelSearch;`).
2. **Verify the collation is case-insensitive** (the name contains `_CI_`, for example `SQL_Latin1_General_CP1_CI_AS`):

   ```sql
   SELECT DATABASEPROPERTYEX('ExcelSearch', 'Collation') AS DatabaseCollation;
   ```

   The business key `(PayOrderNo, TxnRefSeqNo)` of rows that have both relies on it. If it is `_CS_`, drop the database and recreate it with
   `COLLATE SQL_Latin1_General_CP1_CI_AS`.
3. Run `Scripts/db/001_create_tables.sql`, then `002_create_staging.sql`, then `003_concurrency.sql`, then `006_surrogate_key.sql`
   (bank-statement sheets without pay order numbers need it: `Id` becomes the primary key). **An EXISTING production database that
   was built from 001-004 needs 006 too before this version of the app runs against it** (take a backup first; it is one re-runnable
   transaction and keeps every row). Undo: `Scripts/db/rollback/006_rollback.sql` (see `Docs/KeylessImport.md`).
4. Run `Scripts/db/004_enable_rcsi.sql` (READ_COMMITTED_SNAPSHOT) **only if no other application uses this database**: it changes how
   every application reading it behaves.

`Scripts/db/init_db.sql` is the same 001–004 and 006 in one script; do not use it when step 4 must be skipped. `005_query_indexes.sql` is a draft
and is not part of the setup.

## First-run checklist

- [ ] The .NET 8 Desktop Runtime (or newer) is installed.
- [ ] The `publish\` folder is copied in full, and `appsettings.Production.json` beside `ExcelSearch.exe` has the right connection string.
- [ ] The database exists with a case-insensitive collation and scripts 001–003 and 006 (and 004 if allowed) have run.
- [ ] Start `ExcelSearch.exe`. If a "could not connect to SQL Server" dialog appears, it names the file and key to fix.
- [ ] Import `Samples/test/*` small file: choose, preview, commit; then find the rows on the Query screen.
- [ ] Choose "Local DB" in the Database selector (bottom left); the app restarts and a file appears (see below).
- [ ] Only the intended people can read the deployment folder.

## Local DB (SQLite)

The default file is `%LocalAppData%\ExcelSearch\excelsearch.db` (per Windows user) in every configuration, Debug or Production. The folder
is created if missing; if it cannot be created or opened (permissions), the app shows a message and exits. `Database:SqlitePath` overrides
it. Never put the file on a network share or a synced folder (OneDrive, Dropbox).

**Upgrading from the first release:** the first start of this version upgrades an existing file in place (schema version 1 to 2, a table rebuild that
keeps every row). An older ExcelSearch refuses a version-2 file, so **copy the database first** if you may want to go back (see
`Docs/KeylessImport.md` for the rollback script).

**Backup:** close ExcelSearch, then copy `excelsearch.db`. While the app has run, `excelsearch.db-wal` and `excelsearch.db-shm` may sit
beside it; copy them with the `.db` file (or confirm they are gone after a clean close). Restore by closing the app and putting the files back.

## Logs

There is no log file. The app writes warnings and errors to the Windows **Application** event log (source ".NET Runtime"), for example
"SQL Server is not reachable". Information messages only go to a console that a Windows app does not have.
