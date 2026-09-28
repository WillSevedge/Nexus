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
    /// Build step (--install): stop the running hub, copy this build to the install folder,
    /// and start the installed copy in the background. Returns the process exit code.
    /// </summary>
    public static int Install()
    {
        string source = AppContext.BaseDirectory;
        string target = NexusPaths.HubInstallDir;
        if (Path.GetFullPath(source).TrimEnd('\\', '/').Equals(Path.GetFullPath(target).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return 0;

        using (var instance = Claim())
        {
            if (!instance.IsOwner)
            {
                Send("exit");
                if (!instance.WaitForOwnership(TimeSpan.FromSeconds(15)))
                {
                    HubLog.Warn("Install: the running hub did not exit; files in use may not be updated.");
                }
            }

            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Copy(file, dest, overwrite: true);
                        break;
                    }
                    catch (IOException) when (attempt < 10)
                    {
                        Thread.Sleep(300);
                    }
                }
            }
        }
        HubLog.Info("Installed the hub to " + target);

        // Shell-execute so the new hub does not inherit the build's console handles.
        Process.Start(new ProcessStartInfo(NexusPaths.HubExe, "--background")
        {
            UseShellExecute = true,
            WorkingDirectory = target,
        });
        return 0;
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
