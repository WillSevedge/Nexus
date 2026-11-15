using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nexus.Fab;

namespace Nexus.Agent.Revit.Fab;

internal sealed class PlacementResult
{
    public int Placed { get; set; }
    public int AlreadyThere { get; set; }
    public int Skipped { get; set; }
    public List<string> Problems { get; } = new();

    public void Problem(string text)
    {
        if (Problems.Count < 12 && !Problems.Contains(text)) Problems.Add(text);
    }
}

/// <summary>A straight run as the placement tools see it: its element, axis, size and service.</summary>
internal sealed class StraightRun
{
    public Element Element { get; init; } = null!;
    public XYZ Start { get; init; } = XYZ.Zero;
    public XYZ End { get; init; } = XYZ.Zero;
    /// <summary>Round: the outside diameter. Rectangular: width and height (feet).</summary>
    public bool Round { get; init; }
    public double Diameter { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    /// <summary>Across the run, level with its width (rectangular runs).</summary>
    public XYZ WidthAxis { get; init; } = XYZ.BasisX;
    public double Insulation { get; init; }
    public string Service { get; init; } = "";
    public Connector? StartConnector { get; init; }

    public XYZ Direction => (End - Start).Normalize();
    public double Length => Start.DistanceTo(End);
    /// <summary>The size hanger rules go by, in inches: the diameter, or the larger side.</summary>
    public double SizeIn => (Round ? Diameter : Math.Max(Width, Height)) * 12;
}

/// <summary>
/// Places hangers along straights by the spacing rules, and sleeves where straights pass through walls and floors
/// (in this model or a linked one). Running it again adds only what is missing. Hangers are the Fabrication hanger
/// of the run's service, or a Revit family type; sleeves are solid shapes sized by the sleeve rules.
/// </summary>
internal static class FabPlacement
{
    public const string SleeveName = "Nexus Sleeve";

    // ------------------------------------------------------------------ straights

    public static List<StraightRun> Straights(Document doc, IEnumerable<FabItem> items)
    {
        var runs = new List<StraightRun>();
        foreach (var item in items.Where(i => i.Kind is FabKind.Pipe or FabKind.Duct or FabKind.Containment))
        {
            if (item.Tag is not ElementId id || doc.GetElement(id) is not { } e) continue;
            var ends = FabCollector.Connectors(e).Where(c => c.ConnectorType == ConnectorType.End).ToList();
            if (ends.Count < 2) continue;
            // The two ends farthest apart are the run's ends (taps on a straight are closer in).
            var (a, b) = ends.SelectMany(x => ends, (x, y) => (x, y)).OrderByDescending(p => p.x.Origin.DistanceTo(p.y.Origin)).First();
            if (a.Origin.DistanceTo(b.Origin) < 1e-6) continue;
            bool round = a.Shape == ConnectorProfileType.Round;
            runs.Add(new StraightRun
            {
                Element = e, Start = a.Origin, End = b.Origin, StartConnector = a, Service = item.Service, Round = round,
                Diameter = round ? a.Radius * 2 : 0,
                Width = round ? 0 : a.Width, Height = round ? 0 : a.Height,
                WidthAxis = round ? XYZ.BasisX : a.CoordinateSystem.BasisX,
                Insulation = Positive(e.get_Parameter(BuiltInParameter.RBS_REFERENCE_INSULATION_THICKNESS)?.AsDouble())
                             + Positive((e as FabricationPart)?.InsulationThickness),
            });
        }
        return runs;
    }

    private static double Positive(double? value) => value is > 0 ? value.Value : 0;

    // ------------------------------------------------------------------ hangers

    /// <summary>Hanger family types in the model: those whose family or type name says hanger, support, clevis, trapeze or strut.</summary>
    public static List<string> HangerTypes(Document doc)
    {
        string[] words = { "hanger", "support", "clevis", "trapeze", "strut" };
        return new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Select(s => $"{s.FamilyName} : {s.Name}")
            .Where(n => words.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
            .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static PlacementResult PlaceHangers(FabSession session, IReadOnlyList<StraightRun> runs)
    {
        var doc = session.Document;
        var result = new PlacementResult();
        var rules = session.Project.Hangers;
        var config = Safe(() => FabricationConfiguration.GetFabricationConfiguration(doc));
        // Hangers already there (any hanger, however placed): a position within a foot of one is taken.
        var taken = ExistingHangers(doc);

        session.Run("hangers", () =>
        {
            foreach (var run in runs)
            {
                if (Math.Abs(run.Direction.Z) > 0.5)
                {
                    result.Skipped++; // a riser is held by clamps at the floors, not by hangers
                    continue;
                }
                var rule = Placement.RuleFor(rules, run.Service, run.SizeIn);
                if (rule is null)
                {
                    result.Skipped++;
                    result.Problem($"No hanger rule covers {run.Service} at {run.SizeIn:0.##}\".");
                    continue;
                }
                foreach (double at in Placement.HangerPositions(run.Length, rule.SpacingFt, rule.FromEndFt))
                {
                    var point = run.Start + run.Direction * at;
                    if (taken.Any(p => p.DistanceTo(point) < 1.0))
                    {
                        result.AlreadyThere++;
                        continue;
                    }
                    try
                    {
                        bool placed = run.Element is FabricationPart part
                            ? FabricationHanger(doc, config, part, run, rule, at, result)
                            : FamilyHanger(doc, run, rule, point, result);
                        if (!placed) continue;
                        taken.Add(point);
                        result.Placed++;
                    }
                    catch (Exception ex)
                    {
                        result.Problem(ex.Message);
                    }
                }
            }
        });
        return result;
    }

    private static List<XYZ> ExistingHangers(Document doc)
    {
        var points = new List<XYZ>();
        foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_FabricationHangers).WhereElementIsNotElementType())
            if (Centre(e) is { } p) points.Add(p);
        foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            string name = $"{e.Symbol?.FamilyName} {e.Name}";
            if (name.IndexOf("hanger", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("clevis", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("trapeze", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if ((e.Location as LocationPoint)?.Point is { } p) points.Add(p);
        }
        return points;
    }

    /// <summary>The hanger of the run's own service (the rule's name picks one when the service has several).</summary>
    private static bool FabricationHanger(Document doc, FabricationConfiguration? config, FabricationPart host, StraightRun run, HangerRule rule, double at, PlacementResult result)
    {
        if (config is null || run.StartConnector is null) return false;
        FabricationServiceButton? button = null;
        foreach (var service in config.GetAllLoadedServices().Where(s => s.ServiceId == host.ServiceId))
            for (int palette = 0; palette < service.PaletteCount && button is null; palette++)
                for (int i = 0; i < service.GetButtonCount(palette); i++)
                {
                    var candidate = service.GetButton(palette, i);
                    if (!candidate.IsAHanger) continue;
                    if (rule.Hanger.Trim().Length > 0 && candidate.Name.IndexOf(rule.Hanger.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                    button = candidate;
                    break;
                }
        if (button is null)
        {
            result.Skipped++;
            result.Problem($"The service \"{run.Service}\" has no hanger" + (rule.Hanger.Trim().Length > 0 ? $" named \"{rule.Hanger.Trim()}\"." : " in its palettes."));
            return false;
        }
        FabricationPart.CreateHanger(doc, button, host.Id, run.StartConnector, at, true);
        return true;
    }

    /// <summary>A hanger family at the point on the run's centreline, turned along the run, sized by name where it has the parameters.</summary>
    private static bool FamilyHanger(Document doc, StraightRun run, HangerRule rule, XYZ point, PlacementResult result)
    {
        var symbol = Symbol(doc, rule.Hanger);
        if (symbol is null)
        {
            result.Skipped++;
            result.Problem(rule.Hanger.Trim().Length == 0
                ? "Choose a hanger family type in the hanger rules for runs that are not Fabrication parts."
                : $"The hanger family type \"{rule.Hanger}\" is not in the model.");
            return false;
        }
        if (!symbol.IsActive) symbol.Activate();
        var hanger = doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
        double angle = Math.Atan2(run.Direction.Y, run.Direction.X);
        if (Math.Abs(angle) > 1e-9)
            ElementTransformUtils.RotateElement(doc, hanger.Id, Line.CreateBound(point, point + XYZ.BasisZ), angle);
        double outside = (run.Round ? run.Diameter : run.Width) + 2 * run.Insulation;
        foreach (string name in new[] { "Nominal Diameter", "Pipe Diameter", "Pipe OD", "Diameter", "Pipe Size", "Width", "Duct Width" }) SetLength(hanger, name, outside);
        if (!run.Round)
            foreach (string name in new[] { "Height", "Duct Height" }) SetLength(hanger, name, run.Height + 2 * run.Insulation);
        return true;
    }

    private static FamilySymbol? Symbol(Document doc, string name)
    {
        string wanted = name.Trim();
        if (wanted.Length == 0) return null;
        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        return symbols.FirstOrDefault(s => $"{s.FamilyName} : {s.Name}".Equals(wanted, StringComparison.OrdinalIgnoreCase))
               ?? symbols.FirstOrDefault(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static void SetLength(Element e, string parameter, double feet)
    {
        try
        {
            var p = e.LookupParameter(parameter);
            if (p is { IsReadOnly: false, StorageType: StorageType.Double }) p.Set(feet);
        }
        catch { /* the family sizes itself another way */ }
    }

    // ------------------------------------------------------------------ sleeves

    public static PlacementResult PlaceSleeves(FabSession session, IReadOnlyList<StraightRun> runs)
    {
        var doc = session.Document;
        var rules = session.Project.Sleeves;
        var result = new PlacementResult();
        var hosts = Hosts(doc, rules);
        var taken = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Where(e => e.Name == SleeveName).Select(Centre).Where(p => p is not null).Select(p => p!).ToList();
        var solidOptions = new SolidCurveIntersectionOptions();

        session.Run("sleeves", () =>
        {
            foreach (var run in runs)
            {
                var axis = Line.CreateBound(run.Start, run.End);
                foreach (var host in hosts)
                {
                    // In the host's own coordinates (a linked model sits somewhere else).
                    var local = host.ToHost.IsIdentity ? axis : (Line)axis.CreateTransformed(host.ToHost);
                    if (!Overlaps(host.Box, local.GetEndPoint(0), local.GetEndPoint(1))) continue;
                    foreach (var solid in host.Solids())
                    {
                        SolidCurveIntersection hit;
                        try { hit = solid.IntersectWithCurve(local, solidOptions); }
                        catch { continue; }
                        for (int i = 0; i < hit.SegmentCount; i++)
                        {
                            var segment = hit.GetCurveSegment(i);
                            var p = host.FromHost.OfPoint(segment.GetEndPoint(0));
                            var q = host.FromHost.OfPoint(segment.GetEndPoint(1));
                            if (p.DistanceTo(q) < 1.0 / 96) continue; // a graze, not a crossing
                            var middle = (p + q) / 2;
                            if (taken.Any(t => t.DistanceTo(middle) < 0.1))
                            {
                                result.AlreadyThere++;
                                continue;
                            }
                            try
                            {
                                Sleeve(doc, run, rules, p, q, host);
                                taken.Add(middle);
                                result.Placed++;
                            }
                            catch (Exception ex)
                            {
                                result.Problem(ex.Message);
                            }
                        }
                    }
                }
            }
        });
        return result;
    }

    private sealed class Host
    {
        public Element Element { get; init; } = null!;
        public string Kind { get; init; } = "";
        public Transform ToHost { get; init; } = Transform.Identity;
        public Transform FromHost { get; init; } = Transform.Identity;
        public BoundingBoxXYZ Box { get; init; } = null!;
        private List<Solid>? _solids;

        public List<Solid> Solids()
        {
            if (_solids is not null) return _solids;
            _solids = new List<Solid>();
            void Add(GeometryElement? geometry)
            {
                if (geometry is null) return;
                foreach (var o in geometry)
                {
                    if (o is Solid { Volume: > 1e-6 } solid) _solids.Add(solid);
                    else if (o is GeometryInstance instance) Add(instance.GetInstanceGeometry());
                }
            }
            try { Add(Element.get_Geometry(new Options())); } catch { /* no geometry */ }
            return _solids;
        }
    }

    /// <summary>Walls and floors of this model and of every loaded link.</summary>
    private static List<Host> Hosts(Document doc, SleeveRules rules)
    {
        var hosts = new List<Host>();
        void Add(Document source, Transform fromHost)
        {
            var classes = new List<Type>();
            if (rules.Walls) classes.Add(typeof(Wall));
            if (rules.Floors) classes.Add(typeof(Floor));
            if (classes.Count == 0) return;
            foreach (var e in new FilteredElementCollector(source).WherePasses(new ElementMulticlassFilter(classes)).WhereElementIsNotElementType())
            {
                var box = Safe(() => e.get_BoundingBox(null));
                if (box is null) continue;
                hosts.Add(new Host { Element = e, Kind = e is Wall ? "wall" : "floor", FromHost = fromHost, ToHost = fromHost.Inverse, Box = box });
            }
        }
        Add(doc, Transform.Identity);
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            if (Safe(() => link.GetLinkDocument()) is { } linked) Add(linked, link.GetTotalTransform());
        return hosts;
    }

    private static bool Overlaps(BoundingBoxXYZ box, XYZ a, XYZ b) =>
        Math.Max(a.X, b.X) >= box.Min.X - 0.1 && Math.Min(a.X, b.X) <= box.Max.X + 0.1 &&
        Math.Max(a.Y, b.Y) >= box.Min.Y - 0.1 && Math.Min(a.Y, b.Y) <= box.Max.Y + 0.1 &&
        Math.Max(a.Z, b.Z) >= box.Min.Z - 0.1 && Math.Min(a.Z, b.Z) <= box.Max.Z + 0.1;

    /// <summary>One sleeve through a wall or floor from <paramref name="p"/> to <paramref name="q"/>: round for round runs, a box for rectangular ones.</summary>
    private static void Sleeve(Document doc, StraightRun run, SleeveRules rules, XYZ p, XYZ q, Host host)
    {
        var direction = (q - p).Normalize();
        double extend = Math.Max(0, rules.ExtendIn) / 12;
        var start = p - direction * extend;
        double length = p.DistanceTo(q) + 2 * extend;
        var loop = new CurveLoop();
        string size;
        if (run.Round)
        {
            double diameter = Placement.SleeveDiameterIn((run.Diameter + 2 * run.Insulation) * 12, rules) / 12;
            var x = Math.Abs(direction.Z) > 0.9 ? XYZ.BasisX : direction.CrossProduct(XYZ.BasisZ).Normalize();
            var y = direction.CrossProduct(x).Normalize();
            loop.Append(Arc.Create(start, diameter / 2, 0, Math.PI, x, y));
            loop.Append(Arc.Create(start, diameter / 2, Math.PI, 2 * Math.PI, x, y));
            size = FabMeasure.FeetInches(diameter).Replace("0'-", "") + " dia.";
        }
        else
        {
            double clear = Math.Max(0, rules.ClearanceIn) / 12 + run.Insulation;
            double w = run.Width + 2 * clear, h = run.Height + 2 * clear;
            var x = run.WidthAxis.Normalize();
            var y = direction.CrossProduct(x).Normalize();
            var c1 = start - x * (w / 2) - y * (h / 2);
            var c2 = start + x * (w / 2) - y * (h / 2);
            var c3 = start + x * (w / 2) + y * (h / 2);
            var c4 = start - x * (w / 2) + y * (h / 2);
            loop.Append(Line.CreateBound(c1, c2));
            loop.Append(Line.CreateBound(c2, c3));
            loop.Append(Line.CreateBound(c3, c4));
            loop.Append(Line.CreateBound(c4, c1));
            size = $"{FabMeasure.FeetInches(w).Replace("0'-", "")} x {FabMeasure.FeetInches(h).Replace("0'-", "")}";
        }
        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, direction, length);
        var shape = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
        shape.SetShape(new GeometryObject[] { solid });
        shape.Name = SleeveName;
        string text = $"Sleeve {size}, {FabMeasure.FeetInches(length).Replace("0'-", "")} long, {run.Service} through {host.Kind} ({host.Element.Name})";
        try { shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(text); } catch { /* no comments on this shape */ }
        try { shape.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("SLV " + size); } catch { /* no mark */ }
    }

    // ------------------------------------------------------------------ layout points

    /// <summary>
    /// The points to set out on site: every hanger and every Nexus sleeve, as "Point, Northing, Easting, Elevation,
    /// Description" in the model's shared (survey) coordinates and feet, ready for a total station.
    /// </summary>
    public static string LayoutPoints(Document doc)
    {
        var toShared = Safe(() => doc.ActiveProjectLocation.GetTotalTransform().Inverse) ?? Transform.Identity;
        var rows = new List<string[]>();
        void Add(string prefix, XYZ point, string description)
        {
            var p = toShared.OfPoint(point);
            rows.Add(new[]
            {
                $"{prefix}{rows.Count(r => r[0].StartsWith(prefix, StringComparison.Ordinal)) + 1}",
                p.Y.ToString("0.000", CultureInfo.InvariantCulture), p.X.ToString("0.000", CultureInfo.InvariantCulture),
                p.Z.ToString("0.000", CultureInfo.InvariantCulture), description,
            });
        }
        foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_FabricationHangers).WhereElementIsNotElementType())
            if (Centre(e) is { } p) Add("H", p, "Hanger " + e.Name);
        foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            string name = $"{e.Symbol?.FamilyName} {e.Name}";
            if (name.IndexOf("hanger", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("clevis", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("trapeze", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if ((e.Location as LocationPoint)?.Point is { } p) Add("H", p, "Hanger " + e.Name);
        }
        foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Where(e => e.Name == SleeveName))
            if (Centre(e) is { } p) Add("S", p, e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "Sleeve");
        return FabCsv.Write(new[] { "Point", "Northing", "Easting", "Elevation", "Description" }, rows);
    }

    private static XYZ? Centre(Element e)
    {
        var box = Safe(() => e.get_BoundingBox(null));
        return box is null ? null : (box.Min + box.Max) / 2;
    }

    private static T? Safe<T>(Func<T> get)
    {
        try { return get(); }
        catch { return default; }
    }
}
