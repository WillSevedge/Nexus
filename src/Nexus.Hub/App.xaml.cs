using System.Windows;
using System.Windows.Threading;
using Nexus.Hub.Core;

namespace Nexus.Hub;

public partial class App : Application
{
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HubLog.Error("Unhandled error", e.Exception);
        MessageBox.Show(e.Exception.Message, "Nexus", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
