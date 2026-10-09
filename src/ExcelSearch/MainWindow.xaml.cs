using System.Windows;
using ExcelSearch.Data;
using Microsoft.EntityFrameworkCore;

namespace ExcelSearch
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AppDbContext _db;

        public MainWindow(AppDbContext db)
        {
            InitializeComponent();
            _db = db;
        }

        private async void CountButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var count = await _db.Transactions.CountAsync();
                CountText.Text = $"Transactions rows: {count}";
            }
            catch (Exception ex)
            {
                CountText.Text = $"Connection failed: {ex.Message}";
            }
        }
    }
}
