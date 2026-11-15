using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Fab;

/// <summary>How runs are cut into spools. Saved with the model.</summary>
public sealed class SpoolRules
{
    /// <summary>A spool ends at joints of these kinds ("Flange", "Grooved", "Union"…): the joints made in the field.</summary>
    public List<string> FieldJoints { get; set; } = new();
    /// <summary>Valves, dampers and other in-line items are installed in the field: they end a spool and are not in one.</summary>
    public bool AccessoriesAreLoose { get; set; }
    /// <summary>Longest side of a spool in feet (what ships and fits through the door); 0 = no limit.</summary>
    public double MaxLengthFt { get; set; } = 20;
    /// <summary>Heaviest spool in pounds; 0 = no limit. Only counts pieces whose weight the model knows.</summary>
    public double MaxWeightLb { get; set; }
    /// <summary>Most pieces in a spool; 0 = no limit.</summary>
    public int MaxParts { get; set; }
    /// <summary>A spool never mixes services.</summary>
    public bool KeepServicesApart { get; set; } = true;
    /// <summary>A spool never crosses levels.</summary>
    public bool KeepLevelsApart { get; set; }
    /// <summary>Hangers travel with the spool they hold.</summary>
    public bool IncludeHangers { get; set; }
    /// <summary>A group smaller than this is left loose (no spool): a lone fitting is not a spool.</summary>
    public int MinParts { get; set; } = 2;
    /// <summary>"{Service}-{###}": {Service} and {Level} are the spool's own, # the counter (padded to the number of #).</summary>
    public string Name { get; set; } = "{Service}-{###}";
    public int Start { get; set; } = 1;
    /// <summary>Pieces that already have a spool keep it; only loose pieces are spooled.</summary>
    public bool KeepExisting { get; set; }
}

/// <summary>A planned spool.</summary>
public sealed class Spool
{
    public string Name { get; set; } = "";
    public List<FabItem> Items { get; } = new();
    public string Service { get; set; } = "";
    public string Level { get; set; } = "";
    /// <summary>Longest side of the box around it, in feet.</summary>
    public double LongestSideFt { get; set; }
    public double WeightLb { get; set; }
    /// <summary>Total cut length of its straights, in feet.</summary>
    public double PipeFt { get; set; }
    /// <summary>True when it was already in the model (kept, not planned now).</summary>
    public bool Existing { get; set; }
}

public sealed class SpoolPlan
{
    public List<Spool> Spools { get; } = new();
    /// <summary>Pieces left out of every spool: in-line items installed in the field, lone fittings.</summary>
    public List<FabItem> Loose { get; } = new();
    /// <summary>Spools that had to be cut because of a limit, by what cut them ("length", "weight", "parts").</summary>
    public Dictionary<string, int> Splits { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Cuts runs into spools: pieces joined in the shop stay together; a spool ends at a field joint, at a change of
/// service (or level), at equipment, and where it would get too long, too heavy or have too many pieces.
/// The same pieces and rules always give the same spools and names.
/// </summary>
public static class SpoolPlanner
{
    public static SpoolPlan Plan(IReadOnlyList<FabItem> items, SpoolRules rules)
    {
        var plan = new SpoolPlan();
        var byKey = new Dictionary<string, FabItem>(StringComparer.Ordinal);
        foreach (var item in items) byKey[item.Key] = item;

        bool InSpools(FabItem i) => i.Kind switch
        {
            FabKind.Other => false,
            FabKind.Hanger => rules.IncludeHangers,
            FabKind.Accessory => !rules.AccessoriesAreLoose,
            _ => true,
        };

        // Spools kept from the model.
        var kept = new HashSet<string>(StringComparer.Ordinal);
        if (rules.KeepExisting)
            foreach (var group in items.Where(i => i.Spool.Trim().Length > 0).GroupBy(i => i.Spool.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var spool = new Spool { Name = group.First().Spool.Trim(), Existing = true };
                spool.Items.AddRange(group);
                Measure(spool);
                plan.Spools.Add(spool);
                foreach (var i in group) kept.Add(i.Key);
            }

        var free = items.Where(i => InSpools(i) && !kept.Contains(i.Key)).OrderBy(Order).ToList();
        var freeKeys = new HashSet<string>(free.Select(i => i.Key), StringComparer.Ordinal);

        bool ShopJoint(FabItem a, FabJoint joint)
        {
            if (!freeKeys.Contains(joint.OtherKey) || !byKey.TryGetValue(joint.OtherKey, out var b)) return false;
            if (IsFieldJoint(joint.Type, rules.FieldJoints)) return false;
            if (rules.KeepServicesApart && !Same(a.Service, b.Service) && a.Kind != FabKind.Hanger && b.Kind != FabKind.Hanger) return false;
            if (rules.KeepLevelsApart && !Same(a.Level, b.Level)) return false;
            return true;
        }

        // Neighbours across shop joints, whichever side recorded the joint.
        var neighbours = free.ToDictionary(i => i.Key, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var a in free)
            foreach (var joint in a.Joints)
                if (joint.OtherKey != a.Key && ShopJoint(a, joint))
                {
                    if (!neighbours[a.Key].Contains(joint.OtherKey)) neighbours[a.Key].Add(joint.OtherKey);
                    if (!neighbours[joint.OtherKey].Contains(a.Key)) neighbours[joint.OtherKey].Add(a.Key);
                }
        // A joint is a field joint when either side says so.
        foreach (var a in free)
            foreach (var joint in a.Joints)
                if (IsFieldJoint(joint.Type, rules.FieldJoints) && neighbours.ContainsKey(joint.OtherKey))
                {
                    neighbours[a.Key].Remove(joint.OtherKey);
                    neighbours[joint.OtherKey].Remove(a.Key);
                }

        // Grow each spool from an end of a run, piece by piece, until a limit would be passed.
        var done = new HashSet<string>(StringComparer.Ordinal);
        var planned = new List<Spool>();
        foreach (var seed in free.OrderBy(i => neighbours[i.Key].Count == 1 ? 0 : 1).ThenBy(Order))
        {
            if (done.Contains(seed.Key)) continue;
            var component = new List<FabItem>();
            var queue = new Queue<FabItem>();
            queue.Enqueue(seed);
            done.Add(seed.Key);
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                component.Add(item);
                foreach (var key in neighbours[item.Key].OrderBy(k => Order(byKey[k])))
                    if (done.Add(key)) queue.Enqueue(byKey[key]);
            }
            foreach (var spool in Split(component, neighbours, byKey, rules, plan.Splits)) planned.Add(spool);
        }

        // Small leftovers are loose; the rest are named in a steady order.
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(plan.Spools.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var spool in planned.OrderBy(s => s.Service, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Level, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(s => Order(s.Items[0])))
        {
            if (spool.Items.Count < Math.Max(1, rules.MinParts))
            {
                plan.Loose.AddRange(spool.Items);
                continue;
            }
            string prefix = Fill(rules.Name, spool, 0, numbered: false);
            counters.TryGetValue(prefix, out int n);
            if (n == 0) n = rules.Start;
            string name;
            do { name = Fill(rules.Name, spool, n++, numbered: true); } while (!taken.Add(name));
            counters[prefix] = n;
            spool.Name = name;
            plan.Spools.Add(spool);
        }
        plan.Loose.AddRange(items.Where(i => !InSpools(i) && i.Kind != FabKind.Other && i.Kind != FabKind.Hanger && !kept.Contains(i.Key)));
        return plan;
    }

    /// <summary>One connected group into spools that pass the limits, each a connected part of the group.</summary>
    private static IEnumerable<Spool> Split(List<FabItem> component, Dictionary<string, List<string>> neighbours,
        Dictionary<string, FabItem> byKey, SpoolRules rules, Dictionary<string, int> splits)
    {
        var left = new HashSet<string>(component.Select(i => i.Key), StringComparer.Ordinal);
        foreach (var seed in component.OrderBy(i => neighbours[i.Key].Count(left.Contains) == 1 ? 0 : 1).ThenBy(Order))
        {
            if (!left.Contains(seed.Key)) continue;
            var spool = new Spool();
            var frontier = new List<FabItem> { seed };
            while (frontier.Count > 0)
            {
                var next = frontier[0];
                frontier.RemoveAt(0);
                if (!left.Contains(next.Key)) continue;
                string? limit = spool.Items.Count == 0 ? null : Limit(spool.Items, next, rules);
                if (limit is not null)
                {
                    splits[limit] = splits.TryGetValue(limit, out int n) ? n + 1 : 1;
                    continue; // it starts a spool of its own later
                }
                spool.Items.Add(next);
                left.Remove(next.Key);
                frontier.AddRange(neighbours[next.Key].Where(left.Contains).Select(k => byKey[k]).OrderBy(Order));
            }
            Measure(spool);
            yield return spool;
        }
    }

    /// <summary>The limit adding <paramref name="next"/> would pass ("length", "weight", "parts"), or null.</summary>
    private static string? Limit(List<FabItem> items, FabItem next, SpoolRules rules)
    {
        if (rules.MaxParts > 0 && items.Count + 1 > rules.MaxParts) return "parts";
        if (rules.MaxWeightLb > 0 && items.Sum(i => i.WeightLb) + next.WeightLb > rules.MaxWeightLb + 1e-6) return "weight";
        if (rules.MaxLengthFt > 0 && FabMeasure.LongestSideFt(items.Append(next)) > rules.MaxLengthFt + 1e-6) return "length";
        return null;
    }

    private static void Measure(Spool spool)
    {
        var main = spool.Items.Where(i => i.Kind != FabKind.Hanger).DefaultIfEmpty(spool.Items[0]).ToList();
        spool.Service = main.GroupBy(i => i.Service, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key;
        spool.Level = main.GroupBy(i => i.Level, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key;
        spool.LongestSideFt = FabMeasure.LongestSideFt(spool.Items);
        spool.WeightLb = spool.Items.Sum(i => i.WeightLb);
        spool.PipeFt = spool.Items.Where(i => i.IsStraight).Sum(i => i.LengthFt);
    }

    /// <summary>True when the joint's kind is one of the field joints (by name, or containing it: "Flange" covers "150# Flange").</summary>
    public static bool IsFieldJoint(string type, IReadOnlyCollection<string> fieldJoints)
    {
        if (type.Length == 0 || fieldJoints.Count == 0) return false;
        foreach (var f in fieldJoints)
            if (f.Trim().Length > 0 && type.IndexOf(f.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>"{Service}-{###}" with the spool's values; the counter replaces the #s, or goes at the end.</summary>
    public static string Fill(string pattern, Spool spool, int number, bool numbered)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = "SP-{###}";
        string text = Regex.Replace(pattern, @"\{\s*(Service|Level)\s*\}", m =>
            Clean(m.Groups[1].Value.Equals("Service", StringComparison.OrdinalIgnoreCase) ? spool.Service : spool.Level), RegexOptions.IgnoreCase);
        var counter = Regex.Match(text, @"\{?(#+)\}?");
        if (!numbered) return counter.Success ? text.Remove(counter.Index, counter.Length) : text;
        string digits = number.ToString(CultureInfo.InvariantCulture);
        if (!counter.Success) return text + "-" + digits;
        return text.Substring(0, counter.Index) + digits.PadLeft(counter.Groups[1].Length, '0') + text.Substring(counter.Index + counter.Length);
    }

    private static string Clean(string value) => value.Trim().Length == 0 ? "X" : value.Trim();

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A steady order: by position (low to high, then along X and Y), then by id.</summary>
    private static string Order(FabItem i) => string.Format(CultureInfo.InvariantCulture, "{0:000000000.000}|{1:000000000.000}|{2:000000000.000}|{3}",
        i.MinZ + 500000, i.MinX + 500000, i.MinY + 500000, i.Key.PadLeft(12, '0'));
}
