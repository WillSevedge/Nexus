using System.Globalization;
using Autodesk.Revit.DB;
using Nexus.Fab;

namespace Nexus.Agent.Revit.Fab;

internal sealed class MappingResult
{
    public int Written { get; set; }
    public int Unchanged { get; set; }
    public int Refused { get; set; }
    public List<string> AddedParameters { get; set; } = new();
    /// <summary>Target parameter → pieces that do not have it.</summary>
    public Dictionary<string, int> NoParameter { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Value names some pieces have no value for.</summary>
    public HashSet<string> NoValue { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Parameter mapping in the model: reads each piece's fabrication values by name and writes them to Revit
/// parameters by the project's mapping. Values come from the piece itself (spool, service, size…), from its spool
/// and package, from the Fabrication part (item number, product code, custom data…), and, for any other name, from
/// the parameter of that name on the piece or its type.
/// </summary>
internal static class FabMapping
{
    /// <summary>The value names available for what was read: the standard ones, the Fabrication ones and the custom data fields.</summary>
    public static List<string> ValueNames(Document doc, IReadOnlyList<FabItem> items)
    {
        var names = ParameterMapper.Standard.ToList();
        if (items.Any(i => i.Tag is ElementId id && doc.GetElement(id) is FabricationPart))
        {
            names.AddRange(ParameterMapper.Fabrication);
            var config = Safe(() => FabricationConfiguration.GetFabricationConfiguration(doc));
            if (config is not null)
                foreach (int id in Safe(() => config.GetAllPartCustomData()) ?? new List<int>())
                    if (Safe(() => config.GetPartCustomDataName(id)) is { Length: > 0 } name && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    /// <summary>Writes the enabled mapping lines to the pieces. One transaction (one undo).</summary>
    public static MappingResult Write(FabSession session, IReadOnlyList<FabItem> items)
    {
        var doc = session.Document;
        var project = session.Project;
        var maps = project.Mappings.Where(m => m.Enabled && m.From.Trim().Length > 0 && m.To.Trim().Length > 0).ToList();
        var result = new MappingResult();
        if (maps.Count == 0 || items.Count == 0) return result;

        // Totals of each spool, for the "Spool …" values.
        var spools = items.Where(i => i.Spool.Length > 0).GroupBy(i => i.Spool, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var config = Safe(() => FabricationConfiguration.GetFabricationConfiguration(doc));
        var customData = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (config is not null)
            foreach (int id in Safe(() => config.GetAllPartCustomData()) ?? new List<int>())
                if (Safe(() => config.GetPartCustomDataName(id)) is { Length: > 0 } name) customData[name] = id;

        session.Run("parameter mapping", () =>
        {
            // Parameters the model does not have yet (looked for on the first piece of each category).
            if (project.AddMissingParameters)
            {
                var sample = items.Select(i => i.Tag is ElementId id ? doc.GetElement(id) : null).Where(e => e is not null)
                    .GroupBy(e => e!.Category?.Id).Select(g => g.First()!).ToList();
                var missing = maps.Select(m => m.To.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name => sample.Count > 0 && sample.All(e => e.LookupParameter(name) is null)).ToList();
                if (missing.Count > 0)
                {
                    result.AddedParameters = FabStore.EnsureTextParameters(doc,
                        missing.Select(n => (n, FabStore.IdFor(n), "Written by the Nexus parameter mapping.")).ToList());
                    doc.Regenerate();
                }
            }

            foreach (var item in items)
            {
                if (item.Tag is not ElementId id || doc.GetElement(id) is not { } e) continue;
                spools.TryGetValue(item.Spool, out var spool);
                string? Value(string name) => Lookup(doc, e, item, spool, project, customData, name);
                foreach (var map in maps)
                {
                    string target = map.To.Trim();
                    var p = e.LookupParameter(target);
                    if (p is null)
                    {
                        result.NoParameter[target] = result.NoParameter.TryGetValue(target, out int n) ? n + 1 : 1;
                        continue;
                    }
                    var missing = new List<string>();
                    string text = ParameterMapper.Resolve(map.From, Value, missing);
                    foreach (var m in missing) result.NoValue.Add(m);
                    switch (Set(p, text, ParameterMapper.SingleName(map.From), item))
                    {
                        case true: result.Written++; break;
                        case null: result.Unchanged++; break;
                        default: result.Refused++; break;
                    }
                }
            }
        });
        return result;
    }

    /// <summary>Sets the parameter: true = changed, null = it already had the value, false = Revit would not take it.</summary>
    private static bool? Set(Parameter p, string text, string? single, FabItem item)
    {
        try
        {
            if (p.IsReadOnly) return false;
            switch (p.StorageType)
            {
                case StorageType.String:
                    if ((p.AsString() ?? "") == text) return null;
                    return p.Set(text);
                case StorageType.Integer:
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whole)
                        && !int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out whole)) return false;
                    if (p.AsInteger() == whole) return null;
                    return p.Set(whole);
                case StorageType.Double:
                {
                    // A length or a weight written to a length or mass parameter goes in as the number itself.
                    var spec = p.Definition.GetDataType();
                    double? value = null;
                    if (string.Equals(single, "Length", StringComparison.OrdinalIgnoreCase) || string.Equals(single, "Length Ft", StringComparison.OrdinalIgnoreCase))
                    {
                        if (spec == SpecTypeId.Length) value = item.LengthFt;
                    }
                    else if (string.Equals(single, "Weight", StringComparison.OrdinalIgnoreCase))
                    {
                        if (spec == SpecTypeId.Mass) value = UnitUtils.ConvertToInternalUnits(item.WeightLb, UnitTypeId.PoundsMass);
                        else if (spec == SpecTypeId.Weight) value = UnitUtils.ConvertToInternalUnits(item.WeightLb, UnitTypeId.PoundsForce);
                    }
                    if (value is not null)
                    {
                        if (Math.Abs(p.AsDouble() - value.Value) < 1e-9) return null;
                        return p.Set(value.Value);
                    }
                    if (text.Length == 0) return false;
                    // Otherwise as typed in Revit, in the model's units ("12' 6\"", "4.5").
                    string before = p.AsValueString() ?? "";
                    if (!p.SetValueString(text)) return false;
                    return (p.AsValueString() ?? "") == before ? null : true;
                }
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? Lookup(Document doc, Element e, FabItem item, List<FabItem>? spool, FabProject project,
        Dictionary<string, int> customData, string name)
    {
        string key = name.Trim().ToUpperInvariant();
        string Num(double v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);
        switch (key)
        {
            case "SPOOL": return item.Spool;
            case "PACKAGE": return item.Package;
            case "STEP" or "STATUS": return item.Status;
            case "SERVICE": return item.Service;
            case "SIZE": return item.Size;
            case "MATERIAL": return item.Material;
            case "LEVEL": return item.Level;
            case "CATEGORY": return item.Category;
            case "DESCRIPTION": return item.Description;
            case "PART TYPE": return item.PartType;
            case "LENGTH": return item.LengthFt > 0 ? FabMeasure.FeetInches(item.LengthFt) : "";
            case "LENGTH FT": return item.LengthFt > 0 ? Num(item.LengthFt) : "";
            case "WEIGHT": return item.WeightLb > 0 ? Num(item.WeightLb, "0.#") : "";
            case "SPOOL PIECES": return spool is null ? "" : spool.Count.ToString(CultureInfo.InvariantCulture);
            case "SPOOL WEIGHT": return spool is null ? "" : Num(spool.Sum(i => i.WeightLb), "0.#");
            case "SPOOL LONGEST SIDE": return spool is null ? "" : FabMeasure.FeetInches(FabMeasure.LongestSideFt(spool));
            case "PACKAGE DESCRIPTION": return project.Package(item.Package)?.Description ?? "";
            case "PACKAGE DESTINATION": return project.Package(item.Package)?.Destination ?? "";
            case "PACKAGE DUE": return project.Package(item.Package)?.Due ?? "";
        }

        if (e is FabricationPart part)
        {
            switch (key)
            {
                case "ITEM NUMBER": return Safe(() => part.ItemNumber) ?? "";
                case "ALIAS": return Safe(() => part.Alias) ?? "";
                case "NOTES": return Safe(() => part.Notes) ?? "";
                case "PRODUCT CODE": return Safe(() => part.ProductCode) ?? "";
                case "SPECIFICATION": return Safe(() => part.get_Parameter(BuiltInParameter.FABRICATION_SPECIFICATION)?.AsValueString()) ?? "";
                case "INSULATION SPECIFICATION": return Safe(() => part.get_Parameter(BuiltInParameter.FABRICATION_INSULATION_SPEC)?.AsValueString()) ?? "";
                case "SERVICE NAME": return Safe(() => part.ServiceName) ?? "";
                case "SERVICE ABBREVIATION": return Safe(() => part.ServiceAbbreviation) ?? "";
                case "CID": return Safe(() => part.ItemCustomId.ToString(CultureInfo.InvariantCulture)) ?? "";
            }
            if (customData.TryGetValue(name.Trim(), out int field))
                return Safe(() => part.HasCustomData(field)) ? Safe(() => part.GetPartCustomDataText(field)) ?? "" : "";
        }
        else if (Array.Exists(ParameterMapper.Fabrication, f => f.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) || customData.ContainsKey(name.Trim()))
        {
            return ""; // a Fabrication value on a piece that is not a Fabrication part: blank, not an error
        }

        // Any other name: the parameter of that name on the piece, else on its type.
        var p = e.LookupParameter(name.Trim()) ?? doc.GetElement(e.GetTypeId())?.LookupParameter(name.Trim());
        if (p is null) return null;
        return p.StorageType == StorageType.String ? p.AsString() ?? "" : p.AsValueString() ?? "";
    }

    private static T? Safe<T>(Func<T> get)
    {
        try { return get(); }
        catch { return default; }
    }
}
