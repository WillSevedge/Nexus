namespace Nexus.Fab;

/// <summary>Where hangers go along a straight, and how big a sleeve is: the arithmetic behind the placement tools.</summary>
public static class Placement
{
    /// <summary>The rule for a run: the first whose service matches and whose size limit covers it.</summary>
    public static HangerRule? RuleFor(IReadOnlyList<HangerRule> rules, string service, double sizeIn)
    {
        foreach (var rule in rules)
        {
            if (rule.SpacingFt <= 0) continue;
            if (rule.Service.Trim().Length > 0 && service.IndexOf(rule.Service.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (rule.UpToSizeIn > 0 && sizeIn > rule.UpToSizeIn + 1e-9) continue;
            return rule;
        }
        return null;
    }

    /// <summary>
    /// Distances from the start of a straight where hangers go: one within <paramref name="fromEndFt"/> of each end,
    /// and as few as keep every gap within <paramref name="spacingFt"/>, evenly spread. A straight too short for two
    /// gets one in the middle; one shorter than a foot gets none (its neighbours carry it).
    /// </summary>
    public static List<double> HangerPositions(double lengthFt, double spacingFt, double fromEndFt)
    {
        var positions = new List<double>();
        if (lengthFt < 1 || spacingFt <= 0) return positions;
        double inset = Math.Max(0, Math.Min(fromEndFt, lengthFt / 2));
        double span = lengthFt - 2 * inset;
        if (span < 1)
        {
            positions.Add(lengthFt / 2);
            return positions;
        }
        int gaps = Math.Max(1, (int)Math.Ceiling(span / spacingFt - 1e-9));
        for (int i = 0; i <= gaps; i++) positions.Add(Math.Round(inset + span * i / gaps, 4));
        return positions;
    }

    /// <summary>
    /// The sleeve diameter for a run of <paramref name="outsideIn"/> (pipe or duct diagonal, with insulation): the
    /// run plus clearance on both sides, rounded up to the next listed size, or to the next half inch past the list.
    /// </summary>
    public static double SleeveDiameterIn(double outsideIn, SleeveRules rules)
    {
        double need = outsideIn + 2 * Math.Max(0, rules.ClearanceIn);
        foreach (double size in rules.SizesIn.OrderBy(s => s))
            if (size + 1e-9 >= need) return size;
        return Math.Ceiling(need * 2 - 1e-9) / 2;
    }
}
