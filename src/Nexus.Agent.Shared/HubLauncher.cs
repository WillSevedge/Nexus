using System.Diagnostics;
using System.Runtime.InteropServices;
using Nexus.Contracts;

namespace Nexus.Agent;

/// <summary>Starts the hub, or brings it to the front if it is already running.</summary>
public static class HubLauncher
{
    private const string HubProcessName = "Nexus";

    /// <summary>Returns null on success, else a message for the user.</summary>
    public static string? Open(AgentLog log)
    {
        try
        {
            if (ActivateRunningHub(log)) return null;

            string? path = HubPath();
            if (path is null)
                return "The Nexus hub was not found.\n\n" +
                       "Build the Nexus.Hub project in Visual Studio (or run Nexus.exe once) so its location is recorded in:\n" +
                       NexusPaths.HubLocationFile;

            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            });
            log.Info("Started the hub: " + path);
            return null;
        }
        catch (Exception ex)
        {
            log.Error("Could not open the hub.", ex);
            return "Could not open the Nexus hub: " + ex.Message;
        }
    }

    /// <summary>The recorded hub executable, if it still exists.</summary>
    public static string? HubPath()
    {
        try
        {
            if (!File.Exists(NexusPaths.HubLocationFile)) return null;
            string path = File.ReadAllText(NexusPaths.HubLocationFile).Trim();
            return path.Length > 0 && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ActivateRunningHub(AgentLog log)
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName(HubProcessName))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session) continue;
                    var hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
                    SetForegroundWindow(hwnd);
                    log.Info($"Brought the running hub (pid {p.Id}) to the front.");
                    return true;
                }
                catch
                {
                    // Exited meanwhile or access denied: try the next one or start a new hub.
                }
            }
        }
        return false;
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
