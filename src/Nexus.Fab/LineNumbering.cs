using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Fab;

/// <summary>How runs are given line numbers. Saved with the model.</summary>
public sealed class LineRules
{
    /// <summary>
    /// The Revit parameter the line number is written to (one the model has, e.g. MSUITE BIMPro's, or a new one).
    /// </summary>
    public string Parameter { get; set; } = "Nexus Line Number";
    /// <summary>Fabrication parts: also write it to this custom data field ("" = no).</summary>
    public string CustomData { get; set; } = "";
    /// <summary>"{Size}-{Service}-{###}": {Service}, {Size}, {Level}, {Material} are the line's own; # the counter.</summary>
    public string Pattern { get; set; } = "{Size}-{Service}-{###}";
    public int Start { get; set; } = 1;
    /// <summary>Each service counts from the start (CHWS-001, CHWR-001); off = one count for every service.</summary>
    public bool CountPerService { get; set; } = true;
    /// <summary>A new line where the pipe size changes (at a reducer).</summary>
    public bool BreakAtSizeChange { get; set; }
    /// <summary>The branch of a tee is a line of its own (the run goes on straight through).</summary>
    public bool BreakAtBranches { get; set; } = true;
    /// <summary>Pieces that already have a line number keep it; only the others are numbered.</summary>
    public bool KeepExisting { get; set; } = true;
    /// <summary>Pipe only (duct, conduit and cable tray get no line number).</summary>
    public bool PipeOnly { get; set; } = true;
}

/// <summary>A planned line.</summary>
public sealed class PipeLine
{
    public string Name { get; set; } = "";
    public List<FabItem> Items { get; } = new();
    public string Service { get; set; } = "";
    public string Size { get; set; } = "";
    public string Level { get; set; } = "";
    public string Material { get; set; } = "";
    public double LengthFt { get; set; }
    /// <summary>True when it was already in the model (kept, not numbered now).</summary>
    public bool Existing { get; set; }
}

/// <summary>
/// Line numbers for pipe runs: a line is what is connected, of one service, from equipment to equipment, ending where
/// the rules say (a branch, a size change). Lines already numbered can be kept; the rest are numbered by the pattern
/// in a steady order, so the same model always gives the same numbers.
/// </summary>
public static class LinePlanner
{
    public static List<PipeLine> Plan(IReadOnlyList<FabItem> items, LineRules rules)
    {
        bool Takes(FabItem i) => i.Kind is not (FabKind.Other or FabKind.Hanger)
                                 && (!rules.PipeOnly || i.Kind is not (FabKind.Duct or FabKind.Containment) && !IsDuctFitting(i));
        var all = items.Where(Takes).ToList();
        var byKey = all.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var lines = new List<PipeLine>();

        // Lines kept from the model.
        var kept = new HashSet<string>(StringComparer.Ordinal);
        if (rules.KeepExisting)
            foreach (var group in all.Where(i => i.Line.Trim().Length > 0).GroupBy(i => i.Line.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var line = new PipeLine { Name = group.First().Line.Trim(), Existing = true };
                line.Items.AddRange(group);
                Measure(line);
                lines.Add(line);
                foreach (var i in group) kept.Add(i.Key);
            }
        var free = all.Where(i => !kept.Contains(i.Key)).ToList();
        var freeKeys = new HashSet<string>(free.Select(i => i.Key), StringComparer.Ordinal);

        // Neighbours of the same service, both ways.
        var neighbours = free.ToDictionary(i => i.Key, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var a in free)
            foreach (var joint in a.Joints)
                if (joint.OtherKey != a.Key && freeKeys.Contains(joint.OtherKey) && Same(a.Service, byKey[joint.OtherKey].Service))
                {
                    if (!neighbours[a.Key].Contains(joint.OtherKey)) neighbours[a.Key].Add(joint.OtherKey);
                    if (!neighbours[joint.OtherKey].Contains(a.Key)) neighbours[joint.OtherKey].Add(a.Key);
                }

        // Where a line ends: at the branch of a tee, and where the size changes.
        var cut = new HashSet<(string, string)>();
        void Cut(string a, string b)
        {
            cut.Add((a, b));
            cut.Add((b, a));
        }
        if (rules.BreakAtBranches)
            foreach (var tee in free.Where(i => neighbours[i.Key].Count >= 3))
                foreach (var branch in Branches(tee, neighbours[tee.Key].Select(k => byKey[k]).ToList())) Cut(tee.Key, branch.Key);
        if (rules.BreakAtSizeChange)
            foreach (var a in free.Where(i => i.IsStraight))
                foreach (var b in neighbours[a.Key].Select(k => byKey[k]))
                {
                    // Straight to straight, or straight to the fitting between two sizes (a reducer): the reducer
                    // goes with the larger side.
                    if (b.IsStraight && !Same(a.Size, b.Size)) Cut(a.Key, b.Key);
                    else if (!b.IsStraight)
                    {
                        var straights = neighbours[b.Key].Select(k => byKey[k]).Where(x => x.IsStraight).ToList();
                        if (straights.Select(x => Norm(x.Size)).Distinct().Count() > 1 && SizeValue(a.Size) < straights.Max(x => SizeValue(x.Size)))
                            Cut(a.Key, b.Key);
                    }
                }

        // Connected parts, each a line.
        var done = new HashSet<string>(StringComparer.Ordinal);
        var planned = new List<PipeLine>();
        foreach (var seed in free.OrderBy(Order))
        {
            if (!done.Add(seed.Key)) continue;
            var line = new PipeLine();
            var queue = new Queue<FabItem>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                line.Items.Add(item);
                foreach (var key in neighbours[item.Key].Where(k => !cut.Contains((item.Key, k))).OrderBy(k => Order(byKey[k])))
                    if (done.Add(key)) queue.Enqueue(byKey[key]);
            }
            planned.Add(line);
        }

        // A group of fittings only (a lone elbow left between cuts) goes with a line it touches.
        foreach (var orphan in planned.Where(l => l.Items.All(i => !i.IsStraight)).ToList())
        {
            var touching = orphan.Items.SelectMany(i => neighbours[i.Key]).Select(k => planned.FirstOrDefault(l => l != orphan && l.Items.Any(i => i.Key == k)))
                .FirstOrDefault(l => l is not null && l.Items.Any(i => i.IsStraight));
            if (touching is null) continue;
            touching.Items.AddRange(orphan.Items);
            planned.Remove(orphan);
        }

        // Numbering, in a steady order.
        foreach (var line in planned) Measure(line);
        var taken = new HashSet<string>(lines.Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in planned.OrderBy(l => l.Service, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Items.Min(Order), StringComparer.Ordinal))
        {
            string counter = rules.CountPerService ? line.Service : "";
            if (!counters.TryGetValue(counter, out int n)) n = rules.Start;
            string name;
            do { name = Fill(rules.Pattern, line, n++); } while (!taken.Add(name));
            counters[counter] = n;
            line.Name = name;
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// The branch(es) of a tee: the run goes on through the two neighbours that are most nearly opposite each other
    /// (seen from the tee); a smaller neighbour is always a branch when the sizes differ.
    /// </summary>
    private static IEnumerable<FabItem> Branches(FabItem tee, List<FabItem> around)
    {
        var sizes = around.Select(i => SizeValue(i.Size)).ToList();
        (double X, double Y, double Z) Centre(FabItem i) => ((i.MinX + i.MaxX) / 2, (i.MinY + i.MaxY) / 2, (i.MinZ + i.MaxZ) / 2);
        var c = Centre(tee);
        (double, double, double) Dir(FabItem i)
        {
            var p = Centre(i);
            double dx = p.X - c.X, dy = p.Y - c.Y, dz = p.Z - c.Z, len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return len < 1e-9 ? (0, 0, 0) : (dx / len, dy / len, dz / len);
        }
        var dirs = around.Select(Dir).ToList();
        int bestA = 0, bestB = 1;
        double best = double.MaxValue;
        for (int a = 0; a < around.Count; a++)
            for (int b = a + 1; b < around.Count; b++)
            {
                // The run is the pair most nearly opposite; between equals, the pair of the larger size.
                double dot = dirs[a].Item1 * dirs[b].Item1 + dirs[a].Item2 * dirs[b].Item2 + dirs[a].Item3 * dirs[b].Item3;
                double score = dot - (sizes[a] + sizes[b]) * 1e-3;
                if (score < best) (best, bestA, bestB) = (score, a, b);
            }
        for (int i = 0; i < around.Count; i++)
            if (i != bestA && i != bestB) yield return around[i];
    }

    private static void Measure(PipeLine line)
    {
        string Most(Func<FabItem, string> value, IEnumerable<FabItem> from) =>
            from.Select(value).Where(v => v.Trim().Length > 0).GroupBy(v => v.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Key ?? "";
        var straights = line.Items.Where(i => i.IsStraight).ToList();
        var basis = straights.Count > 0 ? straights : line.Items;
        line.Service = Most(i => i.Service, line.Items);
        line.Level = Most(i => i.Level, line.Items);
        line.Material = Most(i => i.Material, basis);
        // The line's size: the largest of its straights (a line is named by its main size).
        line.Size = basis.Select(i => i.Size.Trim()).Where(s => s.Length > 0).OrderByDescending(SizeValue).ThenBy(s => s, StringComparer.Ordinal).FirstOrDefault() ?? "";
        line.LengthFt = straights.Sum(i => i.LengthFt);
    }

    /// <summary>"{Size}-{Service}-{###}" with the line's values; the counter replaces the #s, or goes at the end.</summary>
    public static string Fill(string pattern, PipeLine line, int number)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = "{Service}-{###}";
        string text = Regex.Replace(pattern, @"\{\s*(Service|Size|Level|Material)\s*\}", m => m.Groups[1].Value.ToUpperInvariant() switch
        {
            "SERVICE" => Clean(line.Service),
            "SIZE" => Clean(line.Size),
            "LEVEL" => Clean(line.Level),
            _ => Clean(line.Material),
        }, RegexOptions.IgnoreCase);
        var counter = Regex.Match(text, @"\{?(#+)\}?");
        string digits = number.ToString(CultureInfo.InvariantCulture);
        if (!counter.Success) return text + "-" + digits;
        return text.Substring(0, counter.Index) + digits.PadLeft(counter.Groups[1].Length, '0') + text.Substring(counter.Index + counter.Length);
    }

    /// <summary>The first number in a size ("4\"" → 4, "1 1/2\"" → 1.5, "100 mm" → 100); 0 when there is none.</summary>
    public static double SizeValue(string size)
    {
        var mixed = Regex.Match(size, @"(\d+)\s+(\d+)/(\d+)");
        if (mixed.Success)
            return double.Parse(mixed.Groups[1].Value, CultureInfo.InvariantCulture)
                   + double.Parse(mixed.Groups[2].Value, CultureInfo.InvariantCulture) / Math.Max(1, double.Parse(mixed.Groups[3].Value, CultureInfo.InvariantCulture));
        var fraction = Regex.Match(size, @"(\d+)/(\d+)");
        var plain = Regex.Match(size, @"\d+(?:\.\d+)?");
        if (fraction.Success && (!plain.Success || fraction.Index <= plain.Index))
            return double.Parse(fraction.Groups[1].Value, CultureInfo.InvariantCulture) / Math.Max(1, double.Parse(fraction.Groups[2].Value, CultureInfo.InvariantCulture));
        return plain.Success ? double.Parse(plain.Value, CultureInfo.InvariantCulture) : 0;
    }

    private static bool IsDuctFitting(FabItem i) => i.Category.IndexOf("Duct", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || i.Category.IndexOf("Conduit", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || i.Category.IndexOf("Cable Tray", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || i.Category.IndexOf("Containment", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Sizes in names: 4" → 4in, so the name is a good parameter value and file name.</summary>
    private static string Clean(string value)
    {
        string v = value.Trim();
        if (v.Length == 0) return "X";
        return v.Replace("\"", "in").Replace("ø", "").Replace("Ø", "").Replace(" ", "");
    }

    private static string Norm(string size) => size.Trim().ToUpperInvariant();

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Order(FabItem i) => string.Format(CultureInfo.InvariantCulture, "{0:000000000.000}|{1:000000000.000}|{2:000000000.000}|{3}",
        i.MinZ + 500000, i.MinX + 500000, i.MinY + 500000, i.Key.PadLeft(12, '0'));
}
