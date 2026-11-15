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

        // Programs whose pipe is open but whose registration file is missing (deleted, or never written): the pipe's
        // name says what it is and which process, and the program says the rest when the hub connects.
        foreach (var pipe in OpenAgentPipes())
        {
            int pid = AgentClient.ProcessIdOf(pipe);
            if (pid <= 0 || found.Any(f => f.Host.ProcessId == pid) || !IsRunning(pid)) continue;
            string kind = pipe[Protocol.PipePrefix.Length..pipe.LastIndexOf('.')];
            found.Add(new AgentRegistration
            {
                PipeName = pipe,
                Host = new HostInfo { HostKind = kind, Product = kind, ProcessId = pid, MachineName = Environment.MachineName },
                RegisteredUtc = DateTime.UtcNow,
            });
        }
        return found.OrderBy(r => r.Host.HostKind).ThenBy(r => r.Host.ProcessId).ToList();
    }

    /// <summary>The names of the Nexus add-in pipes open on this PC ("Nexus.Agent.Revit.1234").</summary>
    public static List<string> OpenAgentPipes()
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\").Select(p => Path.GetFileName(p))
                .Where(n => n.StartsWith(Protocol.PipePrefix, StringComparison.OrdinalIgnoreCase) && n.LastIndexOf('.') > Protocol.PipePrefix.Length)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// What the hub can see, in plain lines, for when a program does not show up: the registration files, the add-in
    /// pipes that are open, and the Revit and AutoCAD processes running. Shown on the Connections page and logged.
    /// </summary>
    public static string Diagnose()
    {
        var lines = new List<string>();
        try
        {
            bool elevated = false;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                elevated = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { /* not Windows */ }
            lines.Add($"Hub: user {Environment.UserName}{(elevated ? " (run as administrator)" : "")}, folder {NexusPaths.AgentsDir}");

            var files = Directory.Exists(NexusPaths.AgentsDir) ? Directory.GetFiles(NexusPaths.AgentsDir, "*.json") : Array.Empty<string>();
            lines.Add($"Registration files: {files.Length}");
            foreach (var file in files)
            {
                try
                {
                    var reg = JsonSerializer.Deserialize<AgentRegistration>(File.ReadAllText(file), Json.Options);
                    lines.Add(reg is null ? $"  {Path.GetFileName(file)}: empty"
                        : $"  {Path.GetFileName(file)}: {reg.Host.Name}, process {(IsRunning(reg.Host.ProcessId) ? "running" : "not running")}, " +
                          $"pipe {(PipeIsOpen(reg.PipeName) ? "open" : "not open")}, add-in {reg.Host.AgentVersion}");
                }
                catch (Exception ex)
                {
                    lines.Add($"  {Path.GetFileName(file)}: cannot be read ({ex.Message})");
                }
            }

            var pipes = OpenAgentPipes();
            lines.Add($"Add-in pipes open: {(pipes.Count == 0 ? "none" : string.Join(", ", pipes))}");

            foreach (var (process, name) in new[] { ("Revit", "Revit"), ("acad", "AutoCAD") })
            {
                var running = Process.GetProcessesByName(process);
                foreach (var p in running)
                {
                    bool hasPipe = pipes.Any(x => AgentClient.ProcessIdOf(x) == p.Id);
                    lines.Add($"{name} is running (process {p.Id}): " + (hasPipe
                        ? "its Nexus add-in is listening."
                        : "no Nexus add-in pipe. The add-in is not loaded in it, is older than this hub, or the program runs as another Windows user."));
                    p.Dispose();
                }
            }
            if (lines.Count == 3) lines.Add("Revit and AutoCAD are not running.");
        }
        catch (Exception ex)
        {
            lines.Add("Could not finish looking: " + ex.Message);
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// True when a process with this id exists. A process the hub is not allowed to look into (one run as
    /// administrator, seen from a hub that is not) exists all the same.
    /// </summary>
    private static bool IsRunning(int processId)
    {
        Process p;
        try { p = Process.GetProcessById(processId); }
        catch (ArgumentException) { return false; } // no such process
        catch { return true; }
        using (p)
        {
            try { return !p.HasExited; }
            catch { return true; } // not allowed to ask: it is there
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
        if (!IsRunning(host.ProcessId)) return false;
        try
        {
            // Guard against a process id being used again by another program: the start time must match the
            // registration, or the add-in's pipe must be open.
            using var p = Process.GetProcessById(host.ProcessId);
            var start = p.StartTime.ToUniversalTime();
            if (host.ProcessStartUtc != default && Math.Abs((start - host.ProcessStartUtc).TotalSeconds) > 5)
                return PipeIsOpen(Protocol.PipeName(host.HostKind, host.ProcessId));
        }
        catch
        {
            // Not allowed to read the start time (a program run as administrator): it is running, that is enough.
        }
        return true;
    }
}
