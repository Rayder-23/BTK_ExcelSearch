using ExcelSearch.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ExcelSearch.Data;

// Hand-written half of the scaffolded AppDbContext (the scaffold calls this hook, so a --force re-scaffold keeps it).
// The scaffold describes the SQL Server mapping. When the context runs on SQLite, the provider-specific
// configuration class overrides what differs (money as cents, ISO timestamps, the GUID default).
public partial class AppDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        if (Database.IsSqlite()) SqliteModelConfiguration.Apply(modelBuilder);
    }
}
