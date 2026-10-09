using System.Windows;
using ExcelSearch.Core.Staging;
using ExcelSearch.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace ExcelSearch
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly ImportPreviewService _preview;
        private readonly IStagingStore _staging;
        private int? _batchId;

        public MainWindow(IDbContextFactory<AppDbContext> dbFactory, ImportPreviewService preview, IStagingStore staging)
        {
            InitializeComponent();
            _dbFactory = dbFactory;
            _preview = preview;
            _staging = staging;
        }

        private async void CountButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var count = await db.Transactions.CountAsync();
                CountText.Text = $"Transactions rows: {count}";
            }
            catch (Exception ex)
            {
                CountText.Text = $"Connection failed: {ex.Message}";
            }
        }

        private async void ChooseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx" };
            if (dialog.ShowDialog(this) != true) return;

            ChooseButton.IsEnabled = false;
            DiscardButton.IsEnabled = false;
            StageProgress.IsIndeterminate = true;
            StageProgress.Visibility = Visibility.Visible;
            StageText.Text = "Staging...";

            // Progress<T> created here posts its callbacks back to the UI thread.
            var progress = new Progress<int>(rows => StageText.Text = $"Staging... {rows:N0} rows loaded");
            var path = dialog.FileName;
            var user = ImportPreviewService.CurrentUser();

            try
            {
                // Task.Run keeps hashing, parsing and loading off the UI thread, so the window stays responsive.
                var result = await Task.Run(() => _preview.StageFileAsync(path, user, progress));

                if (result.BatchId is null)
                {
                    StageText.Text = "File rejected: " + string.Join(" ", result.FileErrors);
                }
                else
                {
                    _batchId = result.BatchId;
                    DiscardButton.IsEnabled = true;
                    StageText.Text = $"Batch {result.BatchId}: " +
                        string.Join(", ", result.Counts.Select(c => $"{c.Key} {c.Value:N0}")) +
                        $" ({result.RowsLoaded:N0} rows, {result.LoadRowsPerSecond:N0} rows/s load, {result.TotalElapsed.TotalSeconds:F1}s total)";
                }
            }
            catch (Exception ex)
            {
                StageText.Text = $"Staging failed: {ex.Message}";
            }
            finally
            {
                StageProgress.Visibility = Visibility.Hidden;
                StageProgress.IsIndeterminate = false;
                ChooseButton.IsEnabled = true;
            }
        }

        private async void DiscardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_batchId is not { } id) return;

            DiscardButton.IsEnabled = false;
            try
            {
                await _staging.DiscardAsync(id);
                StageText.Text = $"Batch {id} discarded.";
                _batchId = null;
            }
            catch (Exception ex)
            {
                StageText.Text = $"Discard failed: {ex.Message}";
                DiscardButton.IsEnabled = true;
            }
        }
    }
}
