using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Nexus.Contracts;
using Nexus.Hub.Core;

namespace Nexus.Hub;

/// <summary>
/// One hub per Windows user session. The running hub owns a mutex and listens on
/// <see cref="Protocol.HubControlPipe"/> for "show", "refresh" and "exit".
/// </summary>
internal sealed class HubInstance : IDisposable
{
    private const string MutexName = @"Local\Nexus.Hub";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private bool _owned;

    private HubInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>Try to become the running hub.</summary>
    public static HubInstance Claim()
    {
        var mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // previous hub crashed
        return new HubInstance(mutex, owned);
    }

    public bool IsOwner => _owned;

    /// <summary>Asks the running hub to exit and waits until it has (up to <paramref name="timeout"/>).</summary>
    public bool WaitForOwnership(TimeSpan timeout)
    {
        if (_owned) return true;
        try { _owned = _mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { _owned = true; }
        return _owned;
    }

    public static bool Send(string command, int timeoutMs = 1000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Protocol.HubControlPipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Handles control commands until disposed. <paramref name="onCommand"/> runs on a background thread.</summary>
    public void Listen(Action<string> onCommand) => Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(Protocol.HubControlPipe, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(_stop.Token);
                if (!string.IsNullOrWhiteSpace(line)) onCommand(line.Trim().ToLowerInvariant());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                HubLog.Warn("Hub control pipe error.", ex);
                try { await Task.Delay(1000, _stop.Token); } catch { break; }
            }
        }
    });

    /// <summary>
    /// --install: stop the running hub, copy this program to %LOCALAPPDATA%\Nexus\Hub, add the
    /// Start Menu shortcut and the Settings › Apps entry, and start the installed copy
    /// (in the tray only when <paramref name="background"/>). Returns the process exit code.
    /// </summary>
    public static int Install(bool background)
    {
        string target = NexusPaths.HubInstallDir;
        string sourceDir = AppContext.BaseDirectory;
        bool fromInstallDir = SameDir(sourceDir, target);

        if (!fromInstallDir)
        {
            StopRunningHub();

            // A published Nexus.exe is one self-contained file; a Visual Studio build is a folder of files.
            bool singleFile = !File.Exists(Path.Combine(sourceDir, "Nexus.dll"));
            var files = singleFile
                ? new[] { Environment.ProcessPath! }
                : Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).ToArray();
            string root = singleFile ? Path.GetDirectoryName(Environment.ProcessPath!)! : sourceDir;

            // Start clean so files from an older layout do not linger.
            if (Directory.Exists(target))
                foreach (var old in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                    Retry(() => File.Delete(old));
            Directory.CreateDirectory(target);

            foreach (var file in files)
            {
                string dest = Path.Combine(target, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                Retry(() => File.Copy(file, dest, overwrite: true));
            }
            HubLog.Info($"Installed the hub to {target} ({files.Length} file(s)).");
        }

        ShellIntegration.Register();
        StartupRegistration.Apply();

        // Shell-execute so the new hub does not inherit this process's (or a build's) console handles.
        Process.Start(new ProcessStartInfo(NexusPaths.HubExe, background ? "--background" : "")
        {
            UseShellExecute = true,
            WorkingDirectory = target,
        });
        return 0;
    }

    /// <summary>--uninstall: stop the hub, remove the startup entry, shortcut, Apps entry and program files.</summary>
    public static int Uninstall()
    {
        StopRunningHub();
        StartupRegistration.Remove();
        ShellIntegration.Unregister();

        string target = NexusPaths.HubInstallDir;
        if (SameDir(AppContext.BaseDirectory, target) || SameDir(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", target))
        {
            // We are running from the folder being removed: delete it once this process has exited.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        else if (Directory.Exists(target))
        {
            try { Directory.Delete(target, recursive: true); }
            catch (Exception ex) { HubLog.Warn("Could not delete " + target, ex); }
        }
        HubLog.Info("Uninstalled the hub. Logs and settings remain in " + NexusPaths.Root);
        return 0;
    }

    private static void StopRunningHub()
    {
        using var instance = Claim();
        if (instance.IsOwner) return;
        Send("exit");
        if (!instance.WaitForOwnership(TimeSpan.FromSeconds(15)))
            HubLog.Warn("The running hub did not exit; files in use may not be replaced.");
    }

    private static bool SameDir(string a, string b) =>
        a.Length > 0 && b.Length > 0 &&
        Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static void Retry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { /* ignored */ }
        }
        _mutex.Dispose();
    }
}
