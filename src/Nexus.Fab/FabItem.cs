namespace Nexus.Fab;

public enum FabKind
{
    /// <summary>A straight length of pipe.</summary>
    Pipe,
    /// <summary>A straight length of duct.</summary>
    Duct,
    /// <summary>Conduit, cable tray or other straight containment.</summary>
    Containment,
    /// <summary>An elbow, tee, reducer, flange, coupling…</summary>
    Fitting,
    /// <summary>A valve, damper, strainer or other in-line item.</summary>
    Accessory,
    Hanger,
    /// <summary>Equipment and anything else a run connects to: never part of a spool, and ends one.</summary>
    Other,
}

/// <summary>A joint between two items: what kind it is ("Flange", "Grooved", "Weld", "Threaded"…) and what is on the other side.</summary>
public sealed class FabJoint
{
    public string OtherKey { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>
/// One piece of a run, whichever way it was modelled (a Fabrication part or a Revit pipe, duct, fitting or accessory):
/// what the spooling, packaging, cut list and material rules need to know about it.
/// </summary>
public sealed class FabItem
{
    /// <summary>The host's id for the piece (unique in the model).</summary>
    public string Key { get; set; } = "";
    public FabKind Kind { get; set; }
    /// <summary>"Pipes", "Duct Fittings", "MEP Fabrication Pipework"…</summary>
    public string Category { get; set; } = "";
    /// <summary>The service or system the piece belongs to ("CHWS", "Supply Air").</summary>
    public string Service { get; set; } = "";
    /// <summary>What it is: the product or family and type name.</summary>
    public string Description { get; set; } = "";
    /// <summary>"Elbow", "Tee", "Flange", "Union", "Valve"… when known.</summary>
    public string PartType { get; set; } = "";
    public string Size { get; set; } = "";
    public string Material { get; set; } = "";
    public string Level { get; set; } = "";
    /// <summary>Cut length of a straight, in feet; 0 for fittings.</summary>
    public double LengthFt { get; set; }
    /// <summary>Weight in pounds; 0 when the model does not say.</summary>
    public double WeightLb { get; set; }
    /// <summary>The box around the piece, in feet (model coordinates).</summary>
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MinZ { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public double MaxZ { get; set; }
    public List<FabJoint> Joints { get; set; } = new();

    // What the model holds now.
    public string Spool { get; set; } = "";
    public string Package { get; set; } = "";
    public string Status { get; set; } = "";
    /// <summary>The line number, as read from the line number parameter.</summary>
    public string Line { get; set; } = "";

    /// <summary>The host's own object (element id…), for applying.</summary>
    public object? Tag { get; set; }

    public bool IsStraight => Kind is FabKind.Pipe or FabKind.Duct or FabKind.Containment;
}

/// <summary>Sizes and totals of a group of items (a spool, a package).</summary>
public static class FabMeasure
{
    /// <summary>The longest side of the box around the items, in feet: what has to fit on the truck or through the door.</summary>
    public static double LongestSideFt(IEnumerable<FabItem> items)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        bool any = false;
        foreach (var i in items)
        {
            any = true;
            minX = Math.Min(minX, i.MinX); minY = Math.Min(minY, i.MinY); minZ = Math.Min(minZ, i.MinZ);
            maxX = Math.Max(maxX, i.MaxX); maxY = Math.Max(maxY, i.MaxY); maxZ = Math.Max(maxZ, i.MaxZ);
        }
        return any ? Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)) : 0;
    }

    /// <summary>12.5 → 12'-6"; to the nearest 1/16 inch.</summary>
    public static string FeetInches(double feet)
    {
        bool negative = feet < 0;
        long sixteenths = (long)Math.Round(Math.Abs(feet) * 12 * 16, MidpointRounding.AwayFromZero);
        long ft = sixteenths / (12 * 16);
        long rest = sixteenths % (12 * 16);
        long inches = rest / 16;
        long fraction = rest % 16;
        string text = $"{ft}'-{inches}";
        if (fraction > 0)
        {
            long den = 16;
            while (fraction % 2 == 0) { fraction /= 2; den /= 2; }
            text += $" {fraction}/{den}";
        }
        return (negative ? "-" : "") + text + "\"";
    }
}
