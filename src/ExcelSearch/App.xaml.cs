using System.Windows;
using ExcelSearch.Core.Export;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Query;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data;
using ExcelSearch.Data.Sqlite;
using ExcelSearch.Services;
using ExcelSearch.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExcelSearch
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly IHost _host;

        public App()
        {
            // CreateDefaultBuilder loads appsettings.json plus appsettings.{Environment}.json from the app's
            // base directory. The environment defaults to Production; Debug builds use Development.
            // The content root is where appsettings*.json are looked for. By default it is the current directory,
            // which `dotnet run --project src/ExcelSearch` leaves at the repo root (no settings there, so the
            // connection string came out empty). The settings files are copied next to the exe, so use that.
            _host = Host.CreateDefaultBuilder()
                .UseContentRoot(AppContext.BaseDirectory)
                // The database chosen in the app (left rail) is saved per user and overrides appsettings.
                .ConfigureAppConfiguration(cfg => cfg.AddJsonFile(UserSettings.FilePath, optional: true, reloadOnChange: false))
#if DEBUG
                .UseEnvironment(Environments.Development)
#endif
                .ConfigureServices((context, services) =>
                {
                    // The database is chosen by Database:Provider ("SqlServer" is the default, "Sqlite" is the local file
                    // Database:SqlitePath). One call registers the context factory (a desktop app has no request scope, and the
                    // staging store needs a fresh short-lived context per operation), the staging store and the query service
                    // for that provider. Switching provider needs a restart.
                    var database = new DatabaseOptions
                    {
                        Provider = DatabaseOptions.ParseProvider(context.Configuration["Database:Provider"]),
                        SqlitePath = context.Configuration["Database:SqlitePath"],
                    };
                    // Top-level setting CommitLockTimeoutSeconds (default 120) in any appsettings file.
                    var staging = new StagingStoreOptions
                    {
                        CommitLockTimeoutSeconds = context.Configuration.GetValue("CommitLockTimeoutSeconds",
                            StagingStoreOptions.DefaultCommitLockTimeoutSeconds),
                    };
                    services.AddExcelSearchDatabase(database, context.Configuration.GetConnectionString("DefaultConnection"), staging);

                    services.AddSingleton<IExcelParser, ExcelParser>();
                    services.AddSingleton<ImportPreviewService>();
                    services.AddSingleton<IImportPreviewService>(sp => sp.GetRequiredService<ImportPreviewService>());
                    services.AddSingleton<IDialogService, WpfDialogService>();
                    services.AddSingleton<IAppLifecycle, WpfAppLifecycle>();
                    services.AddSingleton<IInvalidRowSource>(sp => sp.GetRequiredService<IStagingStore>());
                    services.AddSingleton<IInvalidRowExporter, InvalidRowExporter>();

                    services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

                    // ViewModels (one long-lived instance each: the shell shows the same import screen state) and the window.
                    services.AddSingleton<ImportViewModel>();
                    services.AddSingleton<QueryViewModel>();
                    services.AddSingleton<ShellViewModel>();

                    services.AddTransient<MainWindow>();
                })
                .Build();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            // SQLite: create the file and apply the schema before anything touches it. A file written by a newer ExcelSearch
            // (or one that is not an ExcelSearch database) is refused with a message, and nothing in it is changed.
            var database = _host.Services.GetRequiredService<DatabaseOptions>();
            if (database.IsSqlite)
            {
                try
                {
                    SqliteSchemaInitializer.Initialize(database.ResolveSqlitePath());
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "ExcelSearch - local database", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }
            }

            await _host.StartAsync();

            _host.Services.GetRequiredService<MainWindow>().Show();

            // Housekeeping in the background: a failure here is logged and never blocks or crashes startup.
            var preview = _host.Services.GetRequiredService<ImportPreviewService>();
            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            _ = Task.Run(async () =>
            {
                try
                {
                    var cleanup = await preview.CleanupStaleBatchesAsync();
                    logger.LogInformation("Startup cleanup: {Stale} stale batch(es) discarded, {Left} leftover batch(es) cleaned.",
                        cleanup.StaleBatchesDiscarded, cleanup.LeftoverBatchesCleaned);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Startup cleanup of stale import batches failed.");
                }
            });

            base.OnStartup(e);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            // Do not wait long for hosted services (there are none): a slow StopAsync made closing feel stuck.
            using (_host)
            {
                try { await _host.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* leaving anyway */ }
            }

            base.OnExit(e);
        }
    }

}
