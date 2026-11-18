using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit.Fab;

/// <summary>One shared parameter the model has (added by MSUITE BIMPro, another add-in, or by hand).</summary>
internal sealed class ModelParameter
{
    public string Name { get; init; } = "";
    public Guid Id { get; init; }
    public string IdText => Id.ToString();
    /// <summary>"Text", "Length", "Yes/No"…</summary>
    public string Type { get; init; } = "";
    /// <summary>"Instance" or "Type".</summary>
    public string Binding { get; init; } = "";
    public string Categories { get; init; } = "";
    /// <summary>True when it is on pipe, duct or fabrication categories (what spool sheets tag).</summary>
    public bool OnFabricated { get; init; }
    public ForgeTypeId? DataType { get; init; }
    public string Tip { get; init; } = "";
}

/// <summary>
/// The shared parameters the open model already has (MSUITE BIMPro's, other add-ins', the office's), so Nexus writes
/// into those instead of adding its own: tags and schedules built for them keep working. Only read.
/// </summary>
internal static class SharedParameters
{
    public static List<ModelParameter> InModel(Document doc)
    {
        var fabricated = new HashSet<long>(FabStore.Categories.Select(c => (long)c));
        var result = new List<ModelParameter>();
        var it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
        {
            try
            {
                if (it.Key is not InternalDefinition definition || doc.GetElement(definition.Id) is not SharedParameterElement shared) continue;
                var binding = it.Current as ElementBinding;
                var categories = binding?.Categories.Cast<Category>().ToList() ?? new List<Category>();
                ForgeTypeId? type = null;
                string typeText = "";
                try
                {
                    type = definition.GetDataType();
                    typeText = LabelUtils.GetLabelForSpec(type);
                }
                catch { /* shown without a type */ }
                result.Add(new ModelParameter
                {
                    Name = definition.Name,
                    Id = shared.GuidValue,
                    Type = typeText,
                    DataType = type,
                    Binding = binding is TypeBinding ? "Type" : "Instance",
                    Categories = categories.Count > 4 ? $"{categories.Count} categories" : string.Join(", ", categories.Select(c => c.Name).OrderBy(n => n)),
                    OnFabricated = categories.Any(c => fabricated.Contains(c.Id.Value)),
                    Tip = string.Join(", ", categories.Select(c => c.Name).OrderBy(n => n)),
                });
            }
            catch
            {
                // A binding Revit will not describe: left out.
            }
        }
        return result.OrderByDescending(p => p.OnFabricated).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
