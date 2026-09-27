using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AecHub.Agent;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace AecHub.Agent.Revit;

/// <summary>
/// "AecHub" ribbon tab with one button that shows connection status. The pipe
/// server reports changes from background threads; the ribbon is updated from
/// Revit's Idling event (UI thread).
/// </summary>
internal sealed class StatusRibbon : IDisposable
{
    private const string TabName = "AecHub";

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

        foreach (AgentState s in Enum.GetValues<AgentState>())
            _icons[s] = CreateIcon(s);

        var data = new PushButtonData("AecHubStatus", "Hub:\nStarting",
            Assembly.GetExecutingAssembly().Location, typeof(ShowStatusCommand).FullName)
        {
            ToolTip = "AecHub agent status. Click for details.",
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

    private static ImageSource CreateIcon(AgentState state)
    {
        var color = state switch
        {
            AgentState.Connected => Color.FromRgb(0x2E, 0x9E, 0x44),
            AgentState.Listening => Color.FromRgb(0x2F, 0x6F, 0xD6),
            AgentState.Faulted => Color.FromRgb(0xD0, 0x3A, 0x2F),
            _ => Color.FromRgb(0x8A, 0x8A, 0x8A),
        };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, new Rect(2, 2, 28, 28), 6, 6);
            var text = new FormattedText("H", System.Globalization.CultureInfo.InvariantCulture,
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
