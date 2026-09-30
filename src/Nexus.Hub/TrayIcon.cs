using System.Drawing;
using WinForms = System.Windows.Forms;

namespace Nexus.Hub;

/// <summary>The Nexus icon next to the clock: open the hub, refresh, start with Windows, exit.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _startWithWindows;
    private readonly Icon _image;
    private bool _toldAboutBackground;

    public TrayIcon(Action open, Action refresh, Action exit)
    {
        _image = CreateIcon();
        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Open Nexus", null, (_, _) => open()) { Font = new Font(WinForms.Control.DefaultFont, FontStyle.Bold) };
        _startWithWindows = new WinForms.ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            bool on = !_startWithWindows!.Checked;
            StartupRegistration.Set(on);
            _startWithWindows.Checked = on;
        }) { Checked = StartupRegistration.IsEnabled };

        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Refresh hosts", null, (_, _) => refresh()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Exit Nexus", null, (_, _) => exit()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = _image,
            Text = "Nexus",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) open();
        };
    }

    /// <summary>Tooltip text (Windows limits it to 127 characters).</summary>
    public void SetStatus(string text)
    {
        text = "Nexus - " + text;
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>The first time the window is closed, explain that Nexus keeps running.</summary>
    public void NotifyStillRunning()
    {
        if (_toldAboutBackground) return;
        _toldAboutBackground = true;
        _icon.ShowBalloonTip(4000, "Nexus is still running",
            "Nexus stays in the background to connect Revit, AutoCAD and Civil 3D. Click the tray icon to open it, or right-click it to exit.",
            WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    /// <summary>The embedded Nexus.ico at the tray's icon size (sharp at any display scaling).</summary>
    private static Icon CreateIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Nexus.ico")
                           ?? throw new InvalidOperationException("Nexus.ico is not embedded.");
        return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }
}
