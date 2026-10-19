using ExcelSearch.Core.Query;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data.Sqlite;
using ExcelSearch.Data.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExcelSearch.Data;

public static class DatabaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database for the configured provider: the context factory, the staging store and the query service. This is the
    /// one place that knows which provider is in use; everything else depends on the interfaces and the context factory.
    /// A restart is needed to switch provider.
    /// </summary>
    /// <param name="sqlServerConnectionString">Used when the provider is SQL Server; ignored for SQLite.</param>
    public static IServiceCollection AddExcelSearchDatabase(this IServiceCollection services, DatabaseOptions database,
        string? sqlServerConnectionString, StagingStoreOptions staging)
    {
        services.AddSingleton(database);
        services.AddSingleton(staging);

        switch (database.Provider)
        {
            case DatabaseProvider.Sqlite:
                var connectionString = SqliteSchemaInitializer.BuildConnectionString(database.ResolveSqlitePath());
                services.AddSingleton<SqlitePragmaInterceptor>();
                services.AddDbContextFactory<AppDbContext>((sp, options) =>
                    options.UseSqlite(connectionString).AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>()));
                services.AddSingleton<IStagingStore, SqliteStagingStore>();
                services.AddSingleton<ITransactionQueryService, SqliteTransactionQueryService>();
                break;

            default:
                services.AddDbContextFactory<AppDbContext>(options => 
                    // Compatibility level 110 = SQL Server 2012: list filters become IN (...) instead of OPENJSON (2016+).
                    options.UseSqlServer(sqlServerConnectionString, sql => sql.UseCompatibilityLevel(110)));
                services.AddSingleton<IStagingStore, SqlServerStagingStore>();
                services.AddSingleton<ITransactionQueryService, SqlTransactionQueryService>();
                break;
        }

        return services;
    }
}
