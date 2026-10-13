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
            FitToScreen();
            _shell = shell;
            DataContext = shell;   // like passing the Model to a Razor view
        }

        /// <summary>
        /// Opens the window at a sensible size and position: 85% of the usable screen area (the screen minus the taskbar), at most
        /// 1400 x 900, never below the minimum size, and centred, so it is fully on screen on a laptop as well as a large monitor.
        /// (A fixed height of 820 was taller than the usable height of small or scaled screens.) Maximizing still works as usual.
        /// </summary>
        private void FitToScreen()
        {
            var area = SystemParameters.WorkArea;   // device-independent units, taskbar excluded
            var width = Math.Min(1400, Math.Max(MinWidth, area.Width * 0.85));
            var height = Math.Min(900, Math.Max(MinHeight, area.Height * 0.85));
            // On a screen smaller than the minimum size, fit the screen rather than run off it.
            width = Math.Min(width, area.Width);
            height = Math.Min(height, area.Height);

            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = width;
            Height = height;
            Left = area.Left + (area.Width - width) / 2;
            Top = area.Top + (area.Height - height) / 2;
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
