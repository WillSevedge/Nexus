using Nexus.Contracts;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexus.Agent.Acad.Readers;

/// <summary>
/// Small helpers the sheet reader needs, kept free of AutoCAD's user-interface libraries so the same reader
/// also runs in AutoCAD's Core Console (Nexus.Agent.Acad.Console).
/// </summary>
internal static class AcadItems
{
    public static PropertyValue Derived(string name, string? value) => new()
    {
        Name = name,
        Value = value,
        RawValue = value,
        Source = PropertySource.Derived,
        StorageType = "String",
        IsReadOnly = true,
        ReadOnlyReason = "Computed by Nexus",
    };

    public static string EffectiveName(BlockReference br, Transaction tr)
    {
        try
        {
            var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
        }
        catch
        {
            return br.Name;
        }
    }
}
