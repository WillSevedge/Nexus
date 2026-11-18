using System.Text.RegularExpressions;

namespace Nexus.Fab;

/// <summary>One line of the parameter mapping: a value of each piece written to a Revit parameter.</summary>
public sealed class ParameterMap
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// What to write: the name of one value ("Spool", "Item Number", a custom data field, another parameter), or
    /// text with values in braces ("{Service}-{Size}").
    /// </summary>
    public string From { get; set; } = "";
    /// <summary>The Revit parameter to write it to, by name.</summary>
    public string To { get; set; } = "";
}

/// <summary>
/// Parameter mapping: fabrication values (spool, package, step, service, size, item number, custom data…) written
/// to Revit parameters, so they can be tagged, scheduled and filtered, and travel in exports. The mapping is a
/// list kept with the model; the same list always writes the same values.
/// </summary>
public static class ParameterMapper
{
    private static readonly Regex Token = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    /// <summary>Values every piece has, whichever way it was modelled.</summary>
    public static readonly string[] Standard =
    {
        "Spool", "Package", "Step", "Line", "Service", "Size", "Material", "Level", "Category", "Description", "Part Type",
        "Length", "Length Ft", "Weight", "Spool Pieces", "Spool Weight", "Spool Longest Side",
        "Package Description", "Package Destination", "Package Due",
    };

    /// <summary>Values only MEP Fabrication parts have (custom data fields come on top, by their own names).</summary>
    public static readonly string[] Fabrication =
    {
        "Item Number", "Alias", "Notes", "Product Code", "Specification", "Insulation Specification",
        "Service Name", "Service Abbreviation", "CID",
    };

    /// <summary>The value names a mapping line uses: those in braces, or the whole text when it has none.</summary>
    public static List<string> Names(string from)
    {
        string text = from.Trim();
        if (text.Length == 0) return new List<string>();
        var names = Token.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count > 0 || text.IndexOf('{') >= 0 ? names : new List<string> { text };
    }

    /// <summary>The single value a line names (no other text around it), or null: such a line can be written as a number.</summary>
    public static string? SingleName(string from)
    {
        string text = from.Trim();
        if (text.Length == 0) return null;
        if (text.IndexOf('{') < 0) return text;
        var match = Token.Match(text);
        return match.Success && match.Length == text.Length ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// The text to write for one piece. <paramref name="value"/> gives a value by name (null = the piece has none:
    /// it is left blank, and the name is added to <paramref name="missing"/>).
    /// </summary>
    public static string Resolve(string from, Func<string, string?> value, ICollection<string>? missing = null)
    {
        string text = from.Trim();
        if (text.Length == 0) return "";
        string Get(string name)
        {
            string? v = value(name);
            if (v is null && missing is not null && !missing.Contains(name)) missing.Add(name);
            return v ?? "";
        }
        if (text.IndexOf('{') < 0) return Get(text).Trim();
        return Token.Replace(text, m => Get(m.Groups[1].Value.Trim())).Trim();
    }

    /// <summary>The mapping as it should be saved: lines with both sides filled, one line per target parameter.</summary>
    public static List<ParameterMap> Clean(IEnumerable<ParameterMap> maps) =>
        maps.Where(m => m.From.Trim().Length > 0 && m.To.Trim().Length > 0)
            .GroupBy(m => m.To.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ParameterMap { Enabled = g.First().Enabled, From = g.First().From.Trim(), To = g.Key }).ToList();
}
