using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nexus.Agent;
using Nexus.Fab;

namespace Nexus.Agent.Revit.Fab;

/// <summary>A spool as the window lists it: planned (not in the model yet) or in the model.</summary>
internal sealed class SpoolRow
{
    public string Name { get; init; } = "";
    public string Service { get; init; } = "";
    public string Level { get; init; } = "";
    public int Parts { get; init; }
    public string Pipe { get; init; } = "";
    public string Longest { get; init; } = "";
    public string Weight { get; init; } = "";
    public string Package { get; init; } = "";
    public string Status { get; init; } = "";
    /// <summary>"New" until applied, then "In model".</summary>
    public string State { get; init; } = "";
    public List<FabItem> Items { get; init; } = new();
}

/// <summary>
/// The fabrication tools' work on one model: read the pieces, plan and apply spools, put spools in packages, move
/// them through the tracking steps, colour a view, and keep the project (rules, steps, packages, history) in the
/// model. Runs from a modal window, so it calls the Revit API directly; each action is one undo step.
/// </summary>
internal sealed class FabSession
{
    private readonly UIDocument _uidoc;
    private readonly AgentLog _log;

    public FabSession(UIDocument uidoc, AgentLog log)
    {
        _uidoc = uidoc;
        _log = log;
        Project = FabStore.Load(Document);
    }

    public Document Document => _uidoc.Document;
    public UIDocument UiDocument => _uidoc;
    public FabProject Project { get; }
    public List<FabItem> Items { get; private set; } = new();
    public SpoolPlan? Plan { get; private set; }
    public string User => Safe(() => Document.Application.Username) is { Length: > 0 } name ? name : Environment.UserName;

    public void Collect(FabScope scope)
    {
        Items = FabCollector.Collect(_uidoc, scope);
        Plan = null;
    }

    /// <summary>The kinds of joint in what was read, with how many there are: the choices for field joints.</summary>
    public List<(string Type, int Count)> JointTypes() =>
        Items.SelectMany(i => i.Joints.Select(j => j.Type)).Where(t => t.Length > 0)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, (g.Count() + 1) / 2))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Plans spools for what was read. Pieces of spools already released to the shop are left as they are.</summary>
    public SpoolPlan PlanSpools()
    {
        var locked = Items.Where(i => i.Spool.Length > 0 && Project.IsLocked(i.Status)).ToList();
        var lockedKeys = new HashSet<string>(locked.Select(i => i.Key), StringComparer.Ordinal);
        var plan = SpoolPlanner.Plan(Items.Where(i => !lockedKeys.Contains(i.Key)).ToList(), Project.Spooling);
        // Names of locked spools are taken: a new spool never gets one.
        var taken = new HashSet<string>(locked.Select(i => i.Spool), StringComparer.OrdinalIgnoreCase);
        foreach (var spool in plan.Spools.Where(s => !s.Existing && taken.Contains(s.Name)))
        {
            int n = 2;
            string name;
            do { name = $"{spool.Name}.{n++}"; } while (!taken.Add(name) || plan.Spools.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
            spool.Name = name;
        }
        Plan = plan;
        return plan;
    }

    /// <summary>The spools to list: the plan when there is one, else what the model holds (in what was read).</summary>
    public List<SpoolRow> Rows()
    {
        var rows = new List<SpoolRow>();
        if (Plan is not null)
        {
            foreach (var spool in Plan.Spools) rows.Add(Row(spool.Name, spool.Items, spool.Existing ? "In model" : "New"));
            var planned = new HashSet<string>(Plan.Spools.SelectMany(s => s.Items).Select(i => i.Key), StringComparer.Ordinal);
            foreach (var group in Items.Where(i => i.Spool.Length > 0 && !planned.Contains(i.Key)).GroupBy(i => i.Spool, StringComparer.OrdinalIgnoreCase))
                rows.Add(Row(group.Key, group.ToList(), "In model (released)"));
        }
        else
        {
            foreach (var group in Items.Where(i => i.Spool.Length > 0).GroupBy(i => i.Spool, StringComparer.OrdinalIgnoreCase))
                rows.Add(Row(group.First().Spool, group.ToList(), "In model"));
        }
        return rows.OrderBy(r => r.Name, NaturalOrder.Instance).ToList();
    }

    private static SpoolRow Row(string name, List<FabItem> items, string state)
    {
        string Most(Func<FabItem, string> value) => items.Select(value).Where(v => v.Length > 0).GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";
        double weight = items.Sum(i => i.WeightLb);
        double pipe = items.Where(i => i.IsStraight).Sum(i => i.LengthFt);
        return new SpoolRow
        {
            Name = name, Items = items, State = state, Parts = items.Count,
            Service = Most(i => i.Service), Level = Most(i => i.Level), Package = Most(i => i.Package), Status = Most(i => i.Status),
            Pipe = pipe > 0 ? FabMeasure.FeetInches(pipe) : "",
            Longest = FabMeasure.FeetInches(FabMeasure.LongestSideFt(items)),
            Weight = weight > 0 ? weight.ToString("0", CultureInfo.CurrentCulture) + " lb" : "",
        };
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Runs <paramref name="work"/> in one transaction (one undo step); nothing is kept if it fails.</summary>
    public void Run(string name, Action work)
    {
        using var t = new Transaction(Document, "Nexus: " + name);
        if (t.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Revit could not start a transaction. Finish the current command and try again.");
        try
        {
            work();
            t.Commit();
        }
        catch
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            throw;
        }
    }

    public void SaveProject() => Run("fabrication settings", () => FabStore.Save(Document, Project));

    /// <summary>Writes the planned spools to the pieces. Returns how many pieces changed and how many Revit refused.</summary>
    public (int Changed, int Refused) ApplySpools()
    {
        if (Plan is null) return (0, 0);
        int changed = 0, refused = 0;
        Run("spools", () =>
        {
            FabStore.EnsureParameters(Document);
            Document.Regenerate();
            void Write(FabItem item, string spool)
            {
                if (item.Tag is not ElementId id || Document.GetElement(id) is not { } e) return;
                if (FabCollector.SpoolOf(e) == spool) return;
                bool ok = FabStore.Set(e, FabStore.SpoolId, spool);
                // Fabrication parts keep a spool name of their own (it goes into the MAJ job).
                if (e is FabricationPart part)
                    try { part.SpoolName = spool; ok = true; } catch { /* a part that cannot be in a spool */ }
                if (ok) changed++; else refused++;
            }
            foreach (var spool in Plan.Spools.Where(s => !s.Existing))
                foreach (var item in spool.Items) Write(item, spool.Name);
            foreach (var item in Plan.Loose.Where(i => i.Spool.Length > 0)) Write(item, "");
            Project.Record(User, $"{Plan.Spools.Count(s => !s.Existing)} spool(s)", "Spooled", "", "Applied");
            FabStore.Save(Document, Project);
        });
        return (changed, refused);
    }

    /// <summary>Takes the pieces of these spools out of their spool (and out of their package and step).</summary>
    public int ClearSpools(IEnumerable<string> spools)
    {
        var names = new HashSet<string>(spools, StringComparer.OrdinalIgnoreCase);
        int n = 0;
        Run("clear spools", () =>
        {
            foreach (var (id, spool, _, _) in FabCollector.Index(Document).Where(x => names.Contains(x.Spool)))
            {
                if (Document.GetElement(id) is not { } e) continue;
                FabStore.Set(e, FabStore.SpoolId, "");
                FabStore.Set(e, FabStore.PackageId, "");
                FabStore.Set(e, FabStore.StatusId, "");
                if (e is FabricationPart part)
                    try { part.SpoolName = ""; } catch { /* not spoolable */ }
                n++;
            }
            foreach (var name in names) Project.Record(User, "Spool " + name, "Spooled", "Applied", "Cleared");
            FabStore.Save(Document, Project);
        });
        return n;
    }

    /// <summary>Puts spools into a package ("" takes them out) or sets their step. Returns the pieces changed.</summary>
    public int SetValue(IEnumerable<string> spools, Guid parameter, string field, string value)
    {
        var names = new HashSet<string>(spools, StringComparer.OrdinalIgnoreCase);
        int n = 0;
        Run(field.ToLowerInvariant(), () =>
        {
            FabStore.EnsureParameters(Document);
            Document.Regenerate();
            var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, spool, package, status) in FabCollector.Index(Document).Where(x => names.Contains(x.Spool)))
            {
                if (Document.GetElement(id) is not { } e) continue;
                if (!before.ContainsKey(spool)) before[spool] = parameter == FabStore.PackageId ? package : status;
                if (FabStore.Set(e, parameter, value)) n++;
            }
            foreach (var (spool, was) in before) Project.Record(User, "Spool " + spool, field, was, value);
            FabStore.Save(Document, Project);
        });
        return n;
    }

    /// <summary>Selects the pieces of these spools in Revit.</summary>
    public int Select(IEnumerable<string> spools)
    {
        var names = new HashSet<string>(spools, StringComparer.OrdinalIgnoreCase);
        var ids = FabCollector.Index(Document).Where(x => names.Contains(x.Spool)).Select(x => x.Id).ToList();
        _uidoc.Selection.SetElementIds(ids);
        return ids.Count;
    }

    public List<ElementId> ElementsOf(string spool) =>
        FabCollector.Index(Document).Where(x => x.Spool.Equals(spool, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToList();

    // ------------------------------------------------------------------ colours

    private static readonly string[] Palette =
    {
        "#2563EB", "#F59E0B", "#16A34A", "#DC2626", "#8B5CF6", "#0891B2", "#DB2777", "#65A30D", "#EA580C", "#4F46E5", "#0D9488", "#CA8A04",
    };

    /// <summary>
    /// Colours the pieces in the active view by step, spool or package ("Status", "Spool", "Package"). Steps use
    /// their own colour; spools and packages take turns through a set of colours so neighbours differ.
    /// </summary>
    public int Colour(string by)
    {
        var view = Document.ActiveView;
        var solid = new FilteredElementCollector(Document).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
        var colours = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in Project.Statuses) colours["Status|" + status.Name] = ToColor(status.Colour);
        int n = 0;
        Run("colour view", () =>
        {
            foreach (var e in new FilteredElementCollector(Document, view.Id).WherePasses(FabStore.Filter()).WhereElementIsNotElementType())
            {
                string value = by switch
                {
                    "Spool" => FabCollector.SpoolOf(e),
                    "Package" => FabStore.Get(e, FabStore.PackageId),
                    _ => FabStore.Get(e, FabStore.StatusId),
                };
                if (value.Length == 0)
                {
                    view.SetElementOverrides(e.Id, new OverrideGraphicSettings());
                    continue;
                }
                string key = by + "|" + value;
                if (!colours.TryGetValue(key, out var colour))
                    colours[key] = colour = ToColor(by == "Status" ? "#808080" : Palette[colours.Count(c => !c.Key.StartsWith("Status|", StringComparison.Ordinal)) % Palette.Length]);
                var settings = new OverrideGraphicSettings();
                settings.SetProjectionLineColor(colour);
                settings.SetSurfaceForegroundPatternColor(colour);
                if (solid is not null)
                {
                    settings.SetSurfaceForegroundPatternId(solid.Id);
                    settings.SetSurfaceForegroundPatternVisible(true);
                }
                view.SetElementOverrides(e.Id, settings);
                n++;
            }
        });
        return n;
    }

    public int ClearColours()
    {
        var view = Document.ActiveView;
        int n = 0;
        Run("clear colours", () =>
        {
            foreach (var e in new FilteredElementCollector(Document, view.Id).WherePasses(FabStore.Filter()).WhereElementIsNotElementType())
            {
                view.SetElementOverrides(e.Id, new OverrideGraphicSettings());
                n++;
            }
        });
        return n;
    }

    private static Color ToColor(string hex)
    {
        try
        {
            string h = hex.Trim().TrimStart('#');
            if (h.Length == 6)
                return new Color(byte.Parse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        catch { /* grey */ }
        return new Color(128, 128, 128);
    }

    private static T? Safe<T>(Func<T> get)
    {
        try { return get(); }
        catch { return default; }
    }
}

/// <summary>"CHWS-2" before "CHWS-10": numbers inside names compare as numbers.</summary>
internal sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
