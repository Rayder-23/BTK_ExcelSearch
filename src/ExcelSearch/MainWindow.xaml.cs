using System.ComponentModel;
using System.Windows;
using ExcelSearch.ViewModels;

namespace ExcelSearch
{
    /// <summary>
    /// The shell window. Almost no code here: the ViewModel holds the logic, XAML the layout. The only code is the
    /// closing hook, because a window's Closing event has no ViewModel equivalent.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ShellViewModel _shell;
        private bool _closeApproved;

        public MainWindow(ShellViewModel shell)
        {
            InitializeComponent();
            _shell = shell;
            DataContext = shell;   // like passing the Model to a Razor view
        }

        // Closing cannot wait for a dialog that is awaited, so: cancel this attempt, ask, and close again if approved.
        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_closeApproved || _shell.CloseApproved)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            bool ok;
            try
            {
                ok = await _shell.ConfirmCloseAsync();
            }
            catch
            {
                ok = true;   // never trap the user in the window because of a failure while asking
            }

            if (ok)
            {
                _closeApproved = true;
                // Close() cannot be called from inside the Closing event (it threw "while a Window is closing" and crashed the
                // app on every normal exit); queue it for after this event has finished.
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }
}
