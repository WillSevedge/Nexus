using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.Agent;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Nexus.Agent.Revit;

/// <summary>
/// "Nexus" ribbon tab with one button that shows the hub connection status. The pipe
/// server reports changes from background threads; the ribbon is updated from
/// Revit's Idling event (UI thread).
/// </summary>
internal sealed class StatusRibbon : IDisposable
{
    private const string TabName = "Nexus";

    private readonly UIControlledApplication _app;
    private readonly AgentLog _log;
    private readonly PushButton _button;
    private readonly Dictionary<AgentState, ImageSource> _icons = new();
    private readonly Dictionary<AgentState, ImageSource> _small = new();
    private volatile AgentStatus? _pending;

    public StatusRibbon(UIControlledApplication app, AgentLog log)
    {
        _app = app;
        _log = log;

        try { app.CreateRibbonTab(TabName); } catch { /* already exists */ }
        var panel = app.CreateRibbonPanel(TabName, "Hub Link");
        string assembly = Assembly.GetExecutingAssembly().Location;

        foreach (AgentState s in Enum.GetValues<AgentState>())
        {
            _icons[s] = RibbonIcons.Create(s, 32);
            _small[s] = RibbonIcons.Create(s, 16);
        }

        var data = new PushButtonData("NexusStatus", "Hub:\nStarting", assembly, typeof(ShowStatusCommand).FullName)
        {
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            ToolTip = "Nexus agent status. Click for details or to show the Nexus hub.",
            LargeImage = _icons[AgentState.Stopped],
            Image = RibbonIcons.Create(AgentState.Stopped, 16),
        };
        _button = (PushButton)panel.AddItem(data);

        app.Idling += OnIdling;
    }

    /// <summary>Called from any thread.</summary>
    public void OnStatusChanged(AgentStatus status) => _pending = status;

    private void OnIdling(object? sender, IdlingEventArgs e)
    {
        var status = _pending;
        if (status is null) return;
        _pending = null;
        try
        {
            _button.ItemText = status.State switch
            {
                AgentState.Connected => $"Hub:\nConnected",
                AgentState.Listening => "Hub:\nWaiting",
                AgentState.Faulted => "Hub:\nError",
                _ => "Hub:\nStopped",
            };
            _button.ToolTip = $"State: {status.State}\nPipe: {status.PipeName}\nHub connections: {status.Clients}\nRequests: {status.RequestsHandled}"
                              + (status.LastError is null ? "" : $"\nLast error: {status.LastError}");
            _button.LargeImage = _icons[status.State];
            _button.Image = _small[status.State];
        }
        catch (Exception ex)
        {
            _log.Warn("Could not update ribbon status.", ex);
        }
    }

    public void Dispose()
    {
        try { _app.Idling -= OnIdling; } catch { /* ignored */ }
    }
}
