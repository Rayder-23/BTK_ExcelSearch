# ExcelSearch

A Windows desktop app (WPF) for importing bank-transaction Excel sheets into SQL Server and searching them.

**Status: early scaffold.** The solution structure, database schema, EF Core model and dependency injection are in place. The only screen is a button that counts rows in `Transactions` to prove the database connection works. Excel import and the search/filter screen are not built yet.

## Solution layout

```
ExcelSearch.sln
src/
├── ExcelSearch/        WPF app (UI, DI host, appsettings)
├── ExcelSearch.Core/   class library: Excel import logic (ClosedXML) - not implemented yet
└── ExcelSearch.Data/   class library: EF Core model (AppDbContext, entities)
Scripts/db/             T-SQL schema script
Samples/                example Excel files
db.md                   database schema documentation and change history
```

The WPF project references Core and Data. Core and Data do not reference the WPF project.

## Tech stack

- .NET 8 (`net8.0-windows` for the app, `net8.0` for the libraries)
- WPF, with `Microsoft.Extensions.Hosting` for configuration and dependency injection
- Entity Framework Core 8 (SQL Server)
- ClosedXML (Excel reading, in Core)
- CommunityToolkit.Mvvm (referenced, not used yet)

## Prerequisites

- Windows
- .NET 8 SDK (or newer; the target stays .NET 8)
- SQL Server (the project was set up against SQL Server Express 2022)
- `dotnet-ef` tool only if you want to re-scaffold the model: `dotnet tool install --global dotnet-ef`

## Getting started

1. **Create the database.** Create an empty database named `ExcelSearch`, then run `Scripts/db/001_create_tables.sql` against it. The script is safe to re-run: it only creates tables that do not exist. See [db.md](db.md) for the schema.
2. **Configure the connection string.** Settings files live in `src/ExcelSearch/`:

   | File | In git? | Used when |
   |---|---|---|
   | `appsettings.json` | yes | Always loaded first (connection string left empty) |
   | `appsettings.Development.json` | no (gitignored) | Debug builds |
   | `appsettings.Production.json` | no (gitignored) | Release builds |
   | `*.json.example` | yes | Templates to copy |

   Copy `appsettings.Development.json.example` to `appsettings.Development.json` (and the Production one likewise) and fill in your server, user and password. The key is `ConnectionStrings:DefaultConnection`. Never commit the real files.
3. **Build and run.**
   ```
   dotnet build ExcelSearch.sln
   dotnet run --project src/ExcelSearch
   ```
   Click **Count Transactions**. A fresh database shows `Transactions rows: 0`. If the connection fails, the label shows the error (a down server can take about 15 seconds to time out).

Debug builds run in the `Development` environment; any other build defaults to `Production`.

## Database model

Two tables: `ImportBatches` (one row per imported file) and `Transactions` (one row per Excel data row).

- The primary key of `Transactions` is the business key **(PayOrderNo, TxnRefSeqNo)**, so a duplicate row is rejected by the database.
- `Transactions.Id` is a separate unique GUID supplied by the app.
- Each row stores a `RowHash` (SHA-256 of the business columns) so an import can tell an exact duplicate (same key, same hash) from a conflict (same key, different hash).
- Identifiers such as CNIC and account numbers are stored as text so leading zeros survive.

Full column list, indexes and history: [db.md](db.md).

## Re-scaffolding the EF Core model

If the schema changes, regenerate the entities from the live database (replace the connection string with yours):

```
dotnet ef dbcontext scaffold "<connection string>" Microsoft.EntityFrameworkCore.SqlServer ^
  --project src/ExcelSearch.Data --startup-project src/ExcelSearch ^
  --context AppDbContext --context-dir . --output-dir Entities ^
  --namespace ExcelSearch.Data.Entities --context-namespace ExcelSearch.Data ^
  --table dbo.ImportBatches --table dbo.Transactions --no-onconfiguring --force
```

Then update [db.md](db.md).

## Notes

- The app registers `AppDbContext` as transient (a desktop app has no request scope), so each window gets its own context.
- `Samples/` holds example workbooks showing the expected Excel column headers.


## Credits

**Developed By:**

- Rayder-23