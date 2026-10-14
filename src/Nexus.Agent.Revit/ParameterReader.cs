using System.Globalization;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit;

/// <summary>Turns Revit parameters into host-neutral property groups.</summary>
internal static class ParameterReader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Group for parameters the element has but the Properties palette does not show.</summary>
    public const string HiddenGroup = "Not in Properties";

    /// <summary>
    /// All parameters of <paramref name="element"/>, grouped like the Properties palette.
    /// Parameters visible in the palette come first, in palette order and in the palette's groups
    /// (Graphics, Text, Identity Data, Other...); then, when <paramref name="includeHidden"/>, the
    /// ones the palette does not show, in a separate <see cref="HiddenGroup"/> group.
    /// </summary>
    public static List<PropertyGroup> ReadGroups(Document doc, Element element, string groupPrefix,
        string? editBlocker, bool includeHidden)
    {
        var groups = new List<PropertyGroup>();
        var byName = new Dictionary<string, PropertyGroup>(StringComparer.Ordinal);
        var seen = new HashSet<long>();

        void Add(Parameter p, bool hidden)
        {
            if (p?.Definition is null) return;
            if (!seen.Add(p.Id.Value)) return;

            string groupName = groupPrefix + (hidden ? HiddenGroup : GroupLabel(p));
            if (!byName.TryGetValue(groupName, out var group))
            {
                group = new PropertyGroup(groupName);
                byName[groupName] = group;
                groups.Add(group);
            }
            var pv = Read(doc, p, editBlocker);
            pv.OwnerId = element.UniqueId;
            group.Properties.Add(pv);
        }

        IList<Parameter>? ordered = null;
        try { ordered = element.GetOrderedParameters(); } catch { /* some elements do not support it */ }
        if (ordered is not null)
            foreach (var p in ordered) Add(p, hidden: false);

        // Without a palette order (some elements), every parameter goes in its own group.
        if (includeHidden || ordered is null)
            foreach (Parameter p in element.Parameters) Add(p, hidden: ordered is not null);

        return groups;
    }

    public static PropertyValue Read(Document doc, Parameter p, string? editBlocker)
    {
        var pv = new PropertyValue { Name = p.Definition?.Name ?? "(unnamed)" };
        try
        {
            FillSource(doc, p, pv);
            pv.StorageType = p.StorageType.ToString();
            FillDataType(p, pv);
            FillValue(doc, p, pv);

            // Note: Parameter.UserModifiable is NOT a lock. Revit reports it false for many parameters
            // (e.g. sheet parameters, shared parameters created as not user-modifiable) whose values
            // the API can still set. Only IsReadOnly (calculated/driven values) blocks an edit.
            if (p.IsReadOnly)
            {
                pv.IsReadOnly = true;
                pv.ReadOnlyReason = "Read-only parameter (calculated or controlled by Revit)";
            }
            else if (editBlocker is not null)
            {
                pv.IsReadOnly = true;
                pv.ReadOnlyReason = editBlocker;
            }
        }
        catch (Exception ex)
        {
            pv.Value = "<error: " + ex.Message + ">";
            pv.HasValue = false;
            pv.IsReadOnly = true;
            pv.ReadOnlyReason = "Could not read parameter";
        }
        return pv;
    }

    /// <summary>The parameter's current value and raw value, formatted exactly as <see cref="Read"/> does.</summary>
    public static PropertyValue CurrentValue(Document doc, Parameter p)
    {
        var pv = new PropertyValue { Name = p.Definition?.Name ?? "" };
        FillValue(doc, p, pv);
        return pv;
    }

    private static void FillSource(Document doc, Parameter p, PropertyValue pv)
    {
        var bip = (p.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
        if (bip != BuiltInParameter.INVALID)
        {
            pv.Source = PropertySource.BuiltIn;
            pv.Id = bip.ToString();
        }
        else if (p.IsShared)
        {
            pv.Source = PropertySource.Shared;
            pv.Id = p.GUID.ToString();
        }
        else if (doc.GetElement(p.Id) is ParameterElement)
        {
            pv.Source = PropertySource.Project;
            pv.Id = p.Id.Value.ToString(Inv);
        }
        else
        {
            pv.Source = PropertySource.Family;
            pv.Id = p.Id.Value.ToString(Inv);
        }
    }

    private static void FillDataType(Parameter p, PropertyValue pv)
    {
        try
        {
            var spec = p.Definition.GetDataType();
            if (spec is null || spec.Empty()) return;
            try { pv.DataType = LabelUtils.GetLabelForSpec(spec); }
            catch { pv.DataType = spec.TypeId; }

            if (UnitUtils.IsMeasurableSpec(spec))
                pv.Units = LabelUtils.GetLabelForUnit(p.GetUnitTypeId());
        }
        catch
        {
            // Category-typed (family type) parameters etc. have no spec label.
        }
    }

    private static void FillValue(Document doc, Parameter p, PropertyValue pv)
    {
        pv.HasValue = p.HasValue;
        switch (p.StorageType)
        {
            case StorageType.String:
                pv.Value = p.AsString();
                pv.RawValue = pv.Value;
                break;
            case StorageType.Integer:
                pv.RawValue = p.AsInteger().ToString(Inv);
                pv.Value = p.AsValueString() ?? pv.RawValue;
                break;
            case StorageType.Double:
                pv.RawValue = p.AsDouble().ToString("R", Inv);
                pv.Value = p.AsValueString() ?? pv.RawValue;
                break;
            case StorageType.ElementId:
                var id = p.AsElementId();
                pv.RawValue = id.Value.ToString(Inv);
                pv.Value = p.AsValueString();
                if (string.IsNullOrEmpty(pv.Value))
                    pv.Value = id == ElementId.InvalidElementId ? "" : doc.GetElement(id)?.Name ?? pv.RawValue;
                break;
            default:
                pv.HasValue = false;
                break;
        }
    }

    private static string GroupLabel(Parameter p)
    {
        try
        {
            var g = p.Definition.GetGroupTypeId();
            if (g is null || g.Empty()) return "Other";
            return LabelUtils.GetLabelForGroup(g);
        }
        catch
        {
            return "Other";
        }
    }
}
