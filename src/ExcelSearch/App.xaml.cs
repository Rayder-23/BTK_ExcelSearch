using System.Windows;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
            _host = Host.CreateDefaultBuilder()
#if DEBUG
                .UseEnvironment(Environments.Development)
#endif
                .ConfigureServices((context, services) =>
                {
                    // A factory instead of an injected context: a desktop app has no request scope, and the
                    // staging store needs a fresh short-lived context per chunk. Consumers call CreateDbContext().
                    services.AddDbContextFactory<AppDbContext>(options =>
                        options.UseSqlServer(context.Configuration.GetConnectionString("DefaultConnection")));

                    services.AddSingleton<IExcelParser, ExcelParser>();
                    services.AddSingleton<IStagingStore, SqlServerStagingStore>();
                    services.AddSingleton<ImportPreviewService>();

                    services.AddTransient<MainWindow>();
                })
                .Build();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            await _host.StartAsync();

            _host.Services.GetRequiredService<MainWindow>().Show();

            base.OnStartup(e);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            using (_host)
            {
                await _host.StopAsync();
            }

            base.OnExit(e);
        }
    }

}
