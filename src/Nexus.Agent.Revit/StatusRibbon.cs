using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.Agent;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Nexus.Agent.Revit;

/// <summary>
/// "Nexus" ribbon tab: the Hub Link button (hub connection status) and the Fabrication tools
/// (the bridge to the Fabrication CADmep database). The pipe
/// server reports changes from background threads; the ribbon is updated from
/// Revit's Idling event (UI thread).
/// </summary>
internal sealed class StatusRibbon : IExternalEventHandler, IDisposable
{
    private const string TabName = "Nexus";

    private readonly UIControlledApplication _app;
    private readonly AgentLog _log;
    private readonly PushButton _button;
    private readonly Dictionary<AgentState, ImageSource> _icons = new();
    private readonly Dictionary<AgentState, ImageSource> _small = new();
    /// <summary>Tool buttons drawn from an icon-font glyph (redrawn when the theme changes).</summary>
    private readonly List<(PushButton Button, string Glyph)> _tools = new();
    private volatile AgentStatus? _pending;
    private AgentState _state = AgentState.Stopped;
    /// <summary>Raised when the status changes: Revit then calls <see cref="Execute"/> when it is idle. Nothing runs between changes.</summary>
    private readonly ExternalEvent? _statusEvent;
    private bool _iconsDrawn;

    public StatusRibbon(UIControlledApplication app, AgentLog log)
    {
        _app = app;
        _log = log;

        try { app.CreateRibbonTab(TabName); } catch { /* already exists */ }
        var panel = app.CreateRibbonPanel(TabName, "Hub Link");
        string assembly = Assembly.GetExecutingAssembly().Location;

        // Nothing is drawn while Revit starts: the buttons get their pictures on Revit's first idle moment.
        var data = new PushButtonData("NexusStatus", "Hub:\nStarting", assembly, typeof(ShowStatusCommand).FullName)
        {
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            ToolTip = "Nexus agent status. Click for details or to show the Nexus hub.",
        };
        _button = (PushButton)panel.AddItem(data);

        try
        {
            AddToolsPanel(app, assembly);
        }
        catch (Exception ex)
        {
            _log.Error("Could not create the Tools panel.", ex);
        }

        try
        {
            AddFabricationPanel(app, assembly);
        }
        catch (Exception ex)
        {
            _log.Error("Could not create the Fabrication panel.", ex);
        }

        try { _statusEvent = ExternalEvent.Create(this); } catch (Exception ex) { _log.Warn("The ribbon will not show the hub status.", ex); }
        app.Idling += OnFirstIdle;
        try { app.ThemeChanged += OnThemeChanged; } catch { /* older Revit */ }
    }

    /// <summary>Revit is up and idle for the first time: draw the buttons' pictures, then stop listening to Idling for good.</summary>
    private void OnFirstIdle(object? sender, IdlingEventArgs e)
    {
        try { _app.Idling -= OnFirstIdle; } catch { /* ignored */ }
        DrawIcons();
        ApplyStatus();
    }

    private void DrawIcons()
    {
        if (_iconsDrawn) return;
        _iconsDrawn = true;
        try
        {
            BuildIcons();
            _button.LargeImage = _icons[_state];
            _button.Image = _small[_state];
            ApplyToolIcons(IsDark());
        }
        catch (Exception ex)
        {
            _log.Warn("Could not draw the ribbon icons.", ex);
        }
    }

    /// <summary>Tools: Bulk Rename.</summary>
    private void AddToolsPanel(UIControlledApplication app, string assembly)
    {
        var panel = app.CreateRibbonPanel(TabName, "Tools");
        var rename = (PushButton)panel.AddItem(new PushButtonData("NexusBulkRename", "Bulk\nRename", assembly,
            typeof(Rename.BulkRenameCommand).FullName)
        {
            ToolTip = "Rename many views, sheets, levels, grids, rooms, families, types, materials and more at once: " +
                      "find and replace, capitals, remove and add text, numbering, parameter values ({Level}, {Sheet Number}...), with a live preview. One undo puts every name back.",
        });
        _tools.Add((rename, BulkRename));
    }

    /// <summary>Fabrication: browse the database, reload it, export parts to a MAJ job, open the parts in the hub.</summary>
    private void AddFabricationPanel(UIControlledApplication app, string assembly)
    {
        var panel = app.CreateRibbonPanel(TabName, "Fabrication");
        var database = (PushButton)panel.AddItem(new PushButtonData("NexusFabDatabase", "Fabrication\nDatabase", assembly,
            typeof(Fabrication.FabricationDatabaseCommand).FullName)
        {
            ToolTip = "Browse the fabrication database loaded in this model (services, materials, specifications, insulation, custom data, part statuses, ancillaries), reload it, or export parts to Fabrication CADmep.",
        });
        _tools.Add((database, Database));

        var spools = (PushButton)panel.AddItem(new PushButtonData("NexusFabSpools", "Spools &\nPackages", assembly,
            typeof(Fab.FabCommand).FullName)
        {
            ToolTip = "Fabrication for Fabrication parts and Revit pipe and duct alike: cut runs into spools by rules, group spools into packages, " +
                      "track them through the shop and field, make cut lists, material lists, QR labels and spool sheets, and place hangers and sleeves.",
        });
        _tools.Add((spools, Spools));

        var reload = new PushButtonData("NexusFabReload", "Reload", assembly, typeof(Fabrication.FabricationReloadCommand).FullName)
        {
            ToolTip = "Reload the fabrication configuration: pull changes made in Fabrication CADmep (services, item files, custom data) into this model.",
        };
        var export = new PushButtonData("NexusFabExport", "Export MAJ", assembly, typeof(Fabrication.FabricationExportCommand).FullName)
        {
            ToolTip = "Save the selected fabrication parts (or every fabrication part in the active view) as a MAJ job for CADmep, ESTmep or CAMduct.",
        };
        var hub = new PushButtonData("NexusFabHub", "Parts in Nexus", assembly, typeof(Fabrication.FabricationPartsInHubCommand).FullName)
        {
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            ToolTip = "Open the Fabrication parts view in the Nexus hub to compare and bulk-edit item numbers, spools, statuses, specifications and custom data.",
        };
        var stacked = panel.AddStackedItems(reload, export, hub);
        _tools.Add(((PushButton)stacked[0], Reload));
        _tools.Add(((PushButton)stacked[1], Export));
        _tools.Add(((PushButton)stacked[2], OpenInHub));
    }

    // Segoe Fluent Icons / MDL2 glyphs.
    private const string BulkRename = "\uE8AC";  // Rename
    private const string Database = "\uE8F1";   // Library
    private const string Spools = "\uE7B8";     // Package
    private const string Reload = "\uE72C";     // Refresh
    private const string Export = "\uEDE1";     // Export
    private const string OpenInHub = "\uE8A7";  // Open in new window

    private void ApplyToolIcons(bool dark)
    {
        foreach (var (button, glyph) in _tools)
        {
            button.LargeImage = RibbonIcons.Glyph(glyph, 32, dark);
            button.Image = RibbonIcons.Glyph(glyph, 16, dark);
        }
    }

    private static bool IsDark()
    {
        try { return UIThemeManager.CurrentTheme == UITheme.Dark; }
        catch { return false; }
    }

    /// <summary>White N on Revit's dark theme, black N on the light theme.</summary>
    private void BuildIcons()
    {
        bool dark = IsDark();
        foreach (AgentState s in Compat.EnumValues<AgentState>())
        {
            _icons[s] = RibbonIcons.Create(s, 32, dark);
            _small[s] = RibbonIcons.Create(s, 16, dark);
        }
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        if (!_iconsDrawn) return; // not drawn yet: they will be drawn for the theme in use
        _iconsDrawn = false;
        DrawIcons();
    }

    /// <summary>Called from any thread.</summary>
    public void OnStatusChanged(AgentStatus status)
    {
        _pending = status;
        try { _statusEvent?.Raise(); } catch { /* shown with the next change */ }
    }

    /// <summary>Revit calls this on its own thread, when idle, after a status change.</summary>
    public void Execute(UIApplication app)
    {
        DrawIcons();
        ApplyStatus();
    }

    public string GetName() => "Nexus hub status";

    private void ApplyStatus()
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
            if (!_iconsDrawn) return;
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
        try { _app.Idling -= OnFirstIdle; } catch { /* ignored */ }
        try { _statusEvent?.Dispose(); } catch { /* ignored */ }
        try { _app.ThemeChanged -= OnThemeChanged; } catch { /* ignored */ }
    }
}
