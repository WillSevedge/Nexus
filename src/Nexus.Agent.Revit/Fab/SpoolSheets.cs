using Autodesk.Revit.DB;
using Nexus.Fab;

namespace Nexus.Agent.Revit.Fab;

/// <summary>What goes on a spool sheet.</summary>
internal sealed class SpoolSheetOptions
{
    public ElementId TitleBlock { get; set; } = ElementId.InvalidElementId;
    public ElementId ViewTemplate { get; set; } = ElementId.InvalidElementId;
    public bool View3D { get; set; } = true;
    public bool Front { get; set; } = true;
    public bool Top { get; set; } = true;
    public bool PartsList { get; set; } = true;
    public bool Tags { get; set; } = true;
    public bool Dimensions { get; set; } = true;
    /// <summary>Share of the sheet's width kept clear on the right for the title block's own strip (0–0.5).</summary>
    public double TitleStrip { get; set; } = 0.18;
}

internal sealed class SpoolSheetResult
{
    public int Sheets { get; set; }
    public int Tags { get; set; }
    public int Dimensions { get; set; }
    public List<string> Problems { get; } = new();
}

/// <summary>
/// Spool sheets, made the Revit way: each spool becomes an assembly named after it, and the assembly gets its own
/// sheet with a 3D view, a front and a top view scaled to fit, and a parts list. Pieces are tagged with the tag
/// loaded for their category, and straights lying in the view are dimensioned end to end. A spool that already has
/// an assembly and sheet is left as it is.
/// </summary>
internal static class SpoolSheets
{
    /// <summary>Scales tried, largest drawing first (3" = 1'-0" down to 1/16" = 1'-0").</summary>
    private static readonly int[] Scales = { 4, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192 };

    public static SpoolSheetResult Create(FabSession session, IReadOnlyList<SpoolRow> spools, SpoolSheetOptions options)
    {
        var doc = session.Document;
        var result = new SpoolSheetResult();
        var existing = new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>()
            .GroupBy(a => a.AssemblyTypeName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sheetNumbers = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber),
            StringComparer.OrdinalIgnoreCase);

        foreach (var spool in spools)
        {
            try
            {
                var ids = session.ElementsOf(spool.Name);
                if (ids.Count == 0)
                {
                    result.Problems.Add($"{spool.Name}: apply the spools to the model first.");
                    continue;
                }
                if (existing.TryGetValue(spool.Name, out var assembly) && HasSheet(doc, assembly))
                {
                    result.Problems.Add($"{spool.Name}: already has a sheet (left as it is).");
                    continue;
                }

                // 1. The assembly. Its name can only be set once it exists, so it is its own step.
                if (assembly is null)
                {
                    var free = ids.Where(id => doc.GetElement(id) is { } e && e.AssemblyInstanceId == ElementId.InvalidElementId).ToList();
                    if (free.Count == 0)
                    {
                        result.Problems.Add($"{spool.Name}: its pieces are already in other assemblies.");
                        continue;
                    }
                    var naming = free.Select(id => doc.GetElement(id).Category.Id).GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key)
                        .FirstOrDefault(c => AssemblyInstance.IsValidNamingCategory(doc, c, free));
                    if (naming is null)
                    {
                        result.Problems.Add($"{spool.Name}: Revit cannot make an assembly of these pieces.");
                        continue;
                    }
                    session.Run("spool assembly " + spool.Name, () => assembly = AssemblyInstance.Create(doc, free, naming));
                    session.Run("name spool assembly", () =>
                    {
                        try { assembly!.AssemblyTypeName = spool.Name; } catch { /* a name Revit refuses: its own name stays */ }
                        FabStore.Set(assembly!, FabStore.SpoolId, spool.Name);
                        FabStore.Set(assembly!, FabStore.PackageId, spool.Package);
                        FabStore.Set(assembly!, FabStore.StatusId, spool.Status);
                    });
                }

                // 2. The sheet and its views.
                session.Run("spool sheet " + spool.Name, () => Sheet(doc, assembly!, spool, options, sheetNumbers, result));
                result.Sheets++;
            }
            catch (Exception ex)
            {
                result.Problems.Add($"{spool.Name}: {ex.Message}");
            }
        }
        return result;
    }

    private static bool HasSheet(Document doc, AssemblyInstance assembly) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Any(s => s.AssociatedAssemblyInstanceId == assembly.Id);

    private static void Sheet(Document doc, AssemblyInstance assembly, SpoolRow spool, SpoolSheetOptions options, HashSet<string> sheetNumbers, SpoolSheetResult result)
    {
        var sheet = AssemblyViewUtils.CreateSheet(doc, assembly.Id, options.TitleBlock);
        sheet.Name = spool.Name;
        string number = spool.Name;
        for (int n = 2; !sheetNumbers.Add(number); n++) number = $"{spool.Name} ({n})";
        try { sheet.SheetNumber = number; } catch { /* Revit's own number stays */ }
        doc.Regenerate();

        // The sheet's paper, from its title block (a sheet without one is taken as 36 x 24 in).
        var block = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
        var paper = block?.get_BoundingBox(sheet);
        XYZ min = paper?.Min ?? XYZ.Zero, max = paper?.Max ?? new XYZ(3, 2, 0);
        double margin = 0.5 / 12, strip = Math.Max(0, Math.Min(0.5, options.TitleStrip));
        double left = min.X + margin, bottom = min.Y + margin, top = max.Y - margin;
        double right = max.X - margin - (max.X - min.X) * strip;
        double width = right - left, height = top - bottom;

        // Slots: 3D top left, front below it, top view to the right of the front, parts list top right.
        void Place(View? view, double x0, double x1, double y0, double y1)
        {
            if (view is null) return;
            if (options.ViewTemplate != ElementId.InvalidElementId && view.IsValidViewTemplate(options.ViewTemplate))
                try { view.ViewTemplateId = options.ViewTemplate; } catch { /* the view keeps its own settings */ }
            Fit(view, (x1 - x0) * width - margin, (y1 - y0) * height - margin);
            doc.Regenerate();
            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id)) return;
            Viewport.Create(doc, sheet.Id, view.Id, new XYZ(left + (x0 + x1) / 2 * width, bottom + (y0 + y1) / 2 * height, 0));
        }

        View? front = null, plan = null;
        if (options.View3D) Place(AssemblyViewUtils.Create3DOrthographic(doc, assembly.Id), 0, 0.5, 0.5, 1);
        if (options.Front) Place(front = AssemblyViewUtils.CreateDetailSection(doc, assembly.Id, AssemblyDetailViewOrientation.ElevationFront), 0, 0.5, 0, 0.5);
        if (options.Top) Place(plan = AssemblyViewUtils.CreateDetailSection(doc, assembly.Id, AssemblyDetailViewOrientation.ElevationTop), 0.5, 1, 0, 0.5);
        if (options.PartsList)
        {
            var list = AssemblyViewUtils.CreatePartList(doc, assembly.Id);
            ScheduleSheetInstance.Create(doc, sheet.Id, list.Id, new XYZ(left + 0.52 * width, top, 0));
        }
        doc.Regenerate();

        foreach (var view in new[] { front, plan })
        {
            if (view is null) continue;
            if (options.Tags) result.Tags += Tag(doc, view, assembly);
            if (options.Dimensions) result.Dimensions += Dimension(doc, view, assembly);
        }
    }

    /// <summary>The largest scale at which the view's crop fits the slot (paper feet).</summary>
    private static void Fit(View view, double slotWidth, double slotHeight)
    {
        try
        {
            if (view is View3D || !view.CropBoxActive) return;
            var crop = view.CropBox;
            double w = crop.Max.X - crop.Min.X, h = crop.Max.Y - crop.Min.Y;
            if (w <= 0 || h <= 0) return;
            int scale = Scales.FirstOrDefault(s => w / s <= slotWidth && h / s <= slotHeight);
            view.Scale = scale == 0 ? Scales[Scales.Length - 1] : scale;
        }
        catch
        {
            // The view keeps Revit's scale.
        }
    }

    /// <summary>Tags each piece with the tag loaded for its category. Returns how many were placed.</summary>
    private static int Tag(Document doc, View view, AssemblyInstance assembly)
    {
        int n = 0;
        foreach (var id in assembly.GetMemberIds())
        {
            try
            {
                if (doc.GetElement(id) is not { } e) continue;
                var box = e.get_BoundingBox(view);
                if (box is null) continue;
                IndependentTag.Create(doc, view.Id, new Reference(e), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, (box.Min + box.Max) / 2);
                n++;
            }
            catch
            {
                // No tag is loaded for this category, or the piece cannot be tagged in this view.
            }
        }
        return n;
    }

    /// <summary>Dimensions each straight that lies in the view, end to end, a little off the run. Returns how many.</summary>
    private static int Dimension(Document doc, View view, AssemblyInstance assembly)
    {
        int n = 0;
        var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view };
        foreach (var id in assembly.GetMemberIds())
        {
            try
            {
                if (doc.GetElement(id) is not MEPCurve curve || (curve.Location as LocationCurve)?.Curve is not Line axis) continue;
                if (Math.Abs(axis.Direction.DotProduct(view.ViewDirection)) > 0.02) continue; // runs towards the viewer: nothing to measure here
                var centre = Lines(curve.get_Geometry(options)).FirstOrDefault(l =>
                    l.Reference is not null && Math.Abs(l.Length - axis.Length) < 1e-3 && l.Direction.CrossProduct(axis.Direction).GetLength() < 1e-6);
                if (centre is null || centre.GetEndPointReference(0) is not { } a || centre.GetEndPointReference(1) is not { } b) continue;
                var references = new ReferenceArray();
                references.Append(a);
                references.Append(b);
                // Off the run, in the view's plane, by 3/8 inch on paper.
                var off = axis.Direction.CrossProduct(view.ViewDirection).Normalize() * (0.375 / 12 * view.Scale);
                var line = Line.CreateBound(axis.GetEndPoint(0) + off, axis.GetEndPoint(1) + off);
                if (doc.Create.NewDimension(view, line, references) is not null) n++;
            }
            catch
            {
                // A straight Revit will not dimension in this view.
            }
        }
        return n;
    }

    private static IEnumerable<Line> Lines(GeometryElement? geometry)
    {
        if (geometry is null) yield break;
        foreach (var o in geometry)
        {
            if (o is Line line) yield return line;
            else if (o is GeometryInstance instance)
                foreach (var inner in Lines(instance.GetInstanceGeometry())) yield return inner;
        }
    }

    public static List<(string Name, ElementId Id)> TitleBlocks(Document doc) =>
        new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().Cast<FamilySymbol>()
            .Select(s => ($"{s.FamilyName} : {s.Name}", s.Id)).OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();

    public static List<(string Name, ElementId Id)> ViewTemplates(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate && v.ViewType is ViewType.Detail or ViewType.Section)
            .Select(v => (v.Name, v.Id)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
