using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nexus.Agent;
using Nexus.Rename;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace Nexus.Agent.Revit.Rename;

/// <summary>
/// Bulk Rename for Revit (like Bulk Rename Utility): pick what to rename, set the panels, watch the preview,
/// Rename. Shown modally from an external command, so the Rename button calls the Revit API directly; one
/// rename is one undo step. The layout is an embedded XAML file loaded at run time (no compiled XAML, which
/// can fail to load in add-ins that Revit isolates).
/// </summary>
internal sealed class BulkRenameWindow
{
    private const string LastRulesFile = "rename-last.json";

    private readonly UIDocument _uidoc;
    private readonly RenameSession _session;
    private readonly AgentLog _log;
    private readonly Window _window;
    private readonly ObservableCollection<TargetRow> _targets = new();
    private readonly ObservableCollection<PreviewRow> _rows = new();
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _refresh;
    private readonly RenamePresets _presets = new(RenamePresets.DefaultFolder);
    private readonly HashSet<ElementId> _selection;
    private RenameRules _rules;
    private List<RenameResult> _plan = new();
    private TextBox? _lastText;

    // Named parts of the layout.
    private readonly ComboBox _scope, _preset;
    private readonly DataGrid _grid;
    private readonly FrameworkElement _rulesPanel;
    private readonly TextBlock _summary;
    private readonly TextBox _search;
    private readonly CheckBox _changesOnly;
    private readonly Button _rename, _token;

    public BulkRenameWindow(UIDocument uidoc, AgentLog log)
    {
        _uidoc = uidoc;
        _log = log;
        _session = new RenameSession(uidoc.Document, log);
        _selection = new HashSet<ElementId>(uidoc.Selection.GetElementIds());
        _rules = LoadLastRules();

        _window = LoadLayout();
        ApplyTheme(_window);
        try { new WindowInteropHelper(_window).Owner = uidoc.Application.MainWindowHandle; } catch { /* centre on screen */ }

        T Find<T>(string name) where T : class => _window.FindName(name) as T ?? throw new InvalidOperationException($"Layout part '{name}' is missing.");
        _scope = Find<ComboBox>("ScopeCombo");
        _preset = Find<ComboBox>("PresetCombo");
        _grid = Find<DataGrid>("PreviewGrid");
        _rulesPanel = Find<FrameworkElement>("RulesPanel");
        _summary = Find<TextBlock>("SummaryText");
        _search = Find<TextBox>("SearchBox");
        _changesOnly = Find<CheckBox>("ChangesOnly");
        _rename = Find<Button>("RenameButton");
        _token = Find<Button>("TokenButton");
        Find<TextBlock>("DocText").Text = uidoc.Document.Title;

        // Choices with readable labels.
        Choices(Find<ComboBox>("NameModeCombo"), (NameMode.Keep, "Keep the name"), (NameMode.Remove, "Start empty"), (NameMode.Fixed, "Fixed text:"), (NameMode.Reverse, "Reverse"));
        Choices(Find<ComboBox>("CaseModeCombo"), (CaseMode.Same, "Same"), (CaseMode.Upper, "UPPER CASE"), (CaseMode.Lower, "lower case"), (CaseMode.Title, "Title Case"), (CaseMode.Sentence, "Sentence case"));
        Choices(Find<ComboBox>("CropCombo"), (CropMode.None, "No crop"), (CropMode.Before, "Crop before"), (CropMode.After, "Crop after"));
        Choices(Find<ComboBox>("NumberModeCombo"), (NumberingMode.None, "No numbers"), (NumberingMode.Prefix, "At the start"), (NumberingMode.Suffix, "At the end"), (NumberingMode.Insert, "Insert at"), (NumberingMode.Replace, "Number only"));
        Choices(Find<ComboBox>("NumberStyleCombo"), (NumberingStyle.Decimal, "1, 2, 3"), (NumberingStyle.UpperLetters, "A, B, C"), (NumberingStyle.LowerLetters, "a, b, c"));

        // What to rename.
        foreach (var target in RenameTargets.All)
        {
            var row = new TargetRow(target, _session.Count(target));
            row.CheckedChanged += Reload;
            _targets.Add(row);
        }
        var targetsView = new ListCollectionView(_targets);
        targetsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TargetRow.Group)));
        Find<ListBox>("TargetsList").ItemsSource = targetsView;

        _scope.ItemsSource = new[] { "The whole model", $"Selected in Revit ({_selection.Count})" };
        _scope.SelectedIndex = 0;
        _scope.SelectionChanged += (_, _) => Reload();

        // Preview: numbering follows the list order (sort by clicking a column).
        _view = new ListCollectionView(_rows) { Filter = o => Visible((PreviewRow)o) };
        _grid.ItemsSource = _view;
        _grid.Sorting += (_, _) => _window.Dispatcher.BeginInvoke(new Action(ScheduleRefresh), DispatcherPriority.Background);
        _search.TextChanged += (_, _) => _view.Refresh();
        _changesOnly.Checked += (_, _) => _view.Refresh();
        _changesOnly.Unchecked += (_, _) => _view.Refresh();
        Find<Button>("SelectAllButton").Click += (_, _) => SetIncluded(true);
        Find<Button>("SelectNoneButton").Click += (_, _) => SetIncluded(false);

        // Rules: any change refreshes the preview (shortly after typing stops).
        _rulesPanel.DataContext = _rules;
        _refresh = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            Refresh();
        };
        _rulesPanel.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleRefresh()));
        _rulesPanel.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ScheduleRefresh()));
        _rulesPanel.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ScheduleRefresh()));
        _rulesPanel.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => ScheduleRefresh()));
        _rulesPanel.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.NewFocus is TextBox box) _lastText = box;
        }));

        _token.Click += (_, _) => ShowTokens();
        Find<Button>("ResetButton").Click += (_, _) => UseRules(new RenameRules());
        _rename.Click += (_, _) => Rename();
        Find<Button>("CloseButton").Click += (_, _) => _window.Close();
        Find<Button>("LoadPresetButton").Click += (_, _) => LoadPreset();
        Find<Button>("SavePresetButton").Click += (_, _) => SavePreset();
        Find<Button>("DeletePresetButton").Click += (_, _) => DeletePreset();
        _preset.ItemsSource = _presets.Names();
        _window.Closing += (_, _) => SaveLastRules();

        // Start with what is selected in Revit, else views.
        var start = _selection.Count > 0 ? SelectedKinds() : new[] { "views" };
        foreach (var t in _targets.Where(t => start.Contains(t.Target.Id))) t.IsChecked = true;
        if (_selection.Count > 0 && _targets.Any(t => t.IsChecked)) _scope.SelectedIndex = 1;
        Reload();
    }

    public void ShowDialog() => _window.ShowDialog();

    // ------------------------------------------------------------------ layout and theme

    private static Window LoadLayout()
    {
        var assembly = typeof(BulkRenameWindow).Assembly;
        string resource = assembly.GetManifestResourceNames().First(n => n.EndsWith("BulkRenameWindow.xaml", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return (Window)XamlReader.Load(stream);
    }

    /// <summary>Light or dark like Revit (Revit 2024+ themes).</summary>
    private static void ApplyTheme(Window window)
    {
        bool dark = false;
        try { dark = UIThemeManager.CurrentTheme == UITheme.Dark; } catch { /* light */ }
        var colors = dark
            ? new Dictionary<string, string>
            {
                ["WindowBg"] = "#1F1F1F", ["PanelBg"] = "#2B2B2B", ["PanelAlt"] = "#303030", ["HeaderBg"] = "#363636",
                ["Line"] = "#474747", ["Text"] = "#F2F2F2", ["TextDim"] = "#B0B0B0", ["Accent"] = "#2E7FD3",
                ["Selection"] = "#264F78", ["Good"] = "#6CCB5F", ["Bad"] = "#FF99A4", ["Warn"] = "#FCE100",
            }
            : new Dictionary<string, string>
            {
                ["WindowBg"] = "#F3F3F3", ["PanelBg"] = "#FFFFFF", ["PanelAlt"] = "#F9F9F9", ["HeaderBg"] = "#EEEEEE",
                ["Line"] = "#D6D6D6", ["Text"] = "#1B1B1B", ["TextDim"] = "#5C5C5C", ["Accent"] = "#0F6CBD",
                ["Selection"] = "#CCE4F7", ["Good"] = "#107C10", ["Bad"] = "#C42B1C", ["Warn"] = "#9D5D00",
            };
        foreach (var (key, hex) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            window.Resources[key] = brush;
        }
    }

    private static void Choices<T>(ComboBox combo, params (T Value, string Label)[] items) where T : struct, Enum
    {
        combo.ItemsSource = items.Select(i => new KeyValuePair<T, string>(i.Value, i.Label)).ToList();
        combo.SelectedValuePath = "Key";
        combo.DisplayMemberPath = "Value";
        // Re-apply the binding now the items are there.
        BindingOperations.GetBindingExpression(combo, Selector.SelectedValueProperty)?.UpdateTarget();
    }

    // ------------------------------------------------------------------ items and preview

    private IEnumerable<string> SelectedKinds()
    {
        var doc = _uidoc.Document;
        var kinds = new HashSet<string>();
        foreach (var id in _selection)
        {
            switch (doc.GetElement(id))
            {
                case ViewSheet: kinds.Add("sheet-number"); break;
                case ViewSchedule: kinds.Add("schedules"); break;
                case View v when v.IsTemplate: kinds.Add("templates"); break;
                case View: kinds.Add("views"); break;
                case Level: kinds.Add("levels"); break;
                case Autodesk.Revit.DB.Grid: kinds.Add("grids"); break;
                case Autodesk.Revit.DB.Architecture.Room: kinds.Add("room-name"); break;
                case Autodesk.Revit.DB.Mechanical.Space: kinds.Add("space-name"); break;
                case FamilySymbol: kinds.Add("family-types"); break;
                case Family: kinds.Add("families"); break;
                case Material: kinds.Add("materials"); break;
                case ElementType: kinds.Add("system-types"); break;
            }
        }
        return kinds;
    }

    private void Reload()
    {
        foreach (var r in _rows) r.IncludeChanged -= ScheduleRefresh;
        _rows.Clear();
        bool selectionOnly = _scope.SelectedIndex == 1;
        var items = _session.Items(_targets.Where(t => t.IsChecked).Select(t => t.Target));
        foreach (var item in items)
        {
            bool inScope = !selectionOnly || (item.Tag is RenameSource { ElementId: { } id } && _selection.Contains(id));
            var row = new PreviewRow(item, inScope);
            row.IncludeChanged += ScheduleRefresh;
            _rows.Add(row);
        }
        Refresh();
    }

    private bool Visible(PreviewRow row)
    {
        if (!row.InScope) return false;
        if (_changesOnly.IsChecked == true && !row.Changes) return false;
        string search = _search.Text.Trim();
        return search.Length == 0
               || row.Current.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
               || row.New.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
               || row.Detail.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void SetIncluded(bool include)
    {
        foreach (PreviewRow row in _view) row.Include = include;
    }

    private void ScheduleRefresh()
    {
        _refresh.Stop();
        _refresh.Start();
    }

    /// <summary>New names for every item, in the list's current sort order (numbering follows it).</summary>
    private void Refresh()
    {
        var ordered = new ListCollectionView(_rows);
        foreach (var sort in _view.SortDescriptions) ordered.SortDescriptions.Add(sort);
        var rows = ordered.Cast<PreviewRow>().ToList();
        var byKey = rows.ToDictionary(r => r.Item.Key);

        _plan = RenamePlanner.Plan(rows.Select(r => r.Item).ToList(), _rules, i => byKey[i.Key].Include && byKey[i.Key].InScope);
        foreach (var result in _plan) byKey[result.Item.Key].Show(result);
        if (_changesOnly.IsChecked == true || _search.Text.Length > 0) _view.Refresh();

        int rename = _plan.Count(p => p.WillRename);
        int bad = _plan.Count(p => p.Status is RenameStatus.Duplicate or RenameStatus.Invalid or RenameStatus.Empty);
        int locked = _plan.Count(p => p.Status == RenameStatus.Locked);
        int shown = rows.Count(r => r.InScope);
        _summary.Text = shown == 0
            ? "Tick what to rename on the left."
            : string.Join("   ·   ", new[]
            {
                $"{rename} of {shown} will be renamed",
                bad > 0 ? $"{bad} cannot (see Status)" : "",
                locked > 0 ? $"{locked} locked by other users" : "",
                _rules.IsEmpty ? "Turn on a panel below to start" : "",
            }.Where(s => s.Length > 0));
        _summary.SetResourceReference(TextBlock.ForegroundProperty, bad > 0 ? "Bad" : "Text");
        _rename.IsEnabled = rename > 0;
        _rename.Content = rename > 0 ? $"Rename {rename}" : "Rename";
    }

    // ------------------------------------------------------------------ rename

    private void Rename()
    {
        Refresh();
        int count = _plan.Count(p => p.WillRename);
        if (count == 0) return;
        int bad = _plan.Count(p => p.Status is RenameStatus.Duplicate or RenameStatus.Invalid or RenameStatus.Empty or RenameStatus.Locked);
        if (bad > 0 && MessageBox.Show(_window,
                $"{bad} item(s) cannot be renamed (see Status) and will keep their name. Rename the other {count}?",
                "Bulk Rename", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        RenameOutcome outcome;
        try
        {
            _window.Cursor = Cursors.Wait;
            outcome = _session.Apply(_plan);
        }
        catch (Exception ex)
        {
            _log.Error("Bulk Rename failed.", ex);
            MessageBox.Show(_window, "Nothing was renamed: " + ex.Message, "Bulk Rename", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            _window.Cursor = null;
        }

        Reload();
        string text = $"Renamed {outcome.Renamed}. One undo (Ctrl+Z in Revit) puts every name back.";
        if (outcome.Failed.Count > 0)
            text += $"   Revit refused {outcome.Failed.Count}: " + string.Join("; ", outcome.Failed.Take(3).Select(f => $"{f.Name} ({f.Reason})"))
                    + (outcome.Failed.Count > 3 ? "…" : "");
        _summary.Text = text;
        _summary.SetResourceReference(TextBlock.ForegroundProperty, outcome.Failed.Count > 0 ? "Warn" : "Good");
    }

    // ------------------------------------------------------------------ tokens

    private void ShowTokens()
    {
        var menu = new ContextMenu { PlacementTarget = _token, Placement = PlacementMode.Top };
        foreach (var token in RenameSession.CommonTokens)
            menu.Items.Add(TokenItem(token));
        var names = RenameSession.ParameterNames(_rows.Where(r => r.InScope).Select(r => r.Item));
        if (names.Count > 0)
        {
            var parameters = new MenuItem { Header = "Parameters" };
            foreach (var name in names) parameters.Items.Add(TokenItem(name));
            menu.Items.Add(new Separator());
            menu.Items.Add(parameters);
        }
        menu.IsOpen = true;
    }

    private MenuItem TokenItem(string name)
    {
        var item = new MenuItem { Header = "{" + name + "}" };
        item.Click += (_, _) => InsertToken("{" + name + "}");
        return item;
    }

    private void InsertToken(string token)
    {
        var box = _lastText;
        if (box is null)
        {
            _summary.Text = "Click in a text box of a panel first (Add › Prefix, Name › Fixed...), then insert the token.";
            return;
        }
        int at = Math.Min(box.CaretIndex, box.Text.Length);
        box.Text = box.Text.Insert(at, token);
        box.CaretIndex = at + token.Length;
        box.Focus();
    }

    // ------------------------------------------------------------------ presets

    private void UseRules(RenameRules rules)
    {
        _rules = rules;
        _rulesPanel.DataContext = _rules;
        Refresh();
    }

    private void LoadPreset()
    {
        string name = _preset.Text.Trim();
        var rules = name.Length > 0 ? _presets.Load(name) : null;
        if (rules is null)
        {
            _summary.Text = "Pick a saved preset to load.";
            return;
        }
        UseRules(rules);
        _summary.Text = $"Loaded preset \"{name}\".";
    }

    private void SavePreset()
    {
        string name = _preset.Text.Trim();
        if (name.Length == 0)
        {
            _summary.Text = "Type a name for the preset in the Preset box, then Save.";
            return;
        }
        _presets.Save(name, _rules);
        _preset.ItemsSource = _presets.Names();
        _preset.Text = name;
        _summary.Text = $"Saved preset \"{name}\".";
    }

    private void DeletePreset()
    {
        string name = _preset.Text.Trim();
        if (name.Length == 0) return;
        _presets.Delete(name);
        _preset.ItemsSource = _presets.Names();
        _preset.Text = "";
        _summary.Text = $"Deleted preset \"{name}\".";
    }

    private static RenameRules LoadLastRules()
    {
        try
        {
            string path = Path.Combine(RenamePresets.DefaultFolder, "..", LastRulesFile);
            if (File.Exists(path)) return RenamePresets.FromJson(File.ReadAllText(path));
        }
        catch { /* start fresh */ }
        return new RenameRules();
    }

    private void SaveLastRules()
    {
        try
        {
            string path = Path.Combine(RenamePresets.DefaultFolder, "..", LastRulesFile);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, RenamePresets.ToJson(_rules));
        }
        catch (Exception ex)
        {
            _log.Warn("Bulk Rename: could not keep the last settings.", ex);
        }
    }
}
