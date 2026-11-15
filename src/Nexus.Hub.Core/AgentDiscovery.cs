using System.Diagnostics;
using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Hub.Core;

/// <summary>Finds running agents from their registration files.</summary>
public static class AgentDiscovery
{
    public static List<AgentRegistration> Discover(bool removeStale = true)
    {
        var found = new List<AgentRegistration>();
        if (!Directory.Exists(NexusPaths.AgentsDir)) return found;

        foreach (var file in Directory.EnumerateFiles(NexusPaths.AgentsDir, "*.json"))
        {
            AgentRegistration? reg = null;
            try
            {
                reg = JsonSerializer.Deserialize<AgentRegistration>(File.ReadAllText(file), Json.Options);
            }
            catch (Exception ex)
            {
                HubLog.Warn($"Unreadable registration {file}", ex);
            }

            if (reg is null || string.IsNullOrEmpty(reg.PipeName)) continue;

            if (!IsAlive(reg.Host))
            {
                // Removed only when the program is really gone. A registration that merely looks wrong (its process
                // id is in use, but the start time differs) is left alone: deleting the file of a running program
                // would hide it from the hub until it is restarted.
                bool running = IsRunning(reg.Host.ProcessId);
                HubLog.Info(running
                    ? $"Registration of pid {reg.Host.ProcessId} ({reg.Host.Name}) does not match the running process and its pipe is not open; ignoring."
                    : $"Agent pid {reg.Host.ProcessId} is no longer running; ignoring.");
                if (removeStale && !running)
                {
                    try { File.Delete(file); } catch { /* ignored */ }
                }
                continue;
            }
            found.Add(reg);
        }
        return found.OrderBy(r => r.Host.HostKind).ThenBy(r => r.Host.ProcessId).ToList();
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when a named pipe of this name is open on this PC (listed, not opened: opening would take a connection).</summary>
    public static bool PipeIsOpen(string pipeName)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => Path.GetFileName(p).Equals(pipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The program that wrote the registration is still running: its process exists and started when the registration
    /// says, or (when the times differ, e.g. the program could not read its own start time) its pipe is open.
    /// </summary>
    public static bool IsAlive(HostInfo host)
    {
        try
        {
            using var p = Process.GetProcessById(host.ProcessId);
            if (p.HasExited) return false;
            try
            {
                // Guard against pid reuse: the start time must match the registration.
                var start = p.StartTime.ToUniversalTime();
                if (host.ProcessStartUtc != default && Math.Abs((start - host.ProcessStartUtc).TotalSeconds) > 5)
                    return PipeIsOpen(Protocol.PipeName(host.HostKind, host.ProcessId));
            }
            catch
            {
                // Access denied reading StartTime: assume alive.
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
