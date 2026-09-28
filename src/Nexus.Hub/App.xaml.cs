using System.Windows;
using System.Windows.Threading;
using Nexus.Hub.Core;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>
/// Nexus runs in the background like Autodesk Access: one instance per user, a tray
/// icon, started at sign-in with --background. Closing the window only hides it.
///
/// Command line:
///   --background   start hidden in the tray (used by Start with Windows)
///   --replace      stop the running hub and take over (Visual Studio F5)
///   --install      build step: stop the running hub, copy this build to %LOCALAPPDATA%\Nexus\Hub, start it
///   --shutdown     ask the running hub to exit
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan HostPollInterval = TimeSpan.FromSeconds(3);

    private HubInstance? _instance;
    private TrayIcon? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private DispatcherTimer? _poll;
    private bool _exiting;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();

        if (args.Contains("--install"))
        {
            Shutdown(HubInstance.Install());
            return;
        }
        if (args.Contains("--shutdown"))
        {
            HubInstance.Send("exit");
            Shutdown(0);
            return;
        }

        _instance = HubInstance.Claim();
        if (!_instance.IsOwner)
        {
            if (args.Contains("--replace"))
            {
                HubInstance.Send("exit");
                if (!_instance.WaitForOwnership(TimeSpan.FromSeconds(15)))
                {
                    MessageBox.Show("The running Nexus hub did not exit.", "Nexus", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown(1);
                    return;
                }
            }
            else
            {
                // Already running: bring it forward (unless this is the sign-in start) and leave.
                if (!args.Contains("--background")) HubInstance.Send("show");
                _instance.Dispose();
                Shutdown(0);
                return;
            }
        }

        HubLog.Info($"Hub started ({(args.Contains("--background") ? "background" : "window")}). Log: {HubLog.FilePath}");
        StartupRegistration.Apply();

        _vm = new MainViewModel();
        _window = new MainWindow(_vm);
        _window.Closing += (_, ce) =>
        {
            if (_exiting) return;
            ce.Cancel = true;
            _window.Hide();
            _tray?.NotifyStillRunning();
        };

        _tray = new TrayIcon(ShowWindow, () => _ = _vm.RefreshAsync(), () => ExitHub(ask: true));
        _vm.HostsChanged += summary => _tray?.SetStatus(summary);

        _instance.Listen(command => Dispatcher.BeginInvoke(() =>
        {
            switch (command)
            {
                case "show": ShowWindow(); break;
                case "refresh": _ = _vm.RefreshAsync(); break;
                case "exit": ExitHub(ask: false); break;
            }
        }));

        // Pick up hosts as they start and stop, even while the window is hidden.
        _poll = new DispatcherTimer { Interval = HostPollInterval };
        _poll.Tick += async (_, _) => await _vm.RefreshIfHostsChangedAsync();
        _poll.Start();
        _ = _vm.RefreshAsync();

        if (!args.Contains("--background")) ShowWindow();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Windows may refuse to focus a background process; a Topmost flip brings it forward anyway.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void ExitHub(bool ask)
    {
        if (ask && _vm is not null && !_vm.ConfirmExit()) return;
        _exiting = true;
        _poll?.Stop();
        _window?.Close();
        Shutdown(0);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        HubLog.Info("Hub exited.");
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HubLog.Error("Unhandled error", e.Exception);
        MessageBox.Show(e.Exception.Message, "Nexus", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
