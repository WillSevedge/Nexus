using Nexus.Agent;
using Nexus.Agent.Revit.Readers;
using Nexus.Contracts;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Nexus.Agent.Revit;

/// <summary>
/// Revit entry point. Starts the named-pipe agent, adds the Nexus ribbon panel,
/// and marshals hub requests onto Revit's main thread through an ExternalEvent.
/// </summary>
public sealed class AgentApplication : IExternalApplication
{
    internal static AgentApplication? Instance { get; private set; }

    internal AgentLog Log { get; } = new("revit");
    internal AgentServer<Document>? Server { get; private set; }
    internal HostInfo? Host { get; private set; }

    private RevitDispatcher? _dispatcher;
    private StatusRibbon? _ribbon;

    public Result OnStartup(UIControlledApplication application)
    {
        Instance = this;
        var startup = System.Diagnostics.Stopwatch.StartNew();
        // 2024 (.NET Framework): load our own copies of the libraries we ship if the program has others.
        Compat.ResolveDependenciesFrom(System.IO.Path.GetDirectoryName(typeof(Compat).Assembly.Location) ?? AppContext.BaseDirectory);
        try
        {
            // On Revit's own thread, while it starts: only what must be (the ribbon's buttons, with no pictures yet,
            // and the event that brings hub requests onto this thread). Everything else starts in the background, so
            // Revit's start does not wait for it.
            var app = application.ControlledApplication;
            string version = app.VersionNumber, build = $"{app.SubVersionNumber} ({app.VersionBuild}) {app.Product}";
            string started = $"Starting in {app.VersionName} {app.SubVersionNumber}, build {app.VersionBuild}";

            try
            {
                _ribbon = new StatusRibbon(application, Log);
            }
            catch (Exception ex)
            {
                // The agent still works without the ribbon.
                Log.Error("Could not create the ribbon panel.", ex);
            }
            _dispatcher = new RevitDispatcher(Log);
            long onRevitsThread = startup.ElapsedMilliseconds;

            var dispatcher = _dispatcher;
            var ribbon = _ribbon;
            Task.Run(() =>
            {
                try
                {
                    Host = HostInfoFactory.Create(HostKinds.Revit, "Revit", version, build, Log);
                    Log.Info($"{started}, {Host.Runtime}");
                    var server = new AgentServer<Document>(Host, dispatcher.Queue, new RevitDocumentProvider(dispatcher),
                        RevitReaders.CreateRegistry(), Log, writer: new RevitWriter(), selector: new RevitSelector(dispatcher),
                        pdf: new RevitPdfExporter(), sheets: new RevitSheetCreator());
                    if (ribbon is not null) server.StatusChanged += ribbon.OnStatusChanged;
                    Server = server;
                    server.Start();
                    // What Nexus adds to Revit's start is the first number; the rest ran beside it.
                    Log.Info($"Nexus add-in: {onRevitsThread} ms of Revit's start; ready for the hub after {startup.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    Log.Error("Nexus agent failed to start.", ex);
                }
            });
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Error("Nexus agent failed to start.", ex);
            // Succeed anyway so Revit does not show an add-in failure dialog on every start;
            // the ribbon/status command and log explain what happened.
            return Result.Succeeded;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            Server?.Dispose();
            _dispatcher?.Dispose();
            _ribbon?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Error during shutdown.", ex);
        }
        return Result.Succeeded;
    }
}
