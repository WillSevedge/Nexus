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
    private AgentState _state = AgentState.Stopped;

    public StatusRibbon(UIControlledApplication app, AgentLog log)
    {
        _app = app;
        _log = log;

        try { app.CreateRibbonTab(TabName); } catch { /* already exists */ }
        var panel = app.CreateRibbonPanel(TabName, "Hub Link");
        string assembly = Assembly.GetExecutingAssembly().Location;

        BuildIcons();

        var data = new PushButtonData("NexusStatus", "Hub:\nStarting", assembly, typeof(ShowStatusCommand).FullName)
        {
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            ToolTip = "Nexus agent status. Click for details or to show the Nexus hub.",
            LargeImage = _icons[AgentState.Stopped],
            Image = _small[AgentState.Stopped],
        };
        _button = (PushButton)panel.AddItem(data);

        app.Idling += OnIdling;
        try { app.ThemeChanged += OnThemeChanged; } catch { /* older Revit */ }
    }

    /// <summary>White N on Revit's dark theme, black N on the light theme.</summary>
    private void BuildIcons()
    {
        bool dark = false;
        try { dark = UIThemeManager.CurrentTheme == UITheme.Dark; } catch { /* default: light */ }
        foreach (AgentState s in Enum.GetValues<AgentState>())
        {
            _icons[s] = RibbonIcons.Create(s, 32, dark);
            _small[s] = RibbonIcons.Create(s, 16, dark);
        }
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        try
        {
            BuildIcons();
            _button.LargeImage = _icons[_state];
            _button.Image = _small[_state];
        }
        catch (Exception ex)
        {
            _log.Warn("Could not update the ribbon icon for the new theme.", ex);
        }
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
            _state = status.State;
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
        try { _app.ThemeChanged -= OnThemeChanged; } catch { /* ignored */ }
    }
}
