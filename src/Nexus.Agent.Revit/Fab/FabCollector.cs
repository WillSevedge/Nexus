using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Nexus.Fab;

namespace Nexus.Agent.Revit.Fab;

internal enum FabScope { Selection, ActiveView, WholeModel }

/// <summary>
/// Reads the pieces of the runs, whichever way they were modelled: MEP Fabrication parts (ITM content) and Revit's own
/// pipes, ducts, conduit, cable tray, fittings and accessories. Each becomes one <see cref="FabItem"/> with its
/// service, size, length, weight, box and the joints to its neighbours.
/// </summary>
internal static class FabCollector
{
    public static List<FabItem> Collect(UIDocument uidoc, FabScope scope)
    {
        var doc = uidoc.Document;
        IEnumerable<Element> elements = scope switch
        {
            FabScope.Selection => Expand(doc, uidoc.Selection.GetElementIds()).Where(IsFabricated),
            FabScope.ActiveView => new FilteredElementCollector(doc, doc.ActiveView.Id).WherePasses(FabStore.Filter()).WhereElementIsNotElementType(),
            _ => new FilteredElementCollector(doc).WherePasses(FabStore.Filter()).WhereElementIsNotElementType(),
        };
        return Items(doc, elements.ToList());
    }

    /// <summary>Every fabricated piece in the model, read lightly: id, spool, package and status only.</summary>
    public static List<(ElementId Id, string Spool, string Package, string Status)> Index(Document doc) =>
        new FilteredElementCollector(doc).WherePasses(FabStore.Filter()).WhereElementIsNotElementType()
            .Select(e => (e.Id, SpoolOf(e), FabStore.Get(e, FabStore.PackageId), FabStore.Get(e, FabStore.StatusId))).ToList();

    private static bool IsFabricated(Element e) =>
        e.Category is { } c && Array.IndexOf(FabStore.Categories, c.BuiltInCategory) >= 0;

    /// <summary>The selection with the members of selected assemblies and groups.</summary>
    private static IEnumerable<Element> Expand(Document doc, ICollection<ElementId> ids)
    {
        var seen = new HashSet<ElementId>();
        foreach (var id in ids)
        {
            var e = doc.GetElement(id);
            IEnumerable<ElementId> members = e switch
            {
                AssemblyInstance assembly => assembly.GetMemberIds(),
                Group group => group.GetMemberIds(),
                _ => new[] { id },
            };
            foreach (var member in members)
                if (seen.Add(member) && doc.GetElement(member) is { } element) yield return element;
        }
    }

    public static string SpoolOf(Element e)
    {
        string spool = FabStore.Get(e, FabStore.SpoolId);
        if (spool.Length == 0 && e is FabricationPart part)
            try { spool = part.SpoolName ?? ""; } catch { /* not a spoolable part */ }
        return spool.Trim();
    }

    private static List<FabItem> Items(Document doc, List<Element> elements)
    {
        var config = Safe(() => FabricationConfiguration.GetFabricationConfiguration(doc));
        var items = new List<FabItem>(elements.Count);
        var connection = new Dictionary<string, string>(StringComparer.Ordinal); // a pipe's own connection type ("Threaded")
        foreach (var e in elements)
        {
            var item = Item(doc, e, config);
            if (item is null) continue;
            items.Add(item);
            string type = Safe(() => e.get_Parameter(BuiltInParameter.RBS_PIPE_CONNECTIONTYPE_PARAM)?.AsValueString()) ?? "";
            if (type.Length > 0) connection[item.Key] = type;
        }

        // Joints of Revit's own pipes and ducts carry no kind: take it from what meets there (a flange, a union),
        // else from the pipe's connection type.
        var byKey = items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        foreach (var item in items)
            foreach (var joint in item.Joints.Where(j => j.Type.Length == 0))
            {
                byKey.TryGetValue(joint.OtherKey, out var other);
                string parts = item.PartType + " " + (other?.PartType ?? "");
                joint.Type = parts.IndexOf("Flange", StringComparison.OrdinalIgnoreCase) >= 0 ? "Flange"
                    : parts.IndexOf("Union", StringComparison.OrdinalIgnoreCase) >= 0 ? "Union"
                    : connection.TryGetValue(item.Key, out var mine) ? mine
                    : other is not null && connection.TryGetValue(other.Key, out var theirs) ? theirs : "";
            }
        return items;
    }

    private static FabItem? Item(Document doc, Element e, FabricationConfiguration? config)
    {
        if (e.Category is not { } category) return null;
        var bic = category.BuiltInCategory;
        var part = e as FabricationPart;
        bool straight = part is not null ? Safe(() => part.IsAStraight()) : e is MEPCurve;
        var kind = bic switch
        {
            BuiltInCategory.OST_FabricationHangers => FabKind.Hanger,
            BuiltInCategory.OST_FabricationPipework => straight ? FabKind.Pipe : FabKind.Fitting,
            BuiltInCategory.OST_FabricationDuctwork => straight ? FabKind.Duct : FabKind.Fitting,
            BuiltInCategory.OST_FabricationContainment => straight ? FabKind.Containment : FabKind.Fitting,
            BuiltInCategory.OST_PipeCurves or BuiltInCategory.OST_FlexPipeCurves => FabKind.Pipe,
            BuiltInCategory.OST_DuctCurves or BuiltInCategory.OST_FlexDuctCurves => FabKind.Duct,
            BuiltInCategory.OST_Conduit or BuiltInCategory.OST_CableTray => FabKind.Containment,
            BuiltInCategory.OST_PipeAccessory or BuiltInCategory.OST_DuctAccessory => FabKind.Accessory,
            _ => FabKind.Fitting,
        };

        var item = new FabItem
        {
            Key = e.Id.Value.ToString(CultureInfo.InvariantCulture),
            Kind = kind,
            Category = category.Name,
            Tag = e.Id,
            Spool = SpoolOf(e),
            Package = FabStore.Get(e, FabStore.PackageId),
            Status = FabStore.Get(e, FabStore.StatusId),
            Level = Level(doc, e),
        };

        if (part is not null)
        {
            string abbreviation = Safe(() => part.ServiceAbbreviation) ?? "";
            item.Service = abbreviation.Trim().Length > 0 ? abbreviation.Trim() : Safe(() => part.ServiceName) ?? "";
            item.Description = FirstText(Safe(() => part.ProductName), Safe(() => part.get_Parameter(BuiltInParameter.FABRICATION_PRODUCT_DATA_LONG_DESCRIPTION)?.AsString()), e.Name);
            item.Size = FirstText(Safe(() => part.ProductSizeDescription), Safe(() => part.Size), Text(e, BuiltInParameter.RBS_CALCULATED_SIZE));
            item.Material = Safe(() => part.get_Parameter(BuiltInParameter.FABRICATION_PART_MATERIAL)?.AsValueString()) ?? "";
            if (straight) item.LengthFt = Safe(() => part.CenterlineLength);
            item.WeightLb = Weight(part);
        }
        else
        {
            string abbreviation = Text(e, BuiltInParameter.RBS_DUCT_PIPE_SYSTEM_ABBREVIATION_PARAM);
            item.Service = FirstText(abbreviation, ValueText(e, BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM), ValueText(e, BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM),
                Text(e, BuiltInParameter.RBS_SYSTEM_NAME_PARAM));
            item.Size = Text(e, BuiltInParameter.RBS_CALCULATED_SIZE);
            item.Material = FirstText(ValueText(e, BuiltInParameter.RBS_PIPE_MATERIAL_PARAM), ValueText(e, BuiltInParameter.RBS_PIPE_SEGMENT_PARAM));
            if (e is FamilyInstance instance)
            {
                item.Description = $"{instance.Symbol?.FamilyName} : {instance.Name}".Trim(' ', ':');
                item.PartType = Safe(() => instance.MEPModel is MechanicalFitting fitting ? fitting.PartType.ToString() : "") ?? "";
                if (kind == FabKind.Accessory && item.PartType.Length == 0) item.PartType = "Accessory";
            }
            else
            {
                item.Description = doc.GetElement(e.GetTypeId())?.Name ?? e.Name;
                item.LengthFt = e.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;
            }
        }

        var box = Safe(() => e.get_BoundingBox(null));
        if (box is not null)
        {
            (item.MinX, item.MinY, item.MinZ) = (box.Min.X, box.Min.Y, box.Min.Z);
            (item.MaxX, item.MaxY, item.MaxZ) = (box.Max.X, box.Max.Y, box.Max.Z);
        }

        foreach (Connector c in Connectors(e))
        {
            if (c.ConnectorType is not (ConnectorType.End or ConnectorType.Curve) || !Safe(() => c.IsConnected)) continue;
            string type = part is not null ? JointName(c, config) : "";
            foreach (Connector other in c.AllRefs)
            {
                if (other.Owner is null || other.Owner.Id == e.Id) continue;
                if (other.ConnectorType is not (ConnectorType.End or ConnectorType.Curve)) continue;
                if (other.Owner is MEPSystem or InsulationLiningBase) continue;
                string otherKey = other.Owner.Id.Value.ToString(CultureInfo.InvariantCulture);
                if (item.Joints.Any(j => j.OtherKey == otherKey)) continue;
                item.Joints.Add(new FabJoint { OtherKey = otherKey, Type = type.Length > 0 ? type : other.Owner is FabricationPart ? JointName(other, config) : "" });
            }
        }
        return item;
    }

    public static IEnumerable<Connector> Connectors(Element e)
    {
        ConnectorManager? manager = Safe(() => e switch
        {
            FabricationPart part => part.ConnectorManager,
            MEPCurve curve => curve.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            _ => null,
        });
        if (manager is null) yield break;
        foreach (Connector c in manager.Connectors) yield return c;
    }

    /// <summary>The fabrication connector's name ("Flange 150#", "Grooved", "BW"), with its group when it has one.</summary>
    private static string JointName(Connector c, FabricationConfiguration? config)
    {
        if (config is null) return "";
        try
        {
            int id = c.GetFabricationConnectorInfo().BodyConnectorId;
            string name = config.GetFabricationConnectorName(id) ?? "";
            string group = config.GetFabricationConnectorGroup(id) ?? "";
            return group.Length > 0 && name.IndexOf(group, StringComparison.OrdinalIgnoreCase) < 0 ? $"{group}: {name}" : name;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>The part's weight in pounds, whichever unit the model keeps it in.</summary>
    private static double Weight(FabricationPart part)
    {
        try
        {
            var p = part.get_Parameter(BuiltInParameter.FABRICATION_PART_WEIGHT);
            if (p is { HasValue: true, StorageType: StorageType.Double })
            {
                var spec = p.Definition.GetDataType();
                if (spec == SpecTypeId.Mass) return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.PoundsMass);
                if (spec == SpecTypeId.Weight) return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.PoundsForce);
            }
            return part.Weight;
        }
        catch
        {
            return 0;
        }
    }

    private static string Level(Document doc, Element e)
    {
        foreach (var bip in new[] { BuiltInParameter.RBS_START_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.FABRICATION_LEVEL_PARAM })
        {
            var id = Safe(() => e.get_Parameter(bip)?.AsElementId());
            if (id is not null && id != ElementId.InvalidElementId && doc.GetElement(id) is Level level) return level.Name;
        }
        return e.LevelId != ElementId.InvalidElementId && doc.GetElement(e.LevelId) is Level own ? own.Name : "";
    }

    private static string Text(Element e, BuiltInParameter bip) => Safe(() => e.get_Parameter(bip)?.AsString())?.Trim() ?? "";

    private static string ValueText(Element e, BuiltInParameter bip) => Safe(() => e.get_Parameter(bip)?.AsValueString())?.Trim() ?? "";

    private static string FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static T? Safe<T>(Func<T> get)
    {
        try { return get(); }
        catch { return default; }
    }
}
