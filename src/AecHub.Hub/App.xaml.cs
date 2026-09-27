using System.Windows;
using System.Windows.Threading;
using AecHub.Hub.Core;

namespace AecHub.Hub;

public partial class App : Application
{
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HubLog.Error("Unhandled error", e.Exception);
        MessageBox.Show(e.Exception.Message, "AecHub", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
