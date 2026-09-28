using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.Agent;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Nexus.Agent.Revit;

/// <summary>
/// "Nexus" ribbon tab with an "Open Hub" button and a button that shows connection status. The pipe
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
    private volatile AgentStatus? _pending;

    public StatusRibbon(UIControlledApplication app, AgentLog log)
    {
        _app = app;
        _log = log;

        try { app.CreateRibbonTab(TabName); } catch { /* already exists */ }
        var panel = app.CreateRibbonPanel(TabName, "Hub Link");
        string assembly = Assembly.GetExecutingAssembly().Location;

        var openIcon = CreateIcon(Color.FromRgb(0x1F, 0x4E, 0x8C), "N");
        panel.AddItem(new PushButtonData("NexusOpenHub", "Open\nHub", assembly, typeof(OpenHubCommand).FullName)
        {
            ToolTip = "Open the Nexus hub to view and edit this model's data. Brings it to the front if it is already open.",
            LargeImage = openIcon,
            Image = openIcon,
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
        });

        foreach (AgentState s in Enum.GetValues<AgentState>())
            _icons[s] = CreateIcon(StateColor(s), "H");

        var data = new PushButtonData("NexusStatus", "Hub:\nStarting", assembly, typeof(ShowStatusCommand).FullName)
        {
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            ToolTip = "Nexus agent status. Click for details.",
            LargeImage = _icons[AgentState.Stopped],
            Image = _icons[AgentState.Stopped],
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
            _button.Image = _icons[status.State];
        }
        catch (Exception ex)
        {
            _log.Warn("Could not update ribbon status.", ex);
        }
    }

    private static Color StateColor(AgentState state) => state switch
    {
        AgentState.Connected => Color.FromRgb(0x2E, 0x9E, 0x44),
        AgentState.Listening => Color.FromRgb(0x2F, 0x6F, 0xD6),
        AgentState.Faulted => Color.FromRgb(0xD0, 0x3A, 0x2F),
        _ => Color.FromRgb(0x8A, 0x8A, 0x8A),
    };

    private static ImageSource CreateIcon(Color color, string letter)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, new Rect(2, 2, 28, 28), 6, 6);
            var text = new FormattedText(letter, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 20, Brushes.White, 1.0);
            dc.DrawText(text, new Point(16 - text.Width / 2, 16 - text.Height / 2));
        }
        var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        try { _app.Idling -= OnIdling; } catch { /* ignored */ }
    }
}
