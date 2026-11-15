using System.Globalization;

namespace Nexus.Fab;

/// <summary>One line of a cut list: so many pieces of one material and size at one length.</summary>
public sealed class CutLine
{
    public string Service { get; set; } = "";
    public string Material { get; set; } = "";
    public string Size { get; set; } = "";
    public double LengthFt { get; set; }
    public int Quantity { get; set; }
    /// <summary>The spools the pieces go to ("CHWS-001, CHWS-004").</summary>
    public string Spools { get; set; } = "";
    public string Length => FabMeasure.FeetInches(LengthFt);
}

/// <summary>One stock length and the pieces cut from it.</summary>
public sealed class StockBar
{
    public string Material { get; set; } = "";
    public string Size { get; set; } = "";
    public double StockFt { get; set; }
    public List<(double LengthFt, string Spool)> Cuts { get; } = new();
    /// <summary>What is left of the bar, in feet.</summary>
    public double DropFt { get; set; }
    public string CutsText => string.Join("  +  ", Cuts.Select(c => FabMeasure.FeetInches(c.LengthFt) + (c.Spool.Length > 0 ? $" ({c.Spool})" : "")));
}

public sealed class CutPlan
{
    public List<CutLine> Lines { get; } = new();
    public List<StockBar> Bars { get; } = new();
    /// <summary>Pieces longer than their stock length: they need a joint, or longer stock.</summary>
    public List<CutLine> TooLong { get; } = new();
    public double CutFt => Bars.Sum(b => b.Cuts.Sum(c => c.LengthFt));
    public double StockFt => Bars.Sum(b => b.StockFt);
    /// <summary>Share of the stock that ends up in pieces, 0–100.</summary>
    public double YieldPercent => StockFt <= 0 ? 0 : Math.Round(CutFt / StockFt * 100, 1);
}

/// <summary>
/// The cut list of the straights (pipe, duct, containment): pieces grouped by material, size and length, and how to
/// cut them from stock lengths with the least waste (longest pieces first, each into the first bar it fits).
/// </summary>
public static class CutList
{
    /// <summary>Lengths are rounded to this (1/16 inch) so equal cuts group.</summary>
    private const double Step = 1.0 / (12 * 16);

    public static CutPlan Build(IEnumerable<FabItem> items, IReadOnlyList<StockLength> stock, double kerfIn)
    {
        var plan = new CutPlan();
        var straights = items.Where(i => i.IsStraight && i.LengthFt > 0)
            .Select(i => (Item: i, Length: Math.Round(i.LengthFt / Step, MidpointRounding.AwayFromZero) * Step)).ToList();

        foreach (var group in straights.GroupBy(s => (Key(s.Item.Service), Key(s.Item.Material), Key(s.Item.Size), s.Length))
                     .OrderBy(g => g.First().Item.Service, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.First().Item.Material, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(g => g.First().Item.Size, StringComparer.OrdinalIgnoreCase).ThenByDescending(g => g.Key.Length))
        {
            var first = group.First().Item;
            plan.Lines.Add(new CutLine
            {
                Service = first.Service, Material = first.Material, Size = first.Size, LengthFt = group.Key.Length, Quantity = group.Count(),
                Spools = string.Join(", ", group.Select(s => s.Item.Spool).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)),
            });
        }

        double kerf = Math.Max(0, kerfIn) / 12;
        foreach (var group in straights.GroupBy(s => (Key(s.Item.Material), Key(s.Item.Size)))
                     .OrderBy(g => g.First().Item.Material, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.First().Item.Size, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First().Item;
            double length = StockFor(first, stock);
            var bars = new List<(StockBar Bar, double Left)>();
            foreach (var (item, cut) in group.OrderByDescending(s => s.Length).ThenBy(s => s.Item.Spool, StringComparer.OrdinalIgnoreCase))
            {
                if (cut > length + 1e-9)
                {
                    var line = plan.TooLong.FirstOrDefault(l => l.Material == first.Material && l.Size == first.Size && Math.Abs(l.LengthFt - cut) < 1e-9);
                    if (line is null) plan.TooLong.Add(line = new CutLine { Service = item.Service, Material = first.Material, Size = first.Size, LengthFt = cut });
                    line.Quantity++;
                    continue;
                }
                // The first bar with room: a cut that uses the bar up needs no blade width after it.
                int at = bars.FindIndex(b => b.Left + 1e-9 >= cut);
                if (at < 0)
                {
                    bars.Add((new StockBar { Material = first.Material, Size = first.Size, StockFt = length }, length));
                    at = bars.Count - 1;
                }
                var (bar, left) = bars[at];
                bar.Cuts.Add((cut, item.Spool));
                bars[at] = (bar, Math.Max(0, left - cut - kerf));
            }
            foreach (var (bar, left) in bars)
            {
                bar.DropFt = Math.Round(Math.Max(0, bar.StockFt - bar.Cuts.Sum(c => c.LengthFt) - kerf * Math.Max(0, bar.Cuts.Count - 1)), 4);
                plan.Bars.Add(bar);
            }
        }
        return plan;
    }

    /// <summary>The stock length for a piece: the first entry whose material and size match (blank matches any).</summary>
    public static double StockFor(FabItem item, IReadOnlyList<StockLength> stock)
    {
        bool Matches(string rule, string value) => rule.Trim().Length == 0 || value.IndexOf(rule.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        var match = stock.FirstOrDefault(s => s.LengthFt > 0 && (s.Material.Trim().Length > 0 || s.Size.Trim().Length > 0) && Matches(s.Material, item.Material) && Matches(s.Size, item.Size))
                    ?? stock.FirstOrDefault(s => s.LengthFt > 0 && s.Material.Trim().Length == 0 && s.Size.Trim().Length == 0);
        return match?.LengthFt ?? 21;
    }

    private static string Key(string value) => value.Trim().ToUpperInvariant();
}

/// <summary>One line of a bill of material.</summary>
public sealed class BomLine
{
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string Size { get; set; } = "";
    public string Material { get; set; } = "";
    public int Quantity { get; set; }
    /// <summary>Total length of the straights, in feet; 0 for counted items.</summary>
    public double LengthFt { get; set; }
    public double WeightLb { get; set; }
    public string Length => LengthFt > 0 ? FabMeasure.FeetInches(LengthFt) : "";
    public string Weight => WeightLb > 0 ? WeightLb.ToString("0.#", CultureInfo.InvariantCulture) : "";
}

/// <summary>The bill of material of a set of items: like items on one line, straights totalled by length.</summary>
public static class Bom
{
    public static List<BomLine> Build(IEnumerable<FabItem> items) =>
        items.Where(i => i.Kind != FabKind.Other)
            .GroupBy(i => (i.Category.Trim().ToUpperInvariant(), i.Description.Trim().ToUpperInvariant(), i.Size.Trim().ToUpperInvariant(), i.Material.Trim().ToUpperInvariant()))
            .Select(g => new BomLine
            {
                Category = g.First().Category, Description = g.First().Description, Size = g.First().Size, Material = g.First().Material,
                Quantity = g.Count(), LengthFt = g.Where(i => i.IsStraight).Sum(i => i.LengthFt), WeightLb = g.Sum(i => i.WeightLb),
            })
            .OrderBy(l => l.Category, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Size, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Tables as CSV that Excel opens (UTF-8 with a byte order mark, values quoted where needed).</summary>
public static class FabCsv
{
    public static string Write(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(string.Join(",", headers.Select(Cell))).Append("\r\n");
        foreach (var row in rows) sb.Append(string.Join(",", row.Select(Cell))).Append("\r\n");
        return sb.ToString();
    }

    private static string Cell(string? value)
    {
        string v = value ?? "";
        // A leading = + - @ would run as a formula in Excel: keep it text.
        if (v.Length > 0 && "=+-@".IndexOf(v[0]) >= 0 && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
