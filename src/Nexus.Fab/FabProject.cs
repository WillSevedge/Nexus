using System.Text.Json;

namespace Nexus.Fab;

/// <summary>A step a spool goes through (Design, Released, Cut, Shipped, Installed…), with its colour in the model.</summary>
public sealed class FabStatus
{
    public string Name { get; set; } = "";
    /// <summary>"#RRGGBB".</summary>
    public string Colour { get; set; } = "#808080";
    /// <summary>From this step on the spool is being made: re-spooling leaves its pieces alone.</summary>
    public bool Locks { get; set; }
}

/// <summary>A package: spools that are ordered, made, shipped or installed together (a shop ticket, a delivery).</summary>
public sealed class FabPackage
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Where it goes ("Level 3 east", "Shop bay 2").</summary>
    public string Destination { get; set; } = "";
    /// <summary>yyyy-MM-dd, or "".</summary>
    public string Due { get; set; } = "";
    public string Status { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    /// <summary>yyyy-MM-dd HH:mm.</summary>
    public string Created { get; set; } = "";
}

/// <summary>One change of status or package, for the history.</summary>
public sealed class FabChange
{
    /// <summary>yyyy-MM-dd HH:mm.</summary>
    public string When { get; set; } = "";
    public string Who { get; set; } = "";
    /// <summary>"Spool CHWS-012", "Package L3-East"…</summary>
    public string What { get; set; } = "";
    /// <summary>"Status", "Package".</summary>
    public string Field { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>Stock a straight is cut from: matched by material and size (blank matches any).</summary>
public sealed class StockLength
{
    public string Material { get; set; } = "";
    public string Size { get; set; } = "";
    public double LengthFt { get; set; } = 21;
}

/// <summary>How hangers are spaced, for one kind of run. The first rule that matches a run is used.</summary>
public sealed class HangerRule
{
    /// <summary>Service or system name contains this; blank = any.</summary>
    public string Service { get; set; } = "";
    /// <summary>Applies to runs up to this size in inches (diameter, or the larger side of a duct); 0 = any size.</summary>
    public double UpToSizeIn { get; set; }
    /// <summary>Most distance between hangers, in feet.</summary>
    public double SpacingFt { get; set; } = 10;
    /// <summary>A hanger within this distance of each end of a straight, in feet.</summary>
    public double FromEndFt { get; set; } = 1;
    /// <summary>The hanger to place: a family type name (Revit families) or a service button name (Fabrication).</summary>
    public string Hanger { get; set; } = "";
}

/// <summary>How sleeves are sized where runs pass through walls and floors.</summary>
public sealed class SleeveRules
{
    /// <summary>Clear space around the run (and its insulation) on each side, in inches.</summary>
    public double ClearanceIn { get; set; } = 1;
    /// <summary>Sleeve diameters are rounded up to the next of these, in inches (blank = to the next half inch).</summary>
    public List<double> SizesIn { get; set; } = new() { 2, 2.5, 3, 4, 5, 6, 8, 10, 12, 14, 16, 18, 20, 24 };
    /// <summary>How far the sleeve stands out past each face, in inches (floors often 2).</summary>
    public double ExtendIn { get; set; }
    public bool Walls { get; set; } = true;
    public bool Floors { get; set; } = true;
    /// <summary>The sleeve family type to place.</summary>
    public string Sleeve { get; set; } = "";
}

/// <summary>Everything Nexus keeps in the model about fabrication: the rules, the steps, the packages and the history.</summary>
public sealed class FabProject
{
    public List<FabStatus> Statuses { get; set; } = DefaultStatuses();
    public List<FabPackage> Packages { get; set; } = new();
    public List<FabChange> History { get; set; } = new();
    public SpoolRules Spooling { get; set; } = new();
    public List<StockLength> Stock { get; set; } = new() { new StockLength() };
    /// <summary>Lost to each cut, in inches (the blade).</summary>
    public double KerfIn { get; set; } = 0.125;
    public List<HangerRule> Hangers { get; set; } = DefaultHangers();
    public SleeveRules Sleeves { get; set; } = new();
    /// <summary>Spool sheets: the title block and view template names last used.</summary>
    public string SheetTitleBlock { get; set; } = "";
    public string SheetViewTemplate { get; set; } = "";

    public const int MaxHistory = 5000;

    public static List<FabStatus> DefaultStatuses() => new()
    {
        new FabStatus { Name = "Design", Colour = "#9AA0A6" },
        new FabStatus { Name = "Released", Colour = "#2563EB", Locks = true },
        new FabStatus { Name = "Cut", Colour = "#F59E0B", Locks = true },
        new FabStatus { Name = "Fabricated", Colour = "#8B5CF6", Locks = true },
        new FabStatus { Name = "Checked", Colour = "#14B8A6", Locks = true },
        new FabStatus { Name = "Shipped", Colour = "#F97316", Locks = true },
        new FabStatus { Name = "Installed", Colour = "#16A34A", Locks = true },
    };

    /// <summary>Common starting points (steel pipe by size, then duct); every job changes these to its spec.</summary>
    public static List<HangerRule> DefaultHangers() => new()
    {
        new HangerRule { UpToSizeIn = 1, SpacingFt = 7 },
        new HangerRule { UpToSizeIn = 2, SpacingFt = 10 },
        new HangerRule { UpToSizeIn = 4, SpacingFt = 12 },
        new HangerRule { UpToSizeIn = 0, SpacingFt = 10 },
    };

    public bool IsLocked(string status) => Statuses.Any(s => s.Locks && string.Equals(s.Name, status, StringComparison.OrdinalIgnoreCase));

    public FabPackage? Package(string name) => Packages.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Records a change (newest last), keeping the history to a size the model can carry.</summary>
    public void Record(string who, string what, string field, string from, string to, DateTime? when = null)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return;
        History.Add(new FabChange
        {
            When = (when ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            Who = who, What = what, Field = field, From = from, To = to,
        });
        if (History.Count > MaxHistory) History.RemoveRange(0, History.Count - MaxHistory);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>What was saved, or a new project when there is nothing (or it cannot be read).</summary>
    public static FabProject FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FabProject();
        try
        {
            var project = JsonSerializer.Deserialize<FabProject>(json!, Json) ?? new FabProject();
            if (project.Statuses.Count == 0) project.Statuses = DefaultStatuses();
            if (project.Stock.Count == 0) project.Stock.Add(new StockLength());
            return project;
        }
        catch (JsonException)
        {
            return new FabProject();
        }
    }
}

/// <summary>
/// What a label's code says, as text any scanner types: "NX|spool|package". Scanning it into Nexus finds the spool.
/// </summary>
public static class FabCode
{
    public const string Prefix = "NX";

    public static string For(string spool, string package) => $"{Prefix}|{spool.Replace("|", "/")}|{package.Replace("|", "/")}";

    /// <summary>The spool a scanned code names; a bare spool name (typed by hand) is taken as it is.</summary>
    public static string SpoolOf(string scanned)
    {
        string text = scanned.Trim();
        var parts = text.Split('|');
        return parts.Length >= 2 && parts[0] == Prefix ? parts[1].Trim() : text;
    }
}
