using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace IISMonitor.Dashboard;

public partial class App : Application
{
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.TraceError(e.Exception.ToString());
        MessageBox.Show(e.Exception.Message, "IIS Monitor — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
