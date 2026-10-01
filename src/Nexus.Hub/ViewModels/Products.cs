using Nexus.Contracts;

namespace Nexus.Hub.ViewModels;

/// <summary>Which Autodesk product a host is, and how readers are labelled in the hub.</summary>
internal static class Products
{
    /// <summary>"Revit", "AutoCAD", "Civil 3D" or "Plant 3D" (verticals run the AutoCAD agent with a module).</summary>
    public static string Of(HostInfo host)
    {
        if (host.HostKind == HostKinds.Revit) return "Revit";
        if (host.Product.Contains("Civil", StringComparison.OrdinalIgnoreCase) || host.Modules.Contains("Civil3D")) return "Civil 3D";
        if (host.Product.Contains("Plant", StringComparison.OrdinalIgnoreCase) || host.Modules.Contains("Plant3D")) return "Plant 3D";
        return "AutoCAD";
    }

    /// <summary>
    /// Category of a reader in the hub's view list. AutoCAD readers run in every AutoCAD-based product;
    /// a vertical module's readers (civil3d.*, plant.*) only in that product.
    /// </summary>
    public static string ReaderLabel(HostInfo host, string readerId)
    {
        if (host.HostKind == HostKinds.Revit) return "Revit";
        if (readerId.StartsWith("civil3d.", StringComparison.OrdinalIgnoreCase)) return "Civil 3D";
        if (readerId.StartsWith("plant.", StringComparison.OrdinalIgnoreCase)) return "Plant 3D";
        return "AutoCAD family";
    }
}
