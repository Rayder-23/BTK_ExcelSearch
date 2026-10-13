using ExcelSearch.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ExcelSearch.Data.Sqlite;

/// <summary>
/// What differs in the EF model on SQLite. The entities and the rest of the mapping are shared with SQL Server (the
/// scaffolded AppDbContext); this class only overrides the parts SQLite cannot store the same way:
/// every decimal (all five are money) becomes INTEGER cents, every DateTime becomes ISO UTC text, and the column
/// defaults are SQLite expressions instead of NEWSEQUENTIALID() / SYSUTCDATETIME(). The tables themselves are created by
/// Scripts/db/sqlite/001_create_schema.sql, not by EF.
/// </summary>
internal static class SqliteModelConfiguration
{
    public const string NowSql = "(strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))";

    // Same expression as the column default in the schema script: a random version-4 GUID in the upper case EF Core writes.
    public const string GuidSql =
        "(upper(lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))), 2) || '-' " +
        "|| substr('89ab', abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6)))))";

    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clr == typeof(decimal))
                {
                    // INTEGER cents instead of the TEXT EF would use: ranges, ORDER BY and SUM() stay numeric.
                    property.SetValueConverter(SqliteValues.MoneyConverter);
                    property.SetColumnType("INTEGER");
                }
                else if (clr == typeof(DateTime))
                {
                    property.SetValueConverter(SqliteValues.TimestampConverter);
                    property.SetColumnType("TEXT");
                }
            }
        }

        modelBuilder.Entity<ImportBatch>().Property(e => e.ImportedAt).HasDefaultValueSql(NowSql);
        modelBuilder.Entity<Transaction>().Property(e => e.CreatedAt).HasDefaultValueSql(NowSql);
        modelBuilder.Entity<Transaction>().Property(e => e.Id).HasDefaultValueSql(GuidSql);
    }
}
