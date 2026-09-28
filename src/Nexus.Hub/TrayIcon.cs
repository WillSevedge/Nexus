using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
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
        DestroyIcon(_image.Handle);
        _image.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(1, 1, r, r, 180, 90);
            path.AddArc(31 - r, 1, r, r, 270, 90);
            path.AddArc(31 - r, 31 - r, r, r, 0, 90);
            path.AddArc(1, 31 - r, r, r, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(0x1F, 0x4E, 0x8C));
            g.FillPath(fill, path);
            using var font = new Font("Segoe UI Semibold", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("N", font);
            g.DrawString("N", font, Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
