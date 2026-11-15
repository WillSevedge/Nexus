using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using Nexus.Agent;
using Nexus.Fab;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Grid = System.Windows.Controls.Grid;
using ParameterMap = Nexus.Fab.ParameterMap;

namespace Nexus.Agent.Revit.Fab;

/// <summary>
/// The Fabrication window: spools by rules, packages, tracking steps, cut lists, materials, labels, spool sheets,
/// hangers and sleeves, for Fabrication parts and Revit pipe and duct alike. Shown modally from an external command,
/// so the buttons call the Revit API directly; each action is one undo step. The layout is an embedded XAML file
/// loaded at run time (compiled XAML can fail to load in add-ins that Revit isolates).
/// </summary>
internal sealed class FabWindow
{
    private sealed class JointChoice
    {
        public string Name { get; init; } = "";
        public int Count { get; init; }
        public bool IsChecked { get; set; }
        public string CountText => $"   {Count}";
    }

    private sealed class BarRow
    {
        public int Number { get; init; }
        public string Material { get; init; } = "";
        public string Size { get; init; } = "";
        public string Stock { get; init; } = "";
        public string Cuts { get; init; } = "";
        public string Drop { get; init; } = "";
    }

    private sealed record Choice(string Name, ElementId Id);

    private readonly FabSession _session;
    private readonly AgentLog _log;
    private readonly Window _window;
    private readonly ObservableCollection<JointChoice> _joints = new();
    private readonly ObservableCollection<FabPackage> _packages = new();
    private readonly ObservableCollection<FabStatus> _steps = new();
    private readonly ObservableCollection<StockLength> _stock = new();
    private readonly ObservableCollection<HangerRule> _hangers = new();
    private readonly ObservableCollection<ParameterMap> _maps = new();
    private CutPlan? _cuts;
    private List<BomLine> _bom = new();

    private readonly ComboBox _scope, _packageCombo, _stepCombo, _scanStep, _colour, _titleBlock, _template;
    private readonly DataGrid _spools, _history, _cutsGrid, _barsGrid, _bomGrid, _packagesGrid;
    private readonly TextBlock _summary, _message, _cutSummary;

    public FabWindow(UIDocument uidoc, AgentLog log)
    {
        _log = log;
        _session = new FabSession(uidoc, log);
        _window = Load();
        try { new WindowInteropHelper(_window).Owner = uidoc.Application.MainWindowHandle; } catch { /* centre on screen */ }
        _window.Title = "Nexus - Fabrication - " + uidoc.Document.Title;

        _scope = Find<ComboBox>("ScopeCombo");
        _packageCombo = Find<ComboBox>("PackageCombo");
        _stepCombo = Find<ComboBox>("StepCombo");
        _scanStep = Find<ComboBox>("ScanStepCombo");
        _colour = Find<ComboBox>("ColourCombo");
        _titleBlock = Find<ComboBox>("TitleBlockCombo");
        _template = Find<ComboBox>("TemplateCombo");
        _spools = Find<DataGrid>("SpoolsGrid");
        _history = Find<DataGrid>("HistoryGrid");
        _cutsGrid = Find<DataGrid>("CutsGrid");
        _barsGrid = Find<DataGrid>("BarsGrid");
        _bomGrid = Find<DataGrid>("BomGrid");
        _packagesGrid = Find<DataGrid>("PackagesGrid");
        _summary = Find<TextBlock>("SummaryText");
        _message = Find<TextBlock>("MessageText");
        _cutSummary = Find<TextBlock>("CutSummary");

        int selected = uidoc.Selection.GetElementIds().Count;
        _scope.ItemsSource = new[] { $"Selected in Revit ({selected})", "The active view", "The whole model" };
        _scope.SelectedIndex = selected > 0 ? 0 : 1;
        _scope.SelectionChanged += (_, _) => Do(Read);
        Click("ReadButton", Read);
        Click("CloseButton", () => _window.Close());

        Find<ListBox>("JointsList").ItemsSource = _joints;
        Click("PreviewButton", Preview);
        Click("ApplyButton", Apply);
        Click("SelectButton", () => Say($"Selected {_session.Select(Picked())} piece(s) in Revit."));
        Click("ClearButton", ClearSpools);
        Click("AssignButton", Assign);
        Click("StepButton", () => SetStep(Picked(), _stepCombo.SelectedItem as string ?? ""));

        _packagesGrid.ItemsSource = _packages;
        Click("SavePackagesButton", SavePackages);
        Click("PackageStepButton", PackageStep);
        Click("PackageSelectButton", () =>
        {
            if (_packagesGrid.SelectedItem is FabPackage package) Say($"Selected {_session.Select(SpoolsIn(package.Name))} piece(s) of package {package.Name}.");
        });

        Find<DataGrid>("StepsGrid").ItemsSource = _steps;
        Click("SaveStepsButton", SaveSteps);
        _colour.ItemsSource = new[] { "Step", "Spool", "Package" };
        _colour.SelectedIndex = 0;
        Click("ColourButton", () => Say($"Coloured {_session.Colour((string)_colour.SelectedItem == "Step" ? "Status" : (string)_colour.SelectedItem)} piece(s) in the active view. Clear colours puts them back."));
        Click("ClearColourButton", () => Say($"Cleared the colours of {_session.ClearColours()} piece(s) in the active view."));
        var scan = Find<TextBox>("ScanBox");
        scan.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            string spool = FabCode.SpoolOf(scan.Text);
            scan.Clear();
            if (spool.Length > 0) Do(() => SetStep(new[] { spool }, _scanStep.SelectedItem as string ?? ""));
        };
        Click("HistoryButton", () => SaveCsv("Fabrication history", FabCsv.Write(new[] { "When", "Who", "What", "Changed", "From", "To" },
            _session.Project.History.Select(h => new[] { h.When, h.Who, h.What, h.Field, h.From, h.To }))));

        Find<DataGrid>("MapGrid").ItemsSource = _maps;
        var values = Find<ListBox>("ValuesList");
        values.MouseDoubleClick += (_, _) =>
        {
            // A new line for the value, to a parameter of the same name: the name on the right is changed as needed.
            if (values.SelectedItem is string name && !_maps.Any(m => m.From.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
                _maps.Add(new ParameterMap { From = name, To = name });
        };
        Click("SaveMapButton", () =>
        {
            SaveRules();
            Fill(_maps, _session.Project.Mappings);
            Say($"{_session.Project.Mappings.Count} mapping line(s) saved in the model.");
        });
        Click("WriteMapButton", WriteMapping);

        Find<DataGrid>("StockGrid").ItemsSource = _stock;
        Click("CutButton", MakeCutList);
        Click("CutExportButton", ExportCutList);
        Click("BomButton", MakeBom);
        Click("BomExportButton", () =>
        {
            if (_bom.Count == 0) MakeBom();
            SaveCsv("Materials", FabCsv.Write(new[] { "Category", "Description", "Size", "Material", "Qty", "Length", "Weight (lb)" },
                _bom.Select(l => new[] { l.Category, l.Description, l.Size, l.Material, l.Quantity.ToString(CultureInfo.InvariantCulture), l.Length, l.Weight })));
        });

        Click("LabelsButton", PrintLabels);
        var blocks = SpoolSheets.TitleBlocks(_session.Document).Select(b => new Choice(b.Name, b.Id)).ToList();
        _titleBlock.ItemsSource = blocks;
        _titleBlock.SelectedItem = blocks.FirstOrDefault(b => b.Name == _session.Project.SheetTitleBlock) ?? blocks.FirstOrDefault();
        var templates = new[] { new Choice("(none)", ElementId.InvalidElementId) }.Concat(SpoolSheets.ViewTemplates(_session.Document).Select(t => new Choice(t.Name, t.Id))).ToList();
        _template.ItemsSource = templates;
        _template.SelectedItem = templates.FirstOrDefault(t => t.Name == _session.Project.SheetViewTemplate) ?? templates[0];
        Click("SheetsButton", MakeSheets);

        Find<DataGrid>("HangersGrid").ItemsSource = _hangers;
        Find<ListBox>("HangerTypesList").ItemsSource = FabPlacement.HangerTypes(_session.Document);
        Click("SaveHangersButton", () =>
        {
            SaveRules();
            Say("Hanger rules saved in the model.");
        });
        Click("HangersButton", PlaceHangers);
        Click("SleevesButton", PlaceSleeves);
        Click("PointsButton", () => SaveCsv("Layout points", FabPlacement.LayoutPoints(_session.Document)));

        ShowProject();
        Do(Read);
    }

    public void ShowDialog() => _window.ShowDialog();

    // ------------------------------------------------------------------ plumbing

    private static Window Load()
    {
        var assembly = typeof(FabWindow).Assembly;
        string resource = assembly.GetManifestResourceNames().First(n => n.EndsWith("FabWindow.xaml", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return (Window)XamlReader.Load(stream);
    }

    private T Find<T>(string name) where T : class => _window.FindName(name) as T ?? throw new InvalidOperationException($"Layout part '{name}' is missing.");

    private void Click(string button, Action action) => Find<Button>(button).Click += (_, _) => Do(action);

    /// <summary>Runs an action: a failure is said in the window (and logged), and nothing is left half done (one transaction).</summary>
    private void Do(Action action)
    {
        try
        {
            _window.Cursor = Cursors.Wait;
            action();
        }
        catch (Exception ex)
        {
            _log.Error("Fabrication: " + ex.Message, ex);
            Say("That did not work: " + ex.Message, bad: true);
        }
        finally
        {
            _window.Cursor = null;
        }
    }

    private void Say(string text, bool bad = false)
    {
        _message.Text = text;
        _message.Foreground = bad ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.Black;
    }

    private static double Number(TextBox box, double fallback = 0) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
        || double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;

    private static string Text(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private void SaveCsv(string name, string csv)
    {
        var dialog = new SaveFileDialog { Filter = "CSV for Excel (*.csv)|*.csv", FileName = $"{name} {DateTime.Now:yyyy-MM-dd}.csv" };
        if (dialog.ShowDialog(_window) != true) return;
        File.WriteAllText(dialog.FileName, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Say($"Saved {Path.GetFileName(dialog.FileName)}.");
    }

    // ------------------------------------------------------------------ project ⇄ window

    private void ShowProject()
    {
        var p = _session.Project;
        var r = p.Spooling;
        Find<TextBox>("MaxLengthBox").Text = Text(r.MaxLengthFt);
        Find<TextBox>("MaxWeightBox").Text = Text(r.MaxWeightLb);
        Find<TextBox>("MaxPartsBox").Text = r.MaxParts.ToString(CultureInfo.CurrentCulture);
        Find<TextBox>("MinPartsBox").Text = r.MinParts.ToString(CultureInfo.CurrentCulture);
        Find<TextBox>("NameBox").Text = r.Name;
        Find<TextBox>("StartBox").Text = r.Start.ToString(CultureInfo.CurrentCulture);
        Find<CheckBox>("LooseBox").IsChecked = r.AccessoriesAreLoose;
        Find<CheckBox>("ServicesBox").IsChecked = r.KeepServicesApart;
        Find<CheckBox>("LevelsBox").IsChecked = r.KeepLevelsApart;
        Find<CheckBox>("HangersBox").IsChecked = r.IncludeHangers;
        Find<CheckBox>("KeepBox").IsChecked = r.KeepExisting;
        Find<TextBox>("KerfBox").Text = Text(p.KerfIn);
        Find<TextBox>("ClearanceBox").Text = Text(p.Sleeves.ClearanceIn);
        Find<TextBox>("ExtendBox").Text = Text(p.Sleeves.ExtendIn);
        Find<TextBox>("SleeveSizesBox").Text = string.Join(", ", p.Sleeves.SizesIn.Select(Text));
        Find<CheckBox>("WallsBox").IsChecked = p.Sleeves.Walls;
        Find<CheckBox>("FloorsBox").IsChecked = p.Sleeves.Floors;
        Fill(_packages, p.Packages);
        Fill(_steps, p.Statuses);
        Fill(_stock, p.Stock);
        Fill(_hangers, p.Hangers);
        Fill(_maps, p.Mappings);
        Find<CheckBox>("AddParamsBox").IsChecked = p.AddMissingParameters;
        ShowLists();
    }

    private static void Fill<T>(ObservableCollection<T> shown, IEnumerable<T> items)
    {
        shown.Clear();
        foreach (var item in items) shown.Add(item);
    }

    /// <summary>The choices that come from the project (packages, steps) and the history.</summary>
    private void ShowLists()
    {
        var p = _session.Project;
        string package = _packageCombo.Text;
        _packageCombo.ItemsSource = p.Packages.Select(x => x.Name).ToList();
        _packageCombo.Text = package;
        var steps = p.Statuses.Select(s => s.Name).ToList();
        foreach (var combo in new[] { _stepCombo, _scanStep })
        {
            var was = combo.SelectedItem as string;
            combo.ItemsSource = steps;
            combo.SelectedItem = was is not null && steps.Contains(was) ? was : steps.FirstOrDefault();
        }
        _history.ItemsSource = p.History.AsEnumerable().Reverse().ToList();
    }

    /// <summary>The window's rules into the project (not saved to the model yet).</summary>
    private void ReadRules()
    {
        var p = _session.Project;
        var r = p.Spooling;
        r.FieldJoints = _joints.Where(j => j.IsChecked).Select(j => j.Name)
            .Concat(Find<TextBox>("OtherJointsBox").Text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        r.MaxLengthFt = Math.Max(0, Number(Find<TextBox>("MaxLengthBox")));
        r.MaxWeightLb = Math.Max(0, Number(Find<TextBox>("MaxWeightBox")));
        r.MaxParts = (int)Math.Max(0, Number(Find<TextBox>("MaxPartsBox")));
        r.MinParts = (int)Math.Max(1, Number(Find<TextBox>("MinPartsBox"), 2));
        r.Name = Find<TextBox>("NameBox").Text.Trim();
        r.Start = (int)Math.Max(0, Number(Find<TextBox>("StartBox"), 1));
        r.AccessoriesAreLoose = Find<CheckBox>("LooseBox").IsChecked == true;
        r.KeepServicesApart = Find<CheckBox>("ServicesBox").IsChecked == true;
        r.KeepLevelsApart = Find<CheckBox>("LevelsBox").IsChecked == true;
        r.IncludeHangers = Find<CheckBox>("HangersBox").IsChecked == true;
        r.KeepExisting = Find<CheckBox>("KeepBox").IsChecked == true;
        p.KerfIn = Math.Max(0, Number(Find<TextBox>("KerfBox"), 0.125));
        p.Stock = _stock.Where(s => s.LengthFt > 0).ToList();
        if (p.Stock.Count == 0) p.Stock.Add(new StockLength());
        p.Hangers = _hangers.Where(h => h.SpacingFt > 0).ToList();
        Find<DataGrid>("MapGrid").CommitEdit(DataGridEditingUnit.Row, true);
        p.Mappings = ParameterMapper.Clean(_maps);
        p.AddMissingParameters = Find<CheckBox>("AddParamsBox").IsChecked == true;
        p.Sleeves.ClearanceIn = Math.Max(0, Number(Find<TextBox>("ClearanceBox"), 1));
        p.Sleeves.ExtendIn = Math.Max(0, Number(Find<TextBox>("ExtendBox")));
        p.Sleeves.Walls = Find<CheckBox>("WallsBox").IsChecked == true;
        p.Sleeves.Floors = Find<CheckBox>("FloorsBox").IsChecked == true;
        p.Sleeves.SizesIn = Find<TextBox>("SleeveSizesBox").Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0)
            .Where(v => v > 0).OrderBy(v => v).ToList();
        if (_titleBlock.SelectedItem is Choice block) p.SheetTitleBlock = block.Name;
        if (_template.SelectedItem is Choice template) p.SheetViewTemplate = template.Id == ElementId.InvalidElementId ? "" : template.Name;
    }

    private void SaveRules()
    {
        ReadRules();
        _session.SaveProject();
    }

    // ------------------------------------------------------------------ reading and spools

    private void Read()
    {
        var scope = _scope.SelectedIndex switch { 0 => FabScope.Selection, 1 => FabScope.ActiveView, _ => FabScope.WholeModel };
        _session.Collect(scope);
        var items = _session.Items;
        var chosen = new HashSet<string>(_session.Project.Spooling.FieldJoints, StringComparer.OrdinalIgnoreCase);
        var types = _session.JointTypes();
        _joints.Clear();
        foreach (var (type, count) in types) _joints.Add(new JointChoice { Name = type, Count = count, IsChecked = chosen.Contains(type) });
        // Field joints saved earlier that are not in what was read stay, in the box of other joints.
        Find<TextBox>("OtherJointsBox").Text = string.Join(", ", chosen.Where(c => !types.Any(t => t.Type.Equals(c, StringComparison.OrdinalIgnoreCase))));

        int fabricated = items.Count(i => i.Category.IndexOf("Fabrication", StringComparison.OrdinalIgnoreCase) >= 0);
        _summary.Text = items.Count == 0
            ? "Nothing to fabricate here: no pipe, duct, conduit, cable tray or Fabrication parts."
            : $"{items.Count} piece(s): {fabricated} Fabrication part(s), {items.Count - fabricated} Revit pipe, duct and fittings · " +
              $"{items.Where(i => i.Spool.Length > 0).Select(i => i.Spool).Distinct(StringComparer.OrdinalIgnoreCase).Count()} spool(s) in the model";
        ShowSpools();
        Find<ListBox>("ValuesList").ItemsSource = FabMapping.ValueNames(_session.Document, items);
        Say(items.Count == 0 ? "Select runs in Revit or open a view that shows them, then Read again." : "Set the rules on the left, then Preview spools.");
    }

    private void ShowSpools() => _spools.ItemsSource = _session.Rows();

    private List<string> Picked() => _spools.SelectedItems.OfType<SpoolRow>().Select(r => r.Name).ToList();

    private List<SpoolRow> PickedRows() => _spools.SelectedItems.OfType<SpoolRow>().ToList();

    private void Preview()
    {
        ReadRules();
        var plan = _session.PlanSpools();
        ShowSpools();
        int fresh = plan.Spools.Count(s => !s.Existing);
        var cut = plan.Splits.Select(s => $"{s.Value} by {s.Key}").ToList();
        Say($"{fresh} new spool(s), {plan.Loose.Count} loose piece(s)" + (cut.Count > 0 ? $" · cut to fit: {string.Join(", ", cut)}" : "") +
            ". Nothing is changed until Apply to model.");
    }

    private void Apply()
    {
        if (_session.Plan is null) Preview();
        var (changed, refused) = _session.ApplySpools();
        _session.Collect(_scope.SelectedIndex switch { 0 => FabScope.Selection, 1 => FabScope.ActiveView, _ => FabScope.WholeModel });
        ShowSpools();
        ShowLists();
        Say($"Spools written to {changed} piece(s)" + (refused > 0 ? $"; Revit refused {refused} (worksets owned by others, or pieces that cannot be in a spool)" : "") +
            ". One undo in Revit puts them back. The spool is in each piece's Nexus Spool parameter.");
    }

    private void ClearSpools()
    {
        var names = Picked();
        if (names.Count == 0)
        {
            Say("Select the spools to clear in the list first.");
            return;
        }
        if (MessageBox.Show(_window, $"Take the pieces of {names.Count} spool(s) out of their spool, package and step?", "Nexus Fabrication",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int n = _session.ClearSpools(names);
        Read();
        Say($"Cleared {names.Count} spool(s) ({n} piece(s)).");
    }

    private void Assign()
    {
        var names = Picked();
        string package = _packageCombo.Text.Trim();
        if (names.Count == 0)
        {
            Say("Select the spools to put in the package first.");
            return;
        }
        if (package.Length > 0 && _session.Project.Package(package) is null)
        {
            var made = new FabPackage
            {
                Name = package, Status = _session.Project.Statuses.FirstOrDefault()?.Name ?? "",
                CreatedBy = _session.User, Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };
            _session.Project.Packages.Add(made);
            _packages.Add(made);
        }
        int n = _session.SetValue(names, FabStore.PackageId, "Package", package);
        Read();
        ShowLists();
        Say(package.Length == 0 ? $"Took {names.Count} spool(s) out of their package." : $"Put {names.Count} spool(s) ({n} piece(s)) in package {package}.");
    }

    private void SetStep(IReadOnlyCollection<string> names, string step)
    {
        if (names.Count == 0 || step.Length == 0)
        {
            Say("Select spools and a step first.");
            return;
        }
        int n = _session.SetValue(names, FabStore.StatusId, "Step", step);
        Read();
        ShowLists();
        Say(n == 0 ? $"No spool named {string.Join(", ", names)} is in the model." : $"{string.Join(", ", names.Take(4))}{(names.Count > 4 ? "…" : "")}: now {step} ({n} piece(s)).", bad: n == 0);
    }

    // ------------------------------------------------------------------ packages and steps

    private List<string> SpoolsIn(string package) =>
        FabCollector.Index(_session.Document).Where(x => x.Package.Equals(package, StringComparison.OrdinalIgnoreCase) && x.Spool.Length > 0)
            .Select(x => x.Spool).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void SavePackages()
    {
        _packagesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var named = _packages.Where(p => p.Name.Trim().Length > 0).ToList();
        foreach (var package in named.Where(p => p.Created.Length == 0))
        {
            package.CreatedBy = _session.User;
            package.Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        _session.Project.Packages = named.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        SaveRules();
        Fill(_packages, _session.Project.Packages);
        ShowLists();
        Say($"{_session.Project.Packages.Count} package(s) saved in the model.");
    }

    private void PackageStep()
    {
        _packagesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_packagesGrid.SelectedItem is not FabPackage package || package.Status.Trim().Length == 0)
        {
            Say("Select a package that has a step.");
            return;
        }
        SavePackages();
        SetStep(SpoolsIn(package.Name), package.Status.Trim());
    }

    private void SaveSteps()
    {
        Find<DataGrid>("StepsGrid").CommitEdit(DataGridEditingUnit.Row, true);
        var steps = _steps.Where(s => s.Name.Trim().Length > 0).GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (steps.Count == 0)
        {
            Say("Keep at least one step.");
            return;
        }
        _session.Project.Statuses = steps;
        SaveRules();
        ShowLists();
        Say($"{steps.Count} step(s) saved in the model.");
    }

    // ------------------------------------------------------------------ parameter mapping

    private void WriteMapping()
    {
        SaveRules();
        Fill(_maps, _session.Project.Mappings);
        if (!_session.Project.Mappings.Any(m => m.Enabled))
        {
            Say("Add a mapping line first: double-click a value on the left, then name the Revit parameter it goes to.");
            return;
        }
        var items = Chosen(out string what);
        // The spool totals need every piece of each spool, not only the pieces picked.
        var result = FabMapping.Write(_session, items);
        var notes = new List<string>();
        if (result.AddedParameters.Count > 0) notes.Add($"added to the model: {string.Join(", ", result.AddedParameters)}");
        if (result.Refused > 0) notes.Add($"{result.Refused} value(s) Revit would not take (read-only parameters, text in a number parameter, worksets owned by others)");
        foreach (var (parameter, count) in result.NoParameter.Take(3)) notes.Add($"{count} piece(s) have no parameter \"{parameter}\"");
        if (result.NoValue.Count > 0) notes.Add($"no value named {string.Join(", ", result.NoValue.Take(4))} on some pieces (left blank)");
        Say($"Wrote {result.Written} value(s) to {what}; {result.Unchanged} were already right." + (notes.Count > 0 ? "  " + string.Join("; ", notes) + "." : "") +
            " One undo in Revit puts them back.", bad: result.Written + result.Unchanged == 0);
        Read();
    }

    // ------------------------------------------------------------------ cut list, materials, labels

    /// <summary>The pieces of the spools selected on the Spools tab, or everything read when none is selected.</summary>
    private List<FabItem> Chosen(out string what)
    {
        var rows = PickedRows();
        what = rows.Count > 0 ? $"{rows.Count} selected spool(s)" : "everything read";
        return rows.Count > 0 ? rows.SelectMany(r => r.Items).ToList() : _session.Items;
    }

    private void MakeCutList()
    {
        Find<DataGrid>("StockGrid").CommitEdit(DataGridEditingUnit.Row, true);
        ReadRules();
        var items = Chosen(out string what);
        _cuts = CutList.Build(items, _session.Project.Stock, _session.Project.KerfIn);
        _cutsGrid.ItemsSource = _cuts.Lines;
        _barsGrid.ItemsSource = _cuts.Bars.Select((b, i) => new BarRow
        {
            Number = i + 1, Material = b.Material, Size = b.Size, Stock = FabMeasure.FeetInches(b.StockFt), Cuts = b.CutsText, Drop = FabMeasure.FeetInches(b.DropFt),
        }).ToList();
        _cutSummary.Text = _cuts.Lines.Count == 0
            ? $"No straights in {what}."
            : $"{_cuts.Lines.Sum(l => l.Quantity)} cut(s) from {_cuts.Bars.Count} stock length(s): {FabMeasure.FeetInches(_cuts.CutFt)} of {FabMeasure.FeetInches(_cuts.StockFt)} used ({_cuts.YieldPercent:0.#}%)." +
              (_cuts.TooLong.Count > 0 ? $"\n{_cuts.TooLong.Sum(l => l.Quantity)} piece(s) are longer than their stock length: {string.Join(", ", _cuts.TooLong.Select(l => $"{l.Size} {l.Length}"))}." : "");
        Say($"Cut list of {what}.");
    }

    private void ExportCutList()
    {
        if (_cuts is null) MakeCutList();
        if (_cuts is null) return;
        var rows = new List<string[]>();
        foreach (var l in _cuts.Lines) rows.Add(new[] { "Cut", l.Service, l.Material, l.Size, l.Length, l.Quantity.ToString(CultureInfo.InvariantCulture), l.Spools, "" });
        int n = 1;
        foreach (var b in _cuts.Bars) rows.Add(new[] { "Bar " + n++, "", b.Material, b.Size, FabMeasure.FeetInches(b.StockFt), "1", b.CutsText, FabMeasure.FeetInches(b.DropFt) });
        SaveCsv("Cut list", FabCsv.Write(new[] { "Line", "Service", "Material", "Size", "Length", "Qty", "Spools / cuts", "Left over" }, rows));
    }

    private void MakeBom()
    {
        var items = Chosen(out string what);
        _bom = Bom.Build(items);
        _bomGrid.ItemsSource = _bom;
        Say($"Materials of {what}: {_bom.Count} line(s).");
    }

    private void PrintLabels()
    {
        var rows = PickedRows();
        if (rows.Count == 0) rows = _session.Rows().Where(r => r.State.StartsWith("In model", StringComparison.Ordinal)).ToList();
        rows = rows.Where(r => !r.State.StartsWith("New", StringComparison.Ordinal)).ToList();
        if (rows.Count == 0)
        {
            Say("No spools in the model to label: apply the spools first.");
            return;
        }
        string project = _session.Document.ProjectInformation?.Name ?? _session.Document.Title;
        var labels = rows.Select(r => new SpoolLabel
        {
            Spool = r.Name, Package = r.Package, Project = project,
            Line1 = string.Join(" · ", new[] { r.Service, r.Level }.Where(v => v.Length > 0)),
            Line2 = string.Join(" · ", new[] { $"{r.Parts} pc", r.Longest, r.Weight }.Where(v => v.Length > 0)),
            Line3 = _session.Project.Package(r.Package) is { } p ? string.Join(" · ", new[] { p.Destination, p.Due.Length > 0 ? "due " + p.Due : "" }.Where(v => v.Length > 0)) : "",
        }).ToList();
        var document = FabLabels.Build(labels, Find<RadioButton>("LabelSheetRadio").IsChecked == true);
        Say(FabLabels.Print(document, "Nexus spool labels") ? $"Sent {labels.Count} label(s) to the printer." : "Printing cancelled.");
    }

    // ------------------------------------------------------------------ sheets, hangers, sleeves

    private void MakeSheets()
    {
        var rows = PickedRows().Where(r => !r.State.StartsWith("New", StringComparison.Ordinal)).ToList();
        if (rows.Count == 0)
        {
            Say("Select spools that are in the model on the Spools tab first (apply new spools before making their sheets).");
            return;
        }
        if (_titleBlock.SelectedItem is not Choice block)
        {
            Say("Load a title block family into the model first.");
            return;
        }
        SaveRules();
        var options = new SpoolSheetOptions
        {
            TitleBlock = block.Id,
            ViewTemplate = (_template.SelectedItem as Choice)?.Id ?? ElementId.InvalidElementId,
            View3D = Find<CheckBox>("Sheet3DBox").IsChecked == true, Front = Find<CheckBox>("SheetFrontBox").IsChecked == true,
            Top = Find<CheckBox>("SheetTopBox").IsChecked == true, PartsList = Find<CheckBox>("SheetListBox").IsChecked == true,
            Tags = Find<CheckBox>("SheetTagsBox").IsChecked == true, Dimensions = Find<CheckBox>("SheetDimsBox").IsChecked == true,
            TitleStrip = Math.Max(0, Math.Min(50, Number(Find<TextBox>("StripBox"), 18))) / 100,
        };
        var result = SpoolSheets.Create(_session, rows, options);
        Say($"Made {result.Sheets} sheet(s) with {result.Tags} tag(s) and {result.Dimensions} dimension(s)." +
            (result.Problems.Count > 0 ? "  " + string.Join("  ", result.Problems.Take(4)) + (result.Problems.Count > 4 ? "…" : "") : ""), bad: result.Sheets == 0);
    }

    private void PlaceHangers()
    {
        Find<DataGrid>("HangersGrid").CommitEdit(DataGridEditingUnit.Row, true);
        SaveRules();
        var runs = FabPlacement.Straights(_session.Document, _session.Items);
        var result = FabPlacement.PlaceHangers(_session, runs);
        Say(Outcome("hanger", runs.Count, result), bad: result.Placed == 0 && result.Problems.Count > 0);
    }

    private void PlaceSleeves()
    {
        SaveRules();
        var runs = FabPlacement.Straights(_session.Document, _session.Items);
        var result = FabPlacement.PlaceSleeves(_session, runs);
        Say(Outcome("sleeve", runs.Count, result), bad: result.Placed == 0 && result.Problems.Count > 0);
    }

    private static string Outcome(string what, int runs, PlacementResult result) =>
        $"Placed {result.Placed} {what}(s) on {runs} straight(s)" +
        (result.AlreadyThere > 0 ? $"; {result.AlreadyThere} were already there" : "") +
        (result.Skipped > 0 ? $"; {result.Skipped} left out" : "") + ". One undo in Revit takes them away." +
        (result.Problems.Count > 0 ? "  " + string.Join("  ", result.Problems.Take(3)) : "");
}
