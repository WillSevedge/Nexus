using System.Reflection;
using AecHub.Agent;
using AecHub.Agent.Acad.Modules;
using AecHub.Agent.Acad.PropertyEngine;
using AecHub.Agent.Acad.Readers;
using AecHub.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AecHub.Agent.Acad.AgentExtension))]
[assembly: CommandClass(typeof(AecHub.Agent.Acad.AgentCommands))]

namespace AecHub.Agent.Acad;

/// <summary>
/// AutoCAD-family entry point (loaded by the AecHub.bundle autoloader in AutoCAD,
/// Civil 3D and other verticals). Starts the pipe agent; loads vertical modules
/// once AutoCAD has finished starting.
/// </summary>
public sealed class AgentExtension : IExtensionApplication
{
    internal static AgentExtension? Instance { get; private set; }

    internal AgentLog Log { get; } = new("acad");
    internal AgentServer<Document>? Server { get; private set; }
    internal HostInfo? Host { get; private set; }

    private AcadDispatcher? _dispatcher;
    private ReaderRegistry<Document>? _readers;
    private ObjectPropertyReader? _objects;

    public void Initialize()
    {
        Instance = this;
        try
        {
            string dir = Path.GetDirectoryName(typeof(AgentExtension).Assembly.Location) ?? AppContext.BaseDirectory;
            string year = typeof(AgentExtension).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "HostYear")?.Value ?? "";
            string build = "";
            try { build = $"{AcApp.Version} (ACADVER {AcApp.GetSystemVariable("ACADVER")})"; } catch { /* ignored */ }

            Host = HostInfoFactory.Create(HostKinds.AutoCAD, "AutoCAD", year, build, Log);
            Log.Info($"Starting in {Host.Product} {year}, {build}, {Host.Runtime}");

            var journal = new ProbeJournal(Log);
            _objects = new ObjectPropertyReader(journal, CategoryMap.Load(dir, Log), Log);
            _readers = new ReaderRegistry<Document>();
            _readers.Register(new LayoutsReader(_objects));

            _dispatcher = new AcadDispatcher(Log);
            Server = new AgentServer<Document>(Host, _dispatcher.Queue, new AcadDocumentProvider(), _readers, Log);
            Server.Start();

            // Vertical products finish loading their own modules after us; detect on first idle.
            AcApp.Idle += OnFirstIdle;
        }
        catch (System.Exception ex)
        {
            Log.Error("AecHub agent failed to start.", ex);
        }
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        AcApp.Idle -= OnFirstIdle;
        try
        {
            if (_readers is null || _objects is null || Host is null || Server is null) return;
            string dir = Path.GetDirectoryName(typeof(AgentExtension).Assembly.Location) ?? AppContext.BaseDirectory;
            var loaded = ModuleLoader.LoadAll(dir, new AcadModuleContext(Log, _readers, _objects));
            if (loaded.Count == 0) return;

            Host.Modules = loaded.Select(m => m.Name).ToList();
            var product = loaded.Select(m => m.ProductName).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            if (product is not null) Host.Product = product;
            Server.UpdateHost(Host);
        }
        catch (System.Exception ex)
        {
            Log.Error("Loading modules failed.", ex);
        }
    }

    public void Terminate()
    {
        try
        {
            Server?.Dispose();
            _dispatcher?.Dispose();
        }
        catch (System.Exception ex)
        {
            Log.Error("Error during shutdown.", ex);
        }
    }
}

public sealed class AgentCommands
{
    /// <summary>AECHUBSTATUS: print the agent's connection status.</summary>
    [CommandMethod("AECHUBSTATUS", CommandFlags.Modal)]
    public void Status()
    {
        var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null) return;
        var app = AgentExtension.Instance;
        var s = app?.Server?.Status;
        if (s is null)
        {
            ed.WriteMessage("\nAecHub agent is not running. Log folder: " + AecHubPaths.LogsDir);
            return;
        }
        ed.WriteMessage($"\nAecHub agent: {s.State}" +
                        $"\n  Host: {app!.Host?.DisplayName}  Modules: {string.Join(", ", app.Host?.Modules ?? new())}" +
                        $"\n  Pipe: {s.PipeName}" +
                        $"\n  Hub connections: {s.Clients}  Requests: {s.RequestsHandled}" +
                        (s.LastError is null ? "" : $"\n  Last error: {s.LastError}") +
                        $"\n  Log: {app.Log.FilePath}\n");
    }
}
