using System.Diagnostics;
using System.Windows;

namespace ExcelSearch.Services;

public sealed class WpfAppLifecycle : IAppLifecycle
{
    public void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
